namespace Teclas.Core;

/// <summary>One melody note in the lyrics editor and the syllable it carries (empty when none).</summary>
public sealed class LyricSlot
{
    public required string EventId { get; init; }
    public required int Measure { get; init; }
    /// <summary>Position in quarters from the start of the score (measures of the top staff added up).</summary>
    public required double Quarter { get; init; }
    public required string NoteName { get; init; }
    public string Text { get; set; } = "";
    /// <summary>The word continues on the next syllable (a hyphen under the notes).</summary>
    public bool Continues { get; set; }
    /// <summary>A rest of a beat or more comes before this note: a natural place for a new verse.</summary>
    public bool AfterRest { get; init; }
    internal string OriginalText { get; init; } = "";
    internal string OriginalSyllabic { get; init; } = "";
}

/// <summary>A verse of the editor: consecutive notes, with the words written out.</summary>
public sealed record LyricVerse(int First, int Last, string Text);

/// <summary>
/// The lyrics of a generated score as an editable list of melody notes (top staff, voice 1): correct a syllable, move it
/// to the neighbouring note, join or split syllables, or paste a whole lyric divided into syllables. The result is a
/// set of <see cref="MusicLyricChange"/> saved, like the other native edits, as a new version next to the score.
/// </summary>
public sealed class LyricsEditorDocument
{
    private readonly List<LyricSlot> slots;
    public IReadOnlyList<LyricSlot> Slots => slots;

    private LyricsEditorDocument(List<LyricSlot> slots) => this.slots = slots;

    public static LyricsEditorDocument FromModel(NativeMusicModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var top = model.Measures.Where(m => m.Events.Any(e => e.Kind == "Chord")).Select(m => m.Staff).DefaultIfEmpty(1).Min();
        double start = 0, lastEnd = 0;
        var slots = new List<LyricSlot>();
        foreach (var measure in model.Measures.Where(m => m.Staff == top).OrderBy(m => m.Number))
        {
            double length = 0;
            foreach (var item in measure.Events.Where(e => e.Voice == 1 && e.Quarter is not null).OrderBy(e => e.Quarter))
            {
                double at = start + item.Quarter!.Value;
                length = Math.Max(length, item.Quarter.Value + (item.DurationQuarter ?? 0));
                if (item.Kind != "Chord" || item.DurationQuarter is 0) continue;
                bool continues = item.LyricContinues && item.Lyric.Length > 0;
                bool previousContinues = slots.LastOrDefault(s => s.Text.Length > 0)?.Continues == true;
                slots.Add(new LyricSlot
                {
                    EventId = item.Id, Measure = item.Measure, Quarter = Math.Round(at, 4),
                    NoteName = item.Notes.Count > 0 ? item.Notes.MaxBy(n => n.Pitch)!.Name : "",
                    Text = item.Lyric, Continues = continues, AfterRest = slots.Count > 0 && at - lastEnd >= 1 - 1e-6,
                    OriginalText = item.Lyric, OriginalSyllabic = item.Lyric.Length == 0 ? "" : Syllabic(previousContinues, continues)
                });
                lastEnd = at + (item.DurationQuarter ?? 0);
            }
            start += length;
        }
        return new LyricsEditorDocument(slots);
    }

    private static string Syllabic(bool previousContinues, bool continues) =>
        (previousContinues, continues) switch { (false, false) => "single", (false, true) => "begin", (true, true) => "middle", _ => "end" };

    private void Check(int index) { if (index < 0 || index >= slots.Count) throw new ArgumentOutOfRangeException(nameof(index), "Escolha uma nota da melodia."); }

    /// <summary>Writes the syllable of a note; a final "-" means the word continues on the next syllable.</summary>
    public void SetText(int index, string text)
    {
        Check(index);
        string value = (text ?? "").Trim();
        bool continues = value.EndsWith('-');
        value = value.TrimEnd('-').Trim();
        slots[index].Text = value; slots[index].Continues = value.Length > 0 && continues;
    }

