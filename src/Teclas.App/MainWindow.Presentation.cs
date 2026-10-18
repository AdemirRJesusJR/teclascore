using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    public bool CanOpenPresentation => IsIdle;

    private void OpenPresentationClick(object sender, RoutedEventArgs e)
    {
        if (!CanOpenPresentation) return;
        StopTrackAudition(); ResetPlayback();
        string directory = persistencePath is null ? Settings.DataDirectory : Path.GetDirectoryName(Path.GetFullPath(persistencePath))!;
        var store = persistenceEnabled ? new PresentationStore(Path.Combine(directory, "presentation.json")) : null;
        var choices = Files.Where(row => row.ScorePath is not null || row.IsNativeScore)
            .Select(row => new PresentationLibraryItem(row.FullPath, row.ScorePath ?? row.FullPath, row.ScoreTitle, row.PreviewPath)).ToArray();
        var catalog = persistenceEnabled ? new PresentationCatalog(Path.Combine(directory, "presentation.json")) : null;
        new PresentationWindow(store, choices, PreparePresentationPages, catalog, ExportPresentationPdf) { Owner = this, PageLyrics = PresentationPageLyrics }.ShowDialog();
    }

    /// <summary>Lyrics printed on each page of a song: the score's syllables placed by MuseScore's own page layout.</summary>
    internal async Task<IReadOnlyList<string>> PresentationPageLyrics(PresentationEntry item, CancellationToken token)
    {
        var syllables = KaraokeLyrics.FromScore(NativeMusicEditor.Open(item.ScorePath, token));
        if (syllables.Count == 0) return [];
        var service = new ScorePlaybackMapService(MuseScorePath, Path.Combine(PlaybackCacheDirectory, "score-maps"));
        var map = await scoreMapCache.GetOrPrepareAsync(item.ScorePath, MuseScorePath, 1, request => service.PrepareAsync(item.ScorePath, 1, null, request), null, token);
        return KaraokeLyrics.PageTexts(syllables, map.Measures);
    }

    /// <summary>A saved repertoire as one PDF, in its order; songs no longer on disk are left out and reported.</summary>
    internal async Task<string> ExportPresentationPdf(IReadOnlyList<PresentationEntry> songs, string title, string target, bool tablet)
    {
        var items = songs.Where(song => File.Exists(song.ScorePath)).Select(song =>
        {
            var row = Files.FirstOrDefault(file => string.Equals(file.FullPath, song.SourcePath, StringComparison.OrdinalIgnoreCase));
            return new RepertoireItem(song.ScorePath, song.Title, row?.ScoreArtist ?? "", row);
        }).ToArray();
        if (items.Length == 0) return "Nenhuma partitura deste repertório foi encontrada no computador.";
        await BuildRepertoirePdf(items, new RepertoireOptions(string.IsNullOrWhiteSpace(title) ? "Repertório" : title.Trim(), true, true, tablet, false), target);
        int missing = songs.Count - items.Length;
        return File.Exists(target) ? $"PDF salvo com {items.Length} música(s){(missing > 0 ? $"; {missing} sem partitura no computador ficaram de fora" : "")}: {target}" : Status;
    }

    private async Task<string> PreparePresentationPages(PresentationEntry item, IProgress<string> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(MuseScorePath)) throw new FileNotFoundException("Localize o MuseScore Studio 4 em Configurações para reconstruir as páginas.");
        progress.Report("Preparando uma cópia da partitura para a apresentação…");
        // Smoke instances without an explicit settings file must not write into
        // the real user's cache, even if a preview is requested interactively.
        string cache = persistenceEnabled ? LocalJobsDirectory : Path.Combine(Path.GetTempPath(), "TeclaScore-presentation-smoke");
        string folder = Path.Combine(cache, "presentation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string copy = Path.Combine(folder, "partitura.mscz");
        byte[] originalHash;
        await using (var source = new FileStream(item.ScorePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
        {
            originalHash = await SHA256.HashDataAsync(source, token); source.Position = 0;
            await using var destination = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await source.CopyToAsync(destination, token);
        }
        progress.Report("Renderizando todas as páginas da partitura…");
        var pages = await new ScoreExporter(MuseScorePath, folder).ExportPreviewAsync(copy, Path.Combine(folder, "pagina.png"), token);
        token.ThrowIfCancellationRequested();
        await using (var source = File.OpenRead(item.ScorePath))
            if (!(await SHA256.HashDataAsync(source, token)).SequenceEqual(originalHash))
                throw new IOException("A partitura mudou durante a preparação. Atualize as páginas novamente para acompanhar a versão correta.");
        return pages.First();
    }
}
