using System.IO;
using System.Text;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Result of a quick repertoire: the files made and the songs left out.</summary>
public sealed record QuickSetlistResult(string? Pdf, string Text, string? StageList, double Seconds, IReadOnlyList<string> LeftOut);

/// <summary>
/// Quick repertoire for a service or show: the chosen songs in order, each in the key chosen for the day (songs whose key
/// changed, or without a score, are generated again), become one PDF, a text chord sheet for the group (also copied)
/// and a stage list in the Presentation, with the total duration.
/// </summary>
public partial class MainWindow
{
    internal Func<QuickSetlistWindow, bool?> quickSetlistDialog = window => window.ShowDialog();
    private static readonly string[] KeyNames = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];

    /// <summary>
    /// The key a song sounds in with this transposition ("G", "Em"): the analyzed key is the MIDI's own, before any
    /// transposition. Empty when the key was not analyzed.
    /// </summary>
    internal static string KeyName(MidiRow row, int transpose)
    {
        if (row.Chords.Count == 0) return "";
        int tonic = ((row.KeyFifths * 7 + (row.KeyMinor ? 9 : 0) + transpose) % 12 + 12) % 12;
        return KeyNames[tonic] + (row.KeyMinor ? "m" : "");
    }

    private async void QuickSetlistClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        var window = new QuickSetlistWindow(Files.ToList()) { Owner = this };
        if (quickSetlistDialog(window) != true || window.Entries.Count == 0) return;
        await BuildQuickSetlistAsync(window.SetlistTitle, window.Entries.Select(entry => (entry.Row, entry.Transpose)).ToList());
    }

    internal async Task<QuickSetlistResult> BuildQuickSetlistAsync(string title, IReadOnlyList<(MidiRow Row, int Transpose)> songs)
    {
        var leftOut = new List<string>();
        // 1. Songs in another key, or without a score, are generated (melody chosen only when it is clear).
        var regenerate = new List<MidiRow>();
        foreach (var (row, transpose) in songs)
        {
            if (row.ScorePath is not null && row.TransposeSemitones == transpose) continue;
            if (row.UsesNativeScoreWorkflow) { if (row.TransposeSemitones != transpose) leftOut.Add($"{row.Name}: partitura MuseScore; transponha pela prévia"); continue; }
            if (PrepareAutopilotMelody(row) is { } reason) { leftOut.Add($"{row.Name}: {reason}"); continue; }
            row.TransposeSemitones = transpose; regenerate.Add(row);
        }
        if (regenerate.Count > 0)
        {
            IReadOnlyDictionary<MidiRow, string> failures = new Dictionary<MidiRow, string>();
            await RunOperation(async () =>
            {
                Status = $"Repertório: gerando {regenerate.Count} música(s) no tom escolhido…";
                failures = autopilotGenerate is { } generate ? await generate(regenerate, cancellation!.Token) : await GenerateThroughQueueAsync(regenerate, cancellation!.Token);
            }, operationTitle: "Preparando o repertório");
            foreach (var (row, error) in failures) leftOut.Add($"{row.Name}: {error}");
        }
        var ready = songs.Select(s => s.Row).Where(r => r.ScorePath is not null && File.Exists(r.ScorePath) && !leftOut.Any(l => l.StartsWith(r.Name + ":"))).ToList();
        if (ready.Count == 0) { Status = "Nenhuma música do repertório tem partitura pronta."; return new(null, "", null, 0, leftOut); }
        string folder = Path.Combine(await PrepareOutputDirectoryAsync(CancellationToken.None), "Repertórios", ScoreFileName.Stem(null, title));
        Directory.CreateDirectory(folder);
        // 2. One PDF in order.
        string pdf = Path.Combine(folder, ScoreFileName.Stem(null, title) + ".pdf");
        await BuildRepertoirePdf(ready.Select(r => new RepertoireItem(r.ScorePath!, SongTitle(r), r.ScoreArtist ?? "", r)).ToArray(), new RepertoireOptions(title, true, false, false, false), pdf);
        // 3. The text chord sheets for the group, numbered with the key.
        var text = new StringBuilder($"{title}\n\n");
        for (int i = 0; i < ready.Count; i++)
        {
            var row = ready[i];
            string key = KeyName(row, row.TransposeSemitones);
            text.Append($"{i + 1}. {SongTitle(row)}{(key.Length > 0 ? $" (tom {key})" : "")}\n");
            try { text.Append(ChordSheetWriter.FromScore(NativeMusicEditor.Open(row.ScorePath!, CancellationToken.None), "").Trim()).Append("\n\n"); }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Xml.XmlException) { text.Append("(cifra indisponível)\n\n"); }
        }
        string sheet = text.ToString().TrimEnd() + "\n";
        File.WriteAllText(Path.Combine(folder, ScoreFileName.Stem(null, title) + " - cifras.txt"), sheet, new UTF8Encoding(true));
        try { Clipboard.SetText(sheet); } catch (System.Runtime.InteropServices.ExternalException) { }
        // 4. The stage list, with the key as a note, and the total duration.
        string? stage = null; double seconds = 0;
        var entries = ready.Select(r => new PresentationEntry(Guid.NewGuid(), SongTitle(r), r.FullPath, r.ScorePath!, r.PreviewPath,
            KeyName(r, r.TransposeSemitones) is { Length: > 0 } k ? "Tom " + k : "", 1)).ToList();
        foreach (var entry in entries) seconds += PresentationDuration(entry) ?? 0;
        if (persistenceEnabled)
        {
            var catalog = new PresentationCatalog(Path.Combine(DataFolder, "presentation.json"));
            stage = catalog.Create(title);
            var store = new PresentationStore(stage); var loaded = store.Load();
            store.Save(loaded.Document with { Title = title, Entries = entries, SelectedId = entries[0].Id });
        }
        PersistPreferences(); RefreshSelection();
        var span = TimeSpan.FromSeconds(seconds);
        Status = $"Repertório \"{title}\": {ready.Count} música(s), cerca de {(span.TotalHours >= 1 ? $"{(int)span.TotalHours}h{span.Minutes:00}" : span.TotalMinutes >= 1 ? $"{(int)Math.Round(span.TotalMinutes)} min" : $"{span.Seconds} s")}. PDF e cifras em {folder} (cifras copiadas); a lista está na Apresentação." +
                 (leftOut.Count > 0 ? $" Ficaram de fora: {string.Join("; ", leftOut)}." : "");
        return new(pdf, sheet, stage, seconds, leftOut);
    }

    private static string SongTitle(MidiRow row) => string.IsNullOrWhiteSpace(row.ScoreTitle) ? row.Name : row.ScoreTitle;
}
