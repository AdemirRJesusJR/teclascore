using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Teclas.Core;

/// <summary>A piece of a lyric line: the chord printed above where it starts (may be empty) and the words under it.</summary>
public sealed record LyricSheetPart(string Chord, string Text)
{
    /// <summary>The word goes on in the next part (a syllable in the middle of a word).</summary>
    public bool JoinsNext { get; init; }
}

/// <summary>One line of the "Letra e cifras" page; an instrumental line has only chords.</summary>
public sealed record LyricSheetLine(IReadOnlyList<LyricSheetPart> Parts, bool StanzaStart)
{
    public bool Instrumental => Parts.All(p => p.Text.Length == 0);
    public string Words => string.Concat(Parts.Select(p => p.Text)).Trim();
}

/// <summary>A line start (score quarter from the first measure) taken from the MIDI or the engine report.</summary>
public sealed record LyricLineStart(double Quarter, bool Stanza, string Text = "");

/// <summary>
/// The whole lyric organized in lines and stanzas, with each chord above the syllable where it starts (Configurações ›
/// Página "Letra e cifras" no PDF). Lines come from the engine report (validacao.json lyrics.lines), else from the
/// MIDI's karaoke marks ("/" new line, "\" new stanza), else from the rests in the melody. Chords come from the
/// score itself, so they match what was printed (transposition and edits included).
/// </summary>
public static class PdfLyricsPage
{
    private sealed record Unit(double Quarter, string Text, bool Continues, bool GapBefore, double Gap)
    {
        public bool LineStart { get; set; }
        public bool StanzaStart { get; set; }
    }

    /// <summary>The lines for the score, or an empty list when it has no lyrics. Never throws for a missing or odd report.</summary>
    public static IReadOnlyList<LyricSheetLine> Read(string scorePath)
    {
        var model = NativeMusicEditor.Open(scorePath);
        var (starts, wordLines) = ReportLines(scorePath);
        return Build(model, starts, wordLines);
    }

    /// <summary>Lines of the score's verse 1 with the given line starts (empty: from the rests); chords from the score.</summary>
    public static IReadOnlyList<LyricSheetLine> Build(NativeMusicModel model, IReadOnlyList<LyricLineStart>? starts = null, bool textLinesWithoutNotes = true)
    {
        ArgumentNullException.ThrowIfNull(model);
        int top = LyricsEditorDocument.TopStaff(model);
        var measureStart = LyricsEditorDocument.MeasureStarts(model, top);
        var units = new List<Unit>();
        double lastEnd = 0;
        foreach (var measure in model.Measures.Where(m => m.Staff == top).OrderBy(m => m.Number))
            foreach (var item in measure.Events.Where(e => e.Voice == 1 && e.Quarter is not null).OrderBy(e => e.Quarter))
            {
                double at = measureStart[measure.Number] + item.Quarter!.Value;
                if (item.Kind == "Chord" && item.Lyric.Length > 0)
                {
                    double gap = units.Count == 0 ? 0 : at - lastEnd;
                    units.Add(new Unit(at, item.Lyric, item.LyricContinues, gap >= 1 - 1e-6, gap));
                }
                if (item.Kind == "Chord") lastEnd = at + (item.DurationQuarter ?? 0);
            }
        var harmonies = model.Measures.Where(m => m.Staff == top).SelectMany(m => m.Harmonies.Where(h => h.Quarter is not null)
            .Select(h => (Quarter: measureStart[m.Number] + h.Quarter!.Value, Label: Label(h)))).OrderBy(h => h.Quarter).ToArray();

        bool usedStarts = false;
        if (starts is { Count: > 0 } && units.Count > 0)
        {
            // A report line starts at the first syllable at (or just after) its position; enough must match to trust them.
            int matched = 0;
            var flagged = new Dictionary<int, bool>();
            foreach (var start in starts.OrderBy(s => s.Quarter))
            {
                int index = units.FindIndex(u => u.Quarter >= start.Quarter - 0.05);
                if (index < 0 || units[index].Quarter > start.Quarter + 1.01) continue;
                matched++;
                flagged[index] = flagged.TryGetValue(index, out bool stanza) ? stanza || start.Stanza : start.Stanza;
            }
            if (matched >= Math.Max(1, starts.Count / 2))
            {
                usedStarts = true;
                foreach (var (index, stanza) in flagged) { units[index].LineStart = true; units[index].StanzaStart = stanza && index > 0; }
            }
        }
        if (!usedStarts)
            foreach (var unit in units)
            {
                unit.LineStart = unit.GapBefore;
                unit.StanzaStart = unit.Gap >= 4 - 1e-6;
            }
        if (units.Count > 0) { units[0].LineStart = true; units[0].StanzaStart = false; }
        // A very long line (over four measures without a break) is split at its next rest, or at a word start.
        for (int i = 0, lineStart = 0; i < units.Count; i++)
        {
            if (units[i].LineStart) { lineStart = i; continue; }
            if (units[i].Quarter - units[lineStart].Quarter > 16 && !units[i - 1].Continues && (units[i].Gap > 0.4 || units[i].Quarter - units[lineStart].Quarter > 20))
            { units[i].LineStart = true; lineStart = i; }
        }

        // Report lines without syllables under the notes (sung by another part, or spoken): words spread over their time.
        if (textLinesWithoutNotes && starts is { Count: > 0 })
        {
            var ordered = starts.OrderBy(s => s.Quarter).ToArray();
            for (int i = 0; i < ordered.Length; i++)
            {
                var line = ordered[i];
                if (line.Text.Trim().Length == 0) continue;
                double end = i + 1 < ordered.Length ? ordered[i + 1].Quarter : line.Quarter + 8;
                if (units.Any(u => u.Quarter >= line.Quarter - 0.05 && u.Quarter < end - 0.05)) continue;
                var words = line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                double span = Math.Min(end - line.Quarter, Math.Max(2, words.Length));
                for (int w = 0; w < words.Length; w++)
                    units.Add(new Unit(line.Quarter + span * w / words.Length, words[w], false, w == 0, 0) { LineStart = w == 0, StanzaStart = w == 0 && line.Stanza });
            }
            units.Sort((a, b) => a.Quarter.CompareTo(b.Quarter));
        }
        if (units.Count == 0) return [];
        return Lines(units, harmonies);
    }

