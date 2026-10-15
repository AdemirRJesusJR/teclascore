using System.Text.Json;
using Teclas.Core;

string directory = Path.GetFullPath(args.Length > 0 ? args[0] : "voicing-tests");
Directory.CreateDirectory(directory);
var checks = new List<string>();
void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); checks.Add(description); }
bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
VoicingOccurrence Chord(string root, string quality = "", int inversion = 2, bool preserve = false, int transpose = 0, string id = "a") =>
    new(id, "Compasso 1", root, quality, inversion, preserve, transpose);

Check(KeyboardDiagramVoicing.Pitches(0, "", 0).SequenceEqual(new[] { 60, 64, 67 }), "C fundamental is C4 E4 G4");
Check(KeyboardDiagramVoicing.Pitches(0, "", 1).SequenceEqual(new[] { 52, 55, 60 }), "C first inversion uses renderer octave E3 G3 C4");
Check(KeyboardDiagramVoicing.Pitches(0, "", 2).SequenceEqual(new[] { 55, 60, 64 }), "C second inversion is G3 C4 E4");
Check(KeyboardDiagramVoicing.Pitches(0, "9", 1).SequenceEqual(new[] { 52, 55, 58, 60, 62 }), "Ninth chord keeps all five tones in renderer first inversion");
Check(KeyboardDiagramVoicing.TryRootPitchClass("F♯", out int sharp) && sharp == 6 && KeyboardDiagramVoicing.TryRootPitchClass("Gbb", out int dbl) && dbl == 5, "Accidentals and double flats resolve exactly");
Check(!KeyboardDiagramVoicing.TryRootPitchClass("C/E", out _) && !KeyboardDiagramVoicing.TryRootPitchClass("H", out _), "Slash and unsupported roots are not guessed");
Check(Throws<ArgumentOutOfRangeException>(() => KeyboardDiagramVoicing.Pitches(0, "", 3)), "Unsupported third inversion is rejected");
Check(Throws<ArgumentException>(() => KeyboardDiagramVoicing.Pitches(0, "13", 0)), "Unrecognized extensions are not reduced to another chord");

var parity = new List<object>();
foreach (int pc in Enumerable.Range(0, 12)) foreach (string quality in KeyboardDiagramVoicing.Qualities) foreach (int inv in Enumerable.Range(0, 3))
{
    int[] pitches = KeyboardDiagramVoicing.Pitches(pc, quality, inv);
    Check(pitches.SequenceEqual(pitches.Order()) && pitches.Min() >= 48 && pitches.Max() <= 71 && pitches.Distinct().Count() == pitches.Length,
        $"Exact keyboard pitches are ordered and in range: {pc} {quality} inversion {inv}");
    parity.Add(new { root = pc, quality, inversion = inv, pitches });
}
File.WriteAllText(Path.Combine(directory, "renderer-parity.json"), JsonSerializer.Serialize(parity));

