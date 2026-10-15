using Teclas.Core;

internal static class GroupedChecks
{
    internal static void Run(Action<bool, string> check)
    {
        static VoicingOccurrence Chord(string root, string quality = "", int inversion = 0, bool manual = false, int transpose = 0, string id = "a") =>
            new(id, "Trecho sintético", root, quality, inversion, manual, transpose);
        static double Cost(IReadOnlyList<VoicingSuggestion> suggestions, bool after) => Enumerable.Range(1, Math.Max(0, suggestions.Count - 1)).Sum(index =>
            ComfortableVoicingService.Movement(after ? suggestions[index - 1].AfterPitches : suggestions[index - 1].BeforePitches,
                after ? suggestions[index].AfterPitches : suggestions[index].BeforePitches));
        static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }

        check(ComfortableVoicingService.GetChordGroupKey(Chord(" f♯ ", " m7 ")) == ComfortableVoicingService.GetChordGroupKey(Chord("F#", "m7")),
            "Grouped identity trims and normalizes Unicode accidentals and root case");
        check(ComfortableVoicingService.GetChordGroupKey(Chord("bB", "m")) == ComfortableVoicingService.GetChordGroupKey(Chord("B♭", "m")),
            "Grouped identity treats accidental case consistently");
        check(ComfortableVoicingService.GetChordGroupKey(Chord("C#")) != ComfortableVoicingService.GetChordGroupKey(Chord("Db")),
            "Enharmonic written chords remain different groups");
        check(ComfortableVoicingService.GetChordGroupKey(Chord("C", "m")) != ComfortableVoicingService.GetChordGroupKey(Chord("C", "M"))
            && ComfortableVoicingService.GetChordGroupKey(Chord("C", "maj7")) != ComfortableVoicingService.GetChordGroupKey(Chord("C", "Maj7")),
            "Grouped qualities remain ordinal and case sensitive");

        var sequence = new[] { Chord("C", id: "0"), Chord("F", id: "1"), Chord("G", id: "2"), Chord("C", id: "3") };
        var plan = ComfortableVoicingService.SuggestGrouped(sequence);
        check(plan.Suggestions.Count == sequence.Length && plan.Suggestions.Select(item => item.Source).SequenceEqual(sequence),
            "Grouped plan retains every occurrence in original order and keeps source identity");
        check(plan.Suggestions[0].SuggestedInversion == plan.Suggestions[3].SuggestedInversion && plan.ChangeCount > 0,
            "Repeated C receives one shared suggested inversion");
        check(plan.AfterMovement < plan.BeforeMovement && Math.Abs(plan.AfterMovement - Cost(plan.Suggestions, true)) < .000001
            && Math.Abs(plan.BeforeMovement - Cost(plan.Suggestions, false)) < .000001,
            "Grouped motion includes both original C-F and G-C transitions");
        check(sequence.All(source => source.CurrentInversion == 0 && !source.PreserveChoice), "Grouped search does not mutate original choices");
        check(ComfortableVoicingService.SuggestGrouped(sequence).Suggestions.Select(item => item.SuggestedInversion)
            .SequenceEqual(plan.Suggestions.Select(item => item.SuggestedInversion)), "Grouped search is deterministic");

        var manual = sequence.Select((source, index) => source with { PreserveChoice = index == 3 }).ToArray();
        var manualPlan = ComfortableVoicingService.SuggestGrouped(manual);
        check(manualPlan.Suggestions[0].SuggestedInversion == 0 && manualPlan.Suggestions[3].SuggestedInversion == 0
            && manualPlan.Suggestions[0].Limitation.Contains("preservada"), "One manual occurrence protects the entire matching chord group");
        var mixed = new[] { Chord("C", inversion: 0), Chord("G"), Chord(" c ", inversion: 2), Chord("F") };
        var mixedPlan = ComfortableVoicingService.SuggestGrouped(mixed);
        check(mixedPlan.Suggestions[0].SuggestedInversion == 0 && mixedPlan.Suggestions[2].SuggestedInversion == 2
            && mixedPlan.Suggestions[0].Limitation.Contains("Posições diferentes") && mixedPlan.Suggestions[2].Limitation.Contains("Posições diferentes"),
            "Different legacy inversions stay distinct with an explicit protected-group limitation");
        check(mixedPlan.Suggestions[0].AfterPitches.SequenceEqual([60, 64, 67]) && mixedPlan.Suggestions[2].AfterPitches.SequenceEqual([55, 60, 64]),
            "Mixed legacy group reports the actual different notes instead of a single invented preview");

        var normalized = ComfortableVoicingService.SuggestGrouped([Chord(" f♯ ", " m7 ", 2, transpose: -2), Chord("F#", "m7", 2, transpose: -2)]);
        check(normalized.Suggestions.All(item => item.AfterPitches.Count == 4)
            && normalized.Suggestions.Select(item => item.SuggestedInversion).Distinct().Count() == 1,
            "Normalized grouped roots and trimmed qualities use supported voicings");
        check(normalized.Suggestions.All(item => item.AfterPitches.SequenceEqual(KeyboardDiagramVoicing.Pitches(4, "m7", item.SuggestedInversion))),
            "Grouped suggestions use exact renderer pitches after negative transposition");
        var varyingTranspose = ComfortableVoicingService.SuggestGrouped([Chord("C", "m", 0, transpose: 0), Chord("G"), Chord("C", "m", 0, transpose: 2)]);
        check(varyingTranspose.Suggestions[0].SuggestedInversion == varyingTranspose.Suggestions[2].SuggestedInversion
            && varyingTranspose.Suggestions[2].AfterPitches.SequenceEqual(KeyboardDiagramVoicing.Pitches(2, "m", varyingTranspose.Suggestions[2].SuggestedInversion)),
            "Shared inversion still evaluates each occurrence in its own transposed key");