    /// <summary>Corrects a whole word starting at <paramref name="index"/>: its syllables are divided again over the same notes.</summary>
    public void SetWord(int index, string word)
    {
        Check(index);
        var notes = WordAt(index);
        var syllables = PortugueseSyllables.Split((word ?? "").Trim());
        if (syllables.Count == 0) { foreach (int i in notes) { slots[i].Text = ""; slots[i].Continues = false; } return; }
        // More syllables than notes: the last ones join on the last note of the word.
        for (int k = 0; k < notes.Count; k++)
        {
            var slot = slots[notes[k]];
            if (k >= syllables.Count) { slot.Text = ""; slot.Continues = false; continue; }
            slot.Text = k == notes.Count - 1 ? string.Concat(syllables.Skip(k)) : syllables[k];
            slot.Continues = k < Math.Min(notes.Count, syllables.Count) - 1;
        }
    }

    /// <summary>The notes of the word that has a syllable at <paramref name="index"/>.</summary>
    public IReadOnlyList<int> WordAt(int index)
    {
        Check(index);
        int first = index;
        while (first > 0 && PreviousSyllable(first) is int p && slots[p].Continues) first = p;
        var notes = new List<int> { first };
        int current = first;
        while (slots[current].Continues && NextSyllable(current) is int n) { notes.Add(n); current = n; }
        return notes;
    }

    private int? PreviousSyllable(int index) { for (int i = index - 1; i >= 0; i--) if (slots[i].Text.Length > 0) return i; return null; }
    private int? NextSyllable(int index) { for (int i = index + 1; i < slots.Count; i++) if (slots[i].Text.Length > 0) return i; return null; }