var sequence = new[] { Chord("C", inversion: 0), Chord("F", inversion: 0), Chord("G", inversion: 0), Chord("C", inversion: 0) };
var plan = ComfortableVoicingService.Suggest(sequence);
Check(plan.ChangeCount > 0 && plan.AfterMovement < plan.BeforeMovement, "A fundamental-position progression receives lower-motion suggestions");
Check(sequence.All(s => s.CurrentInversion == 0), "Suggesting never mutates input positions");
Check(plan.Suggestions.All(s => s.BeforePitches.Select(p => p % 12).Order().SequenceEqual(s.AfterPitches.Select(p => p % 12).Order())), "Suggestions preserve every harmonic pitch class");
Check(ComfortableVoicingService.Suggest(sequence).Suggestions.Select(s => s.SuggestedInversion).SequenceEqual(plan.Suggestions.Select(s => s.SuggestedInversion)), "Equal inputs give deterministic proposals");
var anchored = sequence.Select((s, index) => s with { PreserveChoice = index == 1, CurrentInversion = index == 1 ? 2 : s.CurrentInversion }).ToArray();
var anchoredPlan = ComfortableVoicingService.Suggest(anchored, 7);
Check(anchoredPlan.Suggestions[1].SuggestedInversion == 2 && anchoredPlan.Suggestions[1].Limitation.Contains("preservada"), "Manual choice anchors the sequence and is never overwritten");
var allLocked = ComfortableVoicingService.Suggest(sequence.Select(s => s with { PreserveChoice = true }).ToArray());
Check(allLocked.ChangeCount == 0 && allLocked.BeforeMovement == allLocked.AfterMovement, "An entirely customized sequence remains unchanged");
var impossible = ComfortableVoicingService.Suggest([Chord("C", "maj9", 0)], 5).Suggestions.Single();
Check(!impossible.Changed && impossible.Span > 5 && impossible.Limitation.Contains("Nenhuma"), "Too-wide extended chord is retained with a limitation instead of losing notes");
var lockedWide = ComfortableVoicingService.Suggest([Chord("C", "maj7", 0, true)], 7).Suggestions.Single();
Check(!lockedWide.Changed && lockedWide.Limitation.Contains("excede"), "A manual chord exceeding requested reach is explicitly flagged");
var reachable = ComfortableVoicingService.Suggest([Chord("C", inversion: 1)], 7).Suggestions.Single();
Check(reachable.Span <= 7 && reachable.Changed, "Reach constraint finds a smaller available voicing even for a single chord");
var alreadyBest = ComfortableVoicingService.Suggest([Chord("C", inversion: 2)]);
Check(alreadyBest.ChangeCount == 0, "A single already-valid position is not changed arbitrarily");
var unknown = ComfortableVoicingService.Suggest([Chord("C/E"), Chord("C", "13")]);
Check(unknown.ChangeCount == 0 && unknown.Suggestions.All(s => s.BeforePitches.Count == 0 && s.Limitation.Length > 0), "Unsupported chords are retained with explicit unknown-span status");
Check(ComfortableVoicingService.Suggest([]).ChangeCount == 0, "Empty sequences remain harmless");
Check(Throws<ArgumentOutOfRangeException>(() => ComfortableVoicingService.Suggest(sequence, 4)) && Throws<ArgumentOutOfRangeException>(() => ComfortableVoicingService.Suggest(sequence, 13)), "Invalid reach constraints are rejected");
Check(Throws<ArgumentException>(() => ComfortableVoicingService.Suggest([Chord("C", inversion: -1)])), "Invalid original inversion is rejected");
var transposed = ComfortableVoicingService.Suggest([Chord("B", "m7", transpose: 2, preserve: true)]).Suggestions.Single();
Check(transposed.AfterPitches.SequenceEqual(KeyboardDiagramVoicing.Pitches(1, "m7", 2)), "Evaluation uses the transposed score chord, including octave wrap");
Check(ComfortableVoicingService.Movement([60, 64, 67], [60, 64, 67]) == 0 && ComfortableVoicingService.Movement([60, 64, 67], [62, 66, 69]) == 2, "Movement metric compares actual rendered pitch heights");

// Exhaustively compare the optimizer with all inversion paths for mixed chord sizes.
var small = new[] { Chord("C", "maj7", 0), Chord("D", "m7", 2), Chord("G", "9", 1), Chord("C", "", 2) };
var optimal = ComfortableVoicingService.Suggest(small, 12);
double best = double.PositiveInfinity;
for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++) for (int c = 0; c < 3; c++) for (int d = 0; d < 3; d++)
{
    int[] iv = [a, b, c, d]; var pitches = small.Select((s, i) => { KeyboardDiagramVoicing.TryRootPitchClass(s.Root, out int pc); return KeyboardDiagramVoicing.Pitches(pc, s.Quality, iv[i]); }).ToArray();
    double cost = Enumerable.Range(1, 3).Sum(i => ComfortableVoicingService.Movement(pitches[i - 1], pitches[i])); best = Math.Min(best, cost);
}
Check(Math.Abs(optimal.AfterMovement - best) < .000001, "Dynamic programming matches exhaustive global optimum across mixed chord sizes");
var withGap = ComfortableVoicingService.Suggest([Chord("C", inversion: 0), Chord("F", inversion: 0), Chord("unknown"), Chord("B", inversion: 1)]);
Check(withGap.Suggestions[^1].SuggestedInversion == 1, "Unknown chords separate optimization segments rather than inventing a transition");
File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new { status = "passed", checks, parityCases = parity.Count }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {checks.Count} voicing checks; {parity.Count} renderer parity cases exported");