        var caseSensitive = ComfortableVoicingService.SuggestGrouped([Chord("C", "m"), Chord("C", "M")]);
        check(caseSensitive.Suggestions[0].AfterPitches.Count == 3 && caseSensitive.Suggestions[1].AfterPitches.Count == 0
            && !caseSensitive.Suggestions[1].Changed && caseSensitive.Suggestions[1].Limitation.Contains("não reconhecida"),
            "An unsupported quality is never substituted with the similarly spelled minor chord");
        var gap = ComfortableVoicingService.SuggestGrouped([Chord("C"), Chord("unknown"), Chord("F"), Chord("unknown"), Chord("C")]);
        check(gap.AfterMovement == 0 && gap.BeforeMovement == 0 && gap.ChangeCount == 0,
            "Unknown chords break motion edges and do not cause guessed transitions");

        var wide = ComfortableVoicingService.SuggestGrouped([Chord("C", "maj9", 0), Chord("C", "maj9", 0)], 5);
        check(wide.ChangeCount == 0 && wide.Suggestions.All(item => item.Span > 5 && item.Limitation.Contains("Nenhuma")),
            "Impossible grouped reach retains all notes and explains the limitation");
        var manualWide = ComfortableVoicingService.SuggestGrouped([Chord("C", "maj7", 0, true), Chord("C", "maj7", 0)], 7);
        check(manualWide.ChangeCount == 0 && manualWide.Suggestions.All(item => item.Limitation.Contains("Excede")),
            "A manually protected group exceeding reach flags every occurrence");
        var reachable = ComfortableVoicingService.SuggestGrouped([Chord("C", inversion: 1), Chord("C", inversion: 1)], 7);
        check(reachable.Suggestions.All(item => item.Span <= 7) && reachable.Suggestions.Select(item => item.SuggestedInversion).Distinct().Count() == 1,
            "A tighter feasible reach chooses one eligible inversion for every repetition");

        // Repeated transitions have their original frequency, not the order of a deduplicated list.
        var repeated = Enumerable.Range(0, 40).SelectMany(_ => new[] { Chord("C"), Chord("F"), Chord("C"), Chord("G", "7") }).ToArray();
        var repeatedPlan = ComfortableVoicingService.SuggestGrouped(repeated);
        check(repeatedPlan.Suggestions.Count == 160 && Math.Abs(repeatedPlan.AfterMovement - Cost(repeatedPlan.Suggestions, true)) < .000001,
            "All 159 repeated transitions contribute to the reported grouped movement");
        check(repeatedPlan.Suggestions.GroupBy(item => ComfortableVoicingService.GetChordGroupKey(item.Source))
            .All(group => group.Select(item => item.SuggestedInversion).Distinct().Count() == 1), "Long repeated progressions never receive conflicting group suggestions");

        string[] roots = ["C", "D", "Eb", "F", "F#", "G", "Ab", "Bb"];
        string[] qualities = ["", "m", "7", "m7", "maj9", "dim", "sus4", "aug"];
        var random = new Random(82017);
        for (int trial = 0; trial < 40; trial++)
        {
            var palette = Enumerable.Range(0, 6).Select(index => Chord(roots[(trial + index) % roots.Length], qualities[index % qualities.Length], random.Next(3))).ToArray();
            var input = Enumerable.Range(0, 35).Select(index => palette[random.Next(palette.Length)] with { Id = index.ToString() }).ToArray();
            var proposal = ComfortableVoicingService.SuggestGrouped(input);
            check(proposal.AfterMovement <= proposal.BeforeMovement + .000001
                && proposal.Suggestions.GroupBy(item => ComfortableVoicingService.GetChordGroupKey(item.Source))
                    .All(group => group.Select(item => item.SuggestedInversion).Distinct().Count() == 1)
                && proposal.Suggestions.All(item => item.BeforePitches.Select(pitch => pitch % 12).Order()
                    .SequenceEqual(item.AfterPitches.Select(pitch => pitch % 12).Order())),
                "Grouped feasible search never raises motion or changes harmony: deterministic trial " + trial);
        }
        check(ComfortableVoicingService.SuggestGrouped([]).ChangeCount == 0, "Empty grouped input remains harmless");
        check(Throws<ArgumentOutOfRangeException>(() => ComfortableVoicingService.SuggestGrouped(sequence, 4))
            && Throws<ArgumentException>(() => ComfortableVoicingService.SuggestGrouped([Chord("C", inversion: 4)]))
            && Throws<ArgumentException>(() => ComfortableVoicingService.SuggestGrouped([Chord("C", transpose: 49)])),
            "Grouped API validates reach, inversion and transposition limits");
    }
}
