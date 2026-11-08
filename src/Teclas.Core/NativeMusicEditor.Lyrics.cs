using System.Xml.Linq;

namespace Teclas.Core;

/// <summary>
/// Sets the verse 1 syllable of a note: <paramref name="Text"/> empty removes it. <paramref name="Syllabic"/> is
/// single, begin, middle or end, as MuseScore writes the hyphens between syllables of a word.
/// </summary>
public sealed record MusicLyricChange(string EventId, string Text, string Syllabic);

/// <summary>Lyric edits: only the Lyrics element of verse 1 changes; notes, durations and every other element stay.</summary>
public static partial class NativeMusicEditor
{
    private static readonly HashSet<string> Syllabics = new(StringComparer.Ordinal) { "single", "begin", "middle", "end" };

    private static XElement? VerseOne(XElement chord) =>
        chord.Elements("Lyrics").FirstOrDefault(l => (string?)l.Element("no") is null or "0");

    private static bool ValidateLyrics(State state, NativeMusicEdits edits, List<string> errors)
    {
        bool changed = false;
        if (edits.Lyrics.Count > 0 && edits.EventReplacements.Count + edits.VoiceCopies.Count > 0)
        { errors.Add("Salve a letra separadamente das mudanças de ritmo e de compassos."); return false; }
        var used = new HashSet<string>(StringComparer.Ordinal);
        // A note with lyrics keeps its duration protected: the same batch cannot change both.
        var timed = edits.Durations.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var edit in edits.Lyrics)
        {
            if (timed.Contains(edit.EventId)) { errors.Add("Salve a letra separadamente das mudanças de duração da mesma nota."); continue; }
            if (!used.Add(edit.EventId) || !state.Events.TryGetValue(edit.EventId, out var target)) { errors.Add("Uma sílaba da letra é repetida ou não pertence à versão aberta."); continue; }
            if (target.View.Kind != "Chord" || IsGrace(target.Node)) { errors.Add($"Compasso {target.View.Measure}: a letra só pode ficar em notas, não em pausas ou ornamentos."); continue; }
            string global = GlobalRestriction(state.Archive);
            if (global.Length > 0) { errors.Add(global); continue; }
            string text = edit.Text ?? "";
            if (text.Length > 64 || text.Any(char.IsControl)) { errors.Add($"Compasso {target.View.Measure}: escreva uma sílaba curta, sem quebras de linha."); continue; }
            if (text.Length > 0 && !Syllabics.Contains(edit.Syllabic ?? "")) { errors.Add("A divisão da sílaba precisa ser única, início, meio ou fim de palavra."); continue; }
            var current = VerseOne(target.Node);
            string before = (string?)current?.Element("text") ?? "", syllabic = (string?)current?.Element("syllabic") ?? "single";
            changed |= text != before || text.Length > 0 && edit.Syllabic != syllabic;
        }
        return changed;
    }

    private static void ApplyLyrics(State state, NativeMusicEdits edits, CancellationToken token)
    {
        foreach (var edit in edits.Lyrics)
        {
            token.ThrowIfCancellationRequested();
            var chord = state.Events[edit.EventId].Node;
            var lyrics = VerseOne(chord);
            if (edit.Text.Length == 0) { lyrics?.Remove(); continue; }
            if (lyrics is null)
            {
                lyrics = new XElement("Lyrics");
                // MuseScore writes the lyrics after the chord's own properties and before its notes.
                if (chord.Element("Note") is { } note) note.AddBeforeSelf(lyrics); else chord.Add(lyrics);
            }
            lyrics.Element("syllabic")?.Remove();
            if (edit.Syllabic != "single")
            {
                var syllabic = new XElement("syllabic", edit.Syllabic);
                if (lyrics.Element("text") is { } existing) existing.AddBeforeSelf(syllabic);
                else if (lyrics.Element("eid") is { } eid) eid.AddAfterSelf(syllabic);
                else lyrics.AddFirst(syllabic);
            }
            if (lyrics.Element("text") is { } textElement) textElement.Value = edit.Text;
            else lyrics.Add(new XElement("text", edit.Text));
        }
    }
}
