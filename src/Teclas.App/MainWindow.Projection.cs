using System.IO;
using System.Text;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Lyrics and chords of the songs in the current list, exported for OpenLP (OpenLyrics) and Holyrics (text).</summary>
public partial class MainWindow
{
    public sealed record ProjectionReport(string Folder, int Exported, IReadOnlyList<string> Skipped);

    private async void ProjectionExportClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        var rows = LibraryView.Cast<MidiRow>().Where(r => r.ScorePath is not null).ToList();
        if (rows.Count == 0) { Status = "Nenhuma partitura gerada na lista atual para exportar."; return; }
        await ExportProjectionAsync(rows);
    }

    /// <summary>Writes OpenLP/&lt;song&gt;.xml and Holyrics/&lt;song&gt;.txt for each song with words; songs without words are reported.</summary>
    internal async Task<ProjectionReport> ExportProjectionAsync(IReadOnlyList<MidiRow> rows)
    {
        string folder = Path.Combine(await PrepareOutputDirectoryAsync(CancellationToken.None), "Projeção");
        string openlp = Path.Combine(folder, "OpenLP"), holyrics = Path.Combine(folder, "Holyrics");
        Directory.CreateDirectory(openlp); Directory.CreateDirectory(holyrics);
        var skipped = new List<string>(); int exported = 0;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            string title = string.IsNullOrWhiteSpace(row.ScoreTitle) || row.ScoreTitle == "Sem título" ? Path.GetFileNameWithoutExtension(row.Name) : row.ScoreTitle;
            try
            {
                var model = await Task.Run(() => NativeMusicEditor.Open(row.ScorePath!, CancellationToken.None));
                var stanzas = ProjectionExport.Stanzas(ChordSheetWriter.ToChordPro(model, title, row.ScoreArtist));
                if (stanzas.Count == 0) { skipped.Add(title + " (sem letra na partitura)"); continue; }
                string stem = ScoreFileName.Stem(null, title);
                string name = stem; for (int n = 2; !taken.Add(name); n++) name = $"{stem} ({n})";
                await File.WriteAllTextAsync(Path.Combine(openlp, name + ".xml"), ProjectionExport.OpenLyrics(stanzas, title, row.ScoreArtist), new UTF8Encoding(false));
                await File.WriteAllTextAsync(Path.Combine(holyrics, name + ".txt"), ProjectionExport.Holyrics(stanzas), new UTF8Encoding(true));
                exported++;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Xml.XmlException) { skipped.Add($"{title} ({error.Message})"); }
        }
        Status = $"Projeção: {exported} música(s) exportada(s) para OpenLP e Holyrics em {folder}." + (skipped.Count > 0 ? $" Sem exportar: {string.Join("; ", skipped)}." : "");
        return new(folder, exported, skipped);
    }
}
