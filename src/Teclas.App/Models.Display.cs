using System.IO;
using Teclas.Core;

namespace Teclas.App;

/// <summary>What the library list and the song header show: a clean title and artist, initials, the status group and the song's facts.</summary>
public sealed partial class MidiRow
{
    private SongMeter? meter;
    private bool meterRead;
    private bool isChecked;

    /// <summary>Marked in the library's selection mode (right-click › Selecionar); never saved.</summary>
    public bool IsChecked { get => isChecked; set => Set(ref isChecked, value); }

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
    /// <summary>Short status chip of a list row: the step progress ("0%" … "100%"), "Aprovada", "Revisada" or "Falhou".</summary>
    public string StatusShort => StatusInfo().Chip;

    private int? flowDoneSteps;
    /// <summary>Steps done in Melodia → Ritmo → Cifras → Gerar, counted like the "Progresso da partitura" panel;
    /// the main window updates it while the song is open, so the card follows every step.</summary>
    public int? FlowDoneSteps { get => flowDoneSteps; set { if (flowDoneSteps == value) return; flowDoneSteps = value; NotifyStatus(); } }

    /// <summary>25% per step done and 100% once the score exists; before the song is opened, a chosen melody with its analysis counts as the first step.</summary>
    public int ProgressPercent => ScorePath is not null ? 100 : Math.Clamp(flowDoneSteps ?? (AnalysisPath is not null ? 1 : 0), 0, 4) * 25;

    /// <summary>Tooltip of the card chip: the steps done, then the score's confidence and what to review.</summary>
    public string ProgressToolTip
    {
        get
        {
            if (Category == DashboardCategory.Error) return "A leitura ou a conversão falhou. Abra a música para ver o motivo.";
            string steps = ScorePath is not null ? "Progresso: 100%, partitura gerada (4 de 4 etapas)."
                : Category == DashboardCategory.Ready ? "Revisão aprovada: gere a partitura quando quiser."
                : $"Progresso: {ProgressPercent}%, {ProgressPercent / 25} de 4 etapas concluídas (Melodia, Ritmo, Cifras, Gerar).";
            string confidence = Confidence is { } c && !Approved ? $" Confiança da partitura: {c.Score}% ({c.Label})." : "";
            string review = ConfidenceToolTip.Length > 0 ? " " + ConfidenceToolTip : "";
            return steps + confidence + review;
        }
    }
    /// <summary>ready · review · error · accent: the chip and cover colors.</summary>
    public string StatusTone => StatusInfo().Tone;

    private (int Order, string Group, string Chip, string Tone) StatusInfo()
    {
        switch (Category)
        {
            case DashboardCategory.Error: return (4, "COM FALHA", "Falhou", "error");
            case DashboardCategory.Generated:
                if (Approved) return (3, "PRONTAS", "Aprovada", "ready");
                // The group still follows the confidence; the chip shows the progress, which is complete.
                if (Confidence is { } c) return c.Score >= 85 ? (3, "PRONTAS", "100%", "ready") : (2, "PARA CONFERIR", "100%", "review");
                return (2, "PARA CONFERIR", "100%", "review");
            case DashboardCategory.Ready: return (1, "EM PREPARO", "Revisada", "accent");
            default: return (1, "EM PREPARO", ProgressPercent + "%", "accent");
        }
    }

    private void NotifyStatus()
    {
        Notify(nameof(StatusGroupOrder)); Notify(nameof(StatusGroup)); Notify(nameof(StatusShort)); Notify(nameof(StatusTone));
        Notify(nameof(ProgressPercent)); Notify(nameof(ProgressToolTip));
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