    private static string Label(MusicHarmony harmony) =>
        ChordNotation.Root(harmony.Root) + ChordNotation.Display(harmony.Quality) + (harmony.Bass is { } bass ? "/" + ChordNotation.Root(bass) : "");

    /// <summary>
    /// Each chord goes above the first syllable at or after it when that syllable comes within two beats; a change in
    /// the middle of a line goes between the syllables; chords of a stretch without words get a line of their own.
    /// </summary>
    private static IReadOnlyList<LyricSheetLine> Lines(List<Unit> units, IReadOnlyList<(double Quarter, string Label)> harmonies)
    {
        var above = new Dictionary<int, List<string>>();
        var between = new Dictionary<int, List<string>>(); // chord-only parts before a syllable, same line
        var instrumental = new Dictionary<int, List<string>>(); // instrumental line before the line of this syllable (units.Count: at the end)
        foreach (var (quarter, label) in harmonies)
        {
            int next = units.FindIndex(u => u.Quarter >= quarter - 0.01);
            int previous = next < 0 ? units.Count - 1 : next - 1;
            if (next >= 0 && units[next].Quarter - quarter <= 2 + 1e-6) Add(above, next, label);
            else if (next >= 0 && previous >= 0 && !units[next].LineStart) Add(between, next, label);
            else if (next < 0 && previous >= 0 && quarter - units[previous].Quarter <= 2) Add(between, units.Count, label);
            else Add(instrumental, next < 0 ? units.Count : LineOf(units, next), label);
        }
        var lines = new List<LyricSheetLine>();
        var parts = new List<LyricSheetPart>();
        bool stanza = false;
        void Flush() { if (parts.Count > 0) lines.Add(new LyricSheetLine(parts.ToArray(), stanza && lines.Count > 0)); parts.Clear(); stanza = false; }
        void Instrumental(int key)
        {
            if (!instrumental.TryGetValue(key, out var chords)) return;
            Flush();
            var distinct = chords.Where((c, i) => i == 0 || c != chords[i - 1]).Select(c => new LyricSheetPart(c, "")).ToArray();
            lines.Add(new LyricSheetLine(distinct, lines.Count > 0));
        }
        for (int i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            if (unit.LineStart) { Flush(); Instrumental(i); stanza = unit.StanzaStart || lines.Count > 0 && lines[^1].Instrumental; }
            if (between.TryGetValue(i, out var middle)) foreach (string chord in middle) parts.Add(new LyricSheetPart(chord, ""));
            string chordAbove = above.TryGetValue(i, out var list) ? string.Join(" ", list.Where((c, k) => k == 0 || c != list[k - 1])) : "";
            bool joins = unit.Continues && i + 1 < units.Count && !units[i + 1].LineStart;
            parts.Add(new LyricSheetPart(chordAbove, unit.Text + (joins ? "" : " ")) { JoinsNext = joins });
        }
        if (between.TryGetValue(units.Count, out var tail)) foreach (string chord in tail) parts.Add(new LyricSheetPart(chord, ""));
        Flush();
        Instrumental(units.Count);
        return lines;

        static void Add(Dictionary<int, List<string>> map, int key, string label)
        { if (!map.TryGetValue(key, out var list)) map[key] = list = []; list.Add(label); }
        static int LineOf(List<Unit> units, int index) { while (index > 0 && !units[index].LineStart) index--; return index; }
    }