    /// <summary>
    /// Moves the syllable to the next (+1) or previous (-1) note. When that note is taken, the following syllables move
    /// along until the first free note; with no free note in that direction nothing moves.
    /// </summary>
    public void Move(int index, int direction)
    {
        Check(index);
        if (direction is not (1 or -1)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (slots[index].Text.Length == 0) throw new InvalidOperationException("Esta nota não tem sílaba para mover.");
        int free = index + direction;
        while (free >= 0 && free < slots.Count && slots[free].Text.Length > 0) free += direction;
        if (free < 0 || free >= slots.Count)
            throw new InvalidOperationException(direction > 0 ? "Não há nota livre à direita para receber a sílaba." : "Não há nota livre à esquerda para receber a sílaba.");
        for (int i = free; i != index; i -= direction)
        {
            slots[i].Text = slots[i - direction].Text; slots[i].Continues = slots[i - direction].Continues;
        }
        slots[index].Text = ""; slots[index].Continues = false;
    }

    /// <summary>Joins the syllable with the next one on the same note ("a" + "mor" → "amor"); the next note becomes free.</summary>
    public void JoinWithNext(int index)
    {
        Check(index);
        if (slots[index].Text.Length == 0 || NextSyllable(index) is not int next) throw new InvalidOperationException("Não há sílaba seguinte para juntar.");
        var slot = slots[index];
        slot.Text = slot.Continues ? slot.Text + slots[next].Text : slot.Text + " " + slots[next].Text;
        slot.Continues = slots[next].Continues;
        slots[next].Text = ""; slots[next].Continues = false;
    }

    /// <summary>Splits the syllable at a letter position; the second part goes to the next note (moving the others along).</summary>
    public void Split(int index, int position)
    {
        Check(index);
        var slot = slots[index];
        if (position <= 0 || position >= slot.Text.Length) throw new InvalidOperationException("Escolha onde dividir a sílaba, entre duas letras.");
        if (index + 1 >= slots.Count) throw new InvalidOperationException("Não há nota seguinte para receber a outra parte.");
        string first = slot.Text[..position].TrimEnd(), second = slot.Text[position..].TrimStart();
        bool sameWord = !slot.Text[position - 1].Equals(' ') && !slot.Text[position].Equals(' ');
        if (slots[index + 1].Text.Length > 0) Move(index + 1, 1);
        slots[index + 1].Text = second; slots[index + 1].Continues = slot.Continues;
        slot.Text = first; slot.Continues = sameWord;
    }

    /// <summary>Connects the syllable to the next one as one word (hyphen), or separates them as two words.</summary>
    public void ToggleHyphen(int index)
    {
        Check(index);
        if (slots[index].Text.Length == 0) throw new InvalidOperationException("Esta nota não tem sílaba.");
        if (!slots[index].Continues && NextSyllable(index) is null) throw new InvalidOperationException("Não há sílaba seguinte para ligar.");
        slots[index].Continues = !slots[index].Continues;
    }

    /// <summary>
    /// Spreads a whole lyric over the notes from <paramref name="from"/> on, one syllable per note in order, replacing
    /// what was there. A new verse waits for the next note after a rest when one comes soon. Returns the syllables that
    /// did not fit (no notes left).
    /// </summary>
    public int Paste(string lyrics, int from = 0)
    {
        if (from < 0 || from > slots.Count) throw new ArgumentOutOfRangeException(nameof(from));
        var syllables = PortugueseSyllables.FromLyrics(lyrics);
        if (syllables.Count == 0) throw new InvalidOperationException("Cole uma letra com pelo menos uma palavra.");
        for (int i = from; i < slots.Count; i++) { slots[i].Text = ""; slots[i].Continues = false; }
        int note = from, placed = 0;
        foreach (var syllable in syllables)
        {
            if (note >= slots.Count) break;
            // A verse starts after a rest when there is one within the next four notes; otherwise right away.
            if (syllable.LineStart && placed > 0 && !slots[note].AfterRest)
                for (int look = note + 1; look < Math.Min(slots.Count, note + 5); look++)
                    if (slots[look].AfterRest) { note = look; break; }
            if (note >= slots.Count) break;
            slots[note].Text = syllable.Text; slots[note].Continues = syllable.Continues;
            note++; placed++;
        }
        return syllables.Count - placed;
    }

    /// <summary>The verses as shown in the editor: a new verse after a rest of a beat or more, or after four measures.</summary>
    public IReadOnlyList<LyricVerse> Verses()
    {
        var verses = new List<LyricVerse>();
        int first = 0;
        for (int i = 1; i <= slots.Count; i++)
        {
            bool end = i == slots.Count || slots[i].AfterRest && !slots[i - 1].Continues || slots[i].Measure - slots[first].Measure >= 4 && !slots[i - 1].Continues;
            if (!end) continue;
            verses.Add(new LyricVerse(first, i - 1, Words(first, i - 1)));
            first = i;
        }
        return verses;
    }

    private string Words(int first, int last)
    {
        var text = new System.Text.StringBuilder();
        bool joined = false;
        for (int i = first; i <= last; i++)
        {
            if (slots[i].Text.Length == 0) continue;
            if (text.Length > 0 && !joined) text.Append(' ');
            text.Append(slots[i].Text); joined = slots[i].Continues;
        }
        return text.ToString();
    }

    /// <summary>The changes against the opened score, ready for <see cref="NativeMusicEditor"/>.</summary>
    public NativeMusicEdits ToEdits()
    {
        var changes = new List<MusicLyricChange>();
        bool previousContinues = false;
        foreach (var slot in slots)
        {
            string syllabic = slot.Text.Length == 0 ? "" : Syllabic(previousContinues, slot.Continues);
            if (slot.Text != slot.OriginalText || slot.Text.Length > 0 && syllabic != slot.OriginalSyllabic)
                changes.Add(new MusicLyricChange(slot.EventId, slot.Text, slot.Text.Length == 0 ? "single" : syllabic));
            if (slot.Text.Length > 0) previousContinues = slot.Continues;
        }
        return new NativeMusicEdits([], [], []) { Lyrics = changes };
    }

    public bool HasChanges => ToEdits().Lyrics.Count > 0;
}
