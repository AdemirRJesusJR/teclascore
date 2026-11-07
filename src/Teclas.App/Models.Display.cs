using System.IO;
using Teclas.Core;

namespace Teclas.App;

/// <summary>What the library list and the song header show: a clean title and artist, initials, the status group and the song's facts.</summary>
public sealed partial class MidiRow
{
    private SongMeter? meter;
    private bool meterRead;

    /// <summary>Title without the artist prefix of names like "Artista - Música.mid"; an edited title is kept as it is.</summary>
    public string DisplayTitle => SplitName().Title;

    /// <summary>The artist from the score properties, or the first part of "Artista - Música".</summary>
    public string DisplayArtist => SplitName().Artist;

    public string DisplaySubtitle
    {
        get
        {
            string artist = DisplayArtist;
            string kind = IsNativeScore ? "Partitura MuseScore" : "MIDI original";
            return artist.Length == 0 ? kind : artist + " · " + kind;
        }
    }

    public string Initials
    {
        get
        {
            var words = DisplayTitle.Split([' ', '-', '_', '.', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
                .Where(w => char.IsLetterOrDigit(w[0])).ToArray();
            string letters = words.Length switch { 0 => "♪", 1 => words[0][..Math.Min(2, words[0].Length)], _ => $"{words[0][0]}{words[1][0]}" };
            return letters.ToUpperInvariant();
        }
    }

    private (string Title, string Artist) SplitName()
    {
        if (ScoreArtist.Length > 0) return (ScoreTitle, ScoreArtist);
        string title = ScoreTitle;
        if (scoreTitle is not null) return (title, "");
        int dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash is > 0 and <= 60 && dash + 3 < title.Length)
        {
            string artist = title[..dash].Trim(), rest = title[(dash + 3)..].Trim();
            if (artist.Length > 0 && rest.Length > 0) return (rest, artist);
        }
        return (title, "");
    }

    /// <summary>Library groups, in order: Em preparo, Para conferir, Prontas, Com falha.</summary>
    public int StatusGroupOrder => StatusInfo().Order;
    public string StatusGroup => StatusInfo().Group;
    /// <summary>Short status chip of a list row.</summary>
    public string StatusShort => StatusInfo().Chip;
    /// <summary>ready · review · error · accent: the chip and cover colors.</summary>
    public string StatusTone => StatusInfo().Tone;

    private (int Order, string Group, string Chip, string Tone) StatusInfo()
    {
        switch (Category)
        {
            case DashboardCategory.Error: return (4, "COM FALHA", "Falhou", "error");
            case DashboardCategory.Generated:
                if (Approved) return (3, "PRONTAS", "Aprovada", "ready");
                if (Confidence is { } c) return c.Score >= 85 ? (3, "PRONTAS", c.Score + "%", "ready") : (2, "PARA CONFERIR", c.Score + "%", "review");
                return (2, "PARA CONFERIR", "Conferir", "review");
            case DashboardCategory.Ready: return (1, "EM PREPARO", "Revisada", "accent");
            default: return (1, "EM PREPARO", HasMelodyChoice || AnalysisPath is not null ? "Em preparo" : "Novo", "accent");
        }
    }

    private void NotifyStatus()
    {
        Notify(nameof(StatusGroupOrder)); Notify(nameof(StatusGroup)); Notify(nameof(StatusShort)); Notify(nameof(StatusTone));
    }

    private void NotifyDisplay()
    {
        Notify(nameof(DisplayTitle)); Notify(nameof(DisplayArtist)); Notify(nameof(DisplaySubtitle)); Notify(nameof(Initials));
    }

    /// <summary>Measures, tempo and duration of the MIDI (read once).</summary>
    public SongMeter? Meter
    {
        get
        {
            if (!meterRead && Document is { } document)
            {
                meterRead = true;
                try { meter = SongMeter.Of(document); } catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException) { meter = null; }
            }
            return meter;
        }
    }

    /// <summary>Song facts for the header chips: pistas, compasso, BPM, duração.</summary>
    public IReadOnlyList<string> Facts
    {
        get
        {
            var facts = new List<string>();
            if (Tracks.Count > 0) facts.Add(Tracks.Count == 1 ? "1 pista" : $"{Tracks.Count} pistas");
            if (Meter is { } m)
            {
                facts.Add(m.MeterText);
                facts.Add($"{Math.Round(m.Bpm)} BPM");
                facts.Add(m.Duration.TotalHours >= 1 ? m.Duration.ToString(@"h\:mm\:ss") : m.Duration.ToString(@"m\:ss"));
                facts.Add(m.Measures.Count == 1 ? "1 compasso" : $"{m.Measures.Count} compassos");
            }
            else if (IsNativeScore) facts.Add(PageCountText);
            return facts;
        }
    }

    public string FileToolTip => Path.GetFileName(FullPath);
}