    /// <summary>
    /// Line starts from the score's engine report ("&lt;name&gt; - revisao/validacao.json"): lyrics.lines when the engine
    /// wrote them, else the "/" and "\" marks of the source MIDI. Missing or unexpected data gives no starts.
    /// </summary>
    public static (IReadOnlyList<LyricLineStart> Starts, bool WordLines) ReportLines(string scorePath)
    {
        try
        {
            string report = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(scorePath))!, Path.GetFileNameWithoutExtension(scorePath) + " - revisao", "validacao.json");
            if (!File.Exists(report)) return ([], false);
            using var document = JsonDocument.Parse(File.ReadAllBytes(report));
            var root = document.RootElement;
            double origin = root.TryGetProperty("origin_removed_quarters", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetDouble() : 0;
            if (root.TryGetProperty("melody_analysis", out var analysis) && analysis.ValueKind == JsonValueKind.Object
                && analysis.TryGetProperty("lyrics", out var lyrics) && lyrics.ValueKind == JsonValueKind.Object)
            {
                var lines = FromReport(lyrics, origin);
                if (lines.Count > 0) return (lines, true);
            }
            if (root.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String && File.Exists(source.GetString()))
                return (FromMidi(MidiReader.Read(source.GetString()!), origin), false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or ArgumentException or FormatException) { }
        return ([], false);
    }

    /// <summary>lyrics.lines of the report: {tick or quarter, text/texto, paragraph/parágrafo}; read tolerantly.</summary>
    public static IReadOnlyList<LyricLineStart> FromReport(JsonElement lyrics, double originQuarters)
    {
        if (!lyrics.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array) return [];
        var result = new List<LyricLineStart>();
        int? paragraph = null;
        foreach (var line in lines.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.Object) continue;
            double? quarter = Number(line, "quarter");
            if (quarter is null) continue; // ticks alone need the MIDI's resolution, which the report does not keep
            string text = Text(line, "text") ?? Text(line, "texto") ?? "";
            int current = (int)(Number(line, "paragraph") ?? Number(line, "parágrafo") ?? Number(line, "paragrafo") ?? paragraph ?? 1);
            bool stanza = paragraph is not null && current != paragraph;
            paragraph = current;
            result.Add(new LyricLineStart(Math.Round(quarter.Value - originQuarters, 6), stanza, text));
        }
        return result;

