using System.Runtime.CompilerServices;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// In karaoke MIDIs the lyrics decide the default melody: the part they follow is suggested first, and when the chosen
/// melody misses the words the app points to it. Songs with a reviewed profile or a melody the user already chose never
/// change on their own: they get the hint and the "Usar a pista da letra" action.
/// </summary>
public partial class MainWindow
{
    private readonly ConditionalWeakTable<MidiDocument, IReadOnlyList<LyricsCoverage>> lyricsCoverage = new();

    private IReadOnlyList<LyricsCoverage> LyricsCoverageOf(MidiRow row) =>
        row.Document is not { } document ? [] : lyricsCoverage.GetValue(document, LyricsTrack.Coverage);

    /// <summary>
    /// The part that carries the lyrics: the advisor's lyrics melody (90% or more of the syllables, clearly ahead), else
    /// the part with most syllables when it covers half of them and looks like a melody. A fixed-pitch guide note or an
    /// accompaniment is never offered as the melody.
    /// </summary>
    internal LyricsCoverage? LyricsMelodyOf(MidiRow row)
    {
        var coverage = LyricsCoverageOf(row);
        if (coverage.Count == 0) return null;
        if (row.Tracks.FirstOrDefault(t => t.MelodySuggestion?.FollowsLyrics == true) is { } sung)
            return coverage.FirstOrDefault(c => c.Source == sung.Source.Key);
        return coverage.FirstOrDefault(c => c.Share >= .5 && row.Tracks.FirstOrDefault(t => t.Source.Key == c.Source) is { } track
            && !IsGuideTrack(track) && track.MelodySuggestion?.LyricsGuide != true && track.Hint is not ("Guia possível" or "Acompanhamento provável" or "Baixo provável" or "Percussão"));
    }

    /// <summary>Share of the syllables on the chosen melody (all marked tracks together); null without lyrics or melody.</summary>
    internal double? ChosenLyricsShare(MidiRow row)
    {
        if (row.Document is not { } document || LyricsCoverageOf(row).Count == 0) return null;
        var chosen = row.Tracks.Where(t => t.IsMelody).Select(t => t.Source.Key).ToArray();
        return chosen.Length == 0 ? null : LyricsTrack.CoverageOf(document, chosen)?.Share;
    }

    /// <summary>The source that follows the lyrics, when it is clearly better than the current melody choice.</summary>
    internal LyricsCoverage? SuggestedLyricsTrack(MidiRow row)
    {
        if (row.ChordsOnly || LyricsMelodyOf(row) is not { } best) return null;
        var chosen = row.Tracks.Where(t => t.IsMelody).Select(t => t.Source.Key).ToHashSet();
        if (chosen.Contains(best.Source)) return null;
        double current = ChosenLyricsShare(row) ?? 0;
        return current < .5 && best.Share > current + .2 ? best : null;
    }

    /// <summary>The Melody step must decide: the choice leaves out more than half of the words while one part has 90% of them.</summary>
    internal bool LyricsChoiceIsCritical(MidiRow row) =>
        SuggestedLyricsTrack(row) is { Share: >= .9 } && row.Tracks.Any(t => t.IsMelody) && (ChosenLyricsShare(row) ?? 0) < .5;

    private static string LyricsTrackName(MidiRow row, LyricsCoverage best)
    {
        var track = row.Tracks.FirstOrDefault(t => t.Source.Key == best.Source);
        return track is null ? $"pista {best.Source.Track + 1}" : $"{track.Instrument} (pista {track.Track}, canal {track.Channel})";
    }

    public string LyricsTrackHint
    {
        get
        {
            if (selectedFile is not { } row) return "";
            if (SuggestedLyricsTrack(row) is not { } best) return GuideOnlyLyrics(row) ? MelodySourceAdvisor.GuideOnlyMessage + " Sem a voz, a letra entra por versos sob os compassos; escolha a melodia de outra pista só se ela for a do canto." : "";
            bool chosen = row.Tracks.Any(t => t.IsMelody);
            int current = (int)Math.Round((ChosenLyricsShare(row) ?? 0) * best.Syllables);
            string kept = row.UseReviewedProfile ? " A música usa o perfil revisado e não muda sozinha: troque só se quiser." : "";
            return $"Este MIDI tem letra, e ela acompanha {LyricsTrackName(row, best)}: {best.Hits} de {best.Syllables} sílabas caem nas notas dessa pista" +
                (chosen ? $", contra {current} na melodia escolhida. Sem trocar, a maior parte da letra fica de fora da partitura." : ".") + kept;
        }
    }

    public Visibility LyricsTrackVisibility => LyricsTrackHint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The "Usar a pista da letra" button only when there is a part to switch to (not for guide notes).</summary>
    public Visibility LyricsTrackActionVisibility => selectedFile is { } row && SuggestedLyricsTrack(row) is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The words follow only guide notes and no part carries the sung melody.</summary>
    internal static bool GuideOnlyLyrics(MidiRow row) =>
        !row.ChordsOnly && row.Tracks.Any(t => t.MelodySuggestion?.LyricsGuide == true) && !row.Tracks.Any(t => t.MelodySuggestion?.FollowsLyrics == true);

    private void UseLyricsTrackClick(object sender, RoutedEventArgs e)
    {
        if (selectedFile is not { } row || SuggestedLyricsTrack(row) is not { } best) return;
        // An explicit choice of the user: a reviewed profile gives way to the free selection with the lyrics part.
        if (row.UseReviewedProfile) UseReviewedProfile = false;
        foreach (var track in row.Tracks) track.IsMelody = track.Source.Key == best.Source;
        Status = "Melodia trocada para a pista que acompanha a letra. Analise e gere de novo para ver a letra completa.";
        NotifyLyricsTrack();
    }

    /// <summary>Marks the part that follows the lyrics in the tracks table (never a guide note or an accompaniment).</summary>
    internal void MarkLyricsTrack(MidiRow row)
    {
        var best = LyricsMelodyOf(row);
        foreach (var track in row.Tracks)
            track.LyricsBadge = best is not null && track.Source.Key == best.Source ? $"🎤 {best.Share:P0}" : "";
    }

    private void NotifyLyricsTrack() { Notify(nameof(LyricsTrackHint)); Notify(nameof(LyricsTrackVisibility)); NotifyCollectionRule(); NotifyFlow(); }
}
