using System.Runtime.CompilerServices;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>In karaoke MIDIs, points out the part that carries the sung melody when the chosen melody misses the lyrics.</summary>
public partial class MainWindow
{
    private readonly ConditionalWeakTable<MidiDocument, IReadOnlyList<LyricsCoverage>> lyricsCoverage = new();

    private IReadOnlyList<LyricsCoverage> LyricsCoverageOf(MidiRow row) =>
        row.Document is not { } document ? [] : lyricsCoverage.GetValue(document, LyricsTrack.Coverage);

    /// <summary>The source that follows the lyrics, when it is clearly better than the current melody choice.</summary>
    internal LyricsCoverage? SuggestedLyricsTrack(MidiRow row)
    {
        var coverage = LyricsCoverageOf(row);
        if (coverage.Count == 0 || row.UseReviewedProfile || row.ChordsOnly) return null;
        var best = coverage[0];
        var chosen = row.Tracks.Where(t => t.IsMelody).Select(t => t.Source.Key).ToHashSet();
        if (best.Share < .5 || chosen.Contains(best.Source)) return null;
        double current = coverage.Where(c => chosen.Contains(c.Source)).Select(c => c.Share).DefaultIfEmpty(0).Max();
        return current < .5 && best.Share > current + .2 ? best : null;
    }

    public string LyricsTrackHint
    {
        get
        {
            if (selectedFile is not { } row || SuggestedLyricsTrack(row) is not { } best) return "";
            var track = row.Tracks.FirstOrDefault(t => t.Source.Key == best.Source);
            string name = track is null ? $"pista {best.Source.Track + 1}" : $"{track.Instrument} (pista {track.Track}, canal {track.Channel})";
            var chosen = row.Tracks.Where(t => t.IsMelody).Select(t => t.Source.Key).ToHashSet();
            int current = LyricsCoverageOf(row).Where(c => chosen.Contains(c.Source)).Select(c => c.Hits).DefaultIfEmpty(0).Max();
            return $"Este MIDI tem letra, e ela acompanha {name}: {best.Hits} de {best.Syllables} sílabas caem nas notas dessa pista" +
                (chosen.Count > 0 ? $", contra {current} na melodia escolhida. Sem trocar, a maior parte da letra fica de fora da partitura." : ".");
        }
    }

    public Visibility LyricsTrackVisibility => LyricsTrackHint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UseLyricsTrackClick(object sender, RoutedEventArgs e)
    {
        if (selectedFile is not { } row || SuggestedLyricsTrack(row) is not { } best) return;
        foreach (var track in row.Tracks) track.IsMelody = track.Source.Key == best.Source;
        Status = "Melodia trocada para a pista que acompanha a letra. Analise e gere de novo para ver a letra completa.";
        NotifyLyricsTrack();
    }

    /// <summary>Marks the part that follows the lyrics in the tracks table (at least half the syllables on its notes).</summary>
    internal void MarkLyricsTrack(MidiRow row)
    {
        var coverage = LyricsCoverageOf(row);
        var best = coverage.Count > 0 && coverage[0].Share >= .5 ? coverage[0] : null;
        foreach (var track in row.Tracks)
            track.LyricsBadge = best is not null && track.Source.Key == best.Source ? $"🎤 {best.Share:P0}" : "";
    }

    private void NotifyLyricsTrack() { Notify(nameof(LyricsTrackHint)); Notify(nameof(LyricsTrackVisibility)); NotifyCollectionRule(); }
}