        static double? Number(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;
        static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>
    /// Karaoke marks in the MIDI's lyric events: "/" (or a line break) starts a line, "\" starts a stanza. The mark may
    /// open the syllable, close the previous one or come as an event of its own.
    /// </summary>
    public static IReadOnlyList<LyricLineStart> FromMidi(MidiDocument midi, double originQuarters)
    {
        var chosen = LyricsTrack.SyllableEvents(midi);
        if (chosen.Count == 0) return [];
        int type = chosen[0].Type;
        var result = new List<LyricLineStart>();
        bool line = false, stanza = false, any = false;
        foreach (var item in midi.TextEvents.Where(e => e.Type == type).OrderBy(e => e.Tick))
        {
            string text = item.Text;
            if (type == 1 && text.TrimStart().StartsWith('@')) continue;
            string leading = new(text.TakeWhile(c => c is '/' or '\\' or '\r' or '\n' or ' ').ToArray());
            if (leading.Contains('\\')) stanza = true;
            if (leading.IndexOfAny(['/', '\r', '\n']) >= 0) line = true;
            string word = text.Trim('/', '\\', '\r', '\n', ' ');
            if (word.Length > 0)
            {
                if ((line || stanza) && any) result.Add(new LyricLineStart(Math.Round((double)item.Tick / midi.Ppq - originQuarters, 6), stanza));
                any = true; line = false; stanza = false;
            }
            string trailing = new(text.Reverse().TakeWhile(c => c is '/' or '\\' or '\r' or '\n' or ' ').ToArray());
            if (word.Length > 0 && trailing.Contains('\\')) stanza = true;
            if (word.Length > 0 && trailing.IndexOfAny(['/', '\r', '\n']) >= 0) line = true;
        }
        return result;
    }
}

public static partial class PdfChordPage
{
    /// <summary>Pages "Letra e cifras": each chord above its syllable, a blank space between stanzas, chords-only lines for the instrumental parts.</summary>
    private static void AddLyricsPages(IReadOnlyList<LyricSheetLine> lines, string title, string artist, double width, double height, Action<StringBuilder> addPage)
    {
        const double marginX = 50, lyricSize = 11.5, chordSize = 10, chordRow = 12, lyricRow = 15.5, lineGap = 3, stanzaGap = 10, bottom = 50;
        double maxWidth = width - 2 * marginX;
        // Visual rows: a long line wraps between parts.
        var rows = new List<(List<(double X, LyricSheetPart Part, bool Dash)> Parts, bool Stanza, bool Chords, bool Words)>();
        foreach (var line in lines)
        {
            var current = new List<(double X, LyricSheetPart Part, bool Dash)>();
            double x = 0; bool first = true;
            foreach (var part in line.Parts)
            {
                double text = TextWidth(part.Text, lyricSize, false), chord = part.Chord.Length == 0 ? 0 : TextWidth(part.Chord, chordSize, true) + 5;
                double advance = Math.Max(text, chord);
                if (x + advance > maxWidth && current.Count > 0)
                {
                    rows.Add((current, first && line.StanzaStart, current.Any(p => p.Part.Chord.Length > 0), current.Any(p => p.Part.Text.Trim().Length > 0)));
                    current = []; x = 0; first = false;
                }
                // A syllable stretched by a long chord name gets a hyphen in the gap, as chord sites print it.
                current.Add((x, part, part.JoinsNext && chord > text + 2));
                x += advance;
            }
            if (current.Count > 0) rows.Add((current, first && line.StanzaStart, current.Any(p => p.Part.Chord.Length > 0), current.Any(p => p.Part.Text.Trim().Length > 0)));
        }
        int page = 0;
        for (int index = 0; index < rows.Count; page++)
        {
            var content = new StringBuilder();
            Text(content, "F2", 15, marginX, height - 40, "Letra e cifras" + (title.Length > 0 ? " — " + title : "") + (page > 0 ? " (continuação)" : ""), "0 0 0", center: false);
            if (artist.Length > 0) Text(content, "F1", 10, marginX, height - 54, artist, "0.3 0.3 0.3", center: false);
            double y = height - 84;
            bool firstOnPage = true;
            while (index < rows.Count)
            {
                var row = rows[index];
                double needed = (firstOnPage ? 0 : row.Stanza ? stanzaGap : lineGap) + (row.Chords ? chordRow : 0) + (row.Words ? lyricRow : 0);
                if (y - needed < bottom && !firstOnPage) break;
                if (!firstOnPage) y -= row.Stanza ? stanzaGap : lineGap;
                if (row.Chords)
                {
                    y -= chordRow;
                    foreach (var (x, part, _) in row.Parts.Where(p => p.Part.Chord.Length > 0))
                        Text(content, "F2", chordSize, marginX + x, y, part.Chord, "0.15 0.3 0.75", center: false);
                }
                if (row.Words)
                {
                    y -= lyricRow;
                    foreach (var (x, part, dash) in row.Parts.Where(p => p.Part.Text.Length > 0))
                    {
                        Text(content, "F1", lyricSize, marginX + x, y, part.Text.TrimEnd(), "0 0 0", center: false);
                        if (dash) Text(content, "F1", lyricSize, marginX + x + TextWidth(part.Text, lyricSize, false) + 1, y, "-", "0.45 0.45 0.45", center: false);
                    }
                }
                else y -= 4;
                firstOnPage = false; index++;
            }
            Text(content, "F1", 8, width / 2, 28, "Acordes sobre a sílaba em que começam, conforme a partitura.", "0.4 0.4 0.4", center: true);
            addPage(content);
        }
    }
}
