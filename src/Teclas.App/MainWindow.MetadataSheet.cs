using System.Windows;

namespace Teclas.App;

/// <summary>The titles sheet: edit the names of many songs at once.</summary>
public partial class MainWindow
{
    internal Func<MetadataSheetWindow, bool?> metadataSheetDialog = window => window.ShowDialog();

    private void MetadataSheetClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        var window = new MetadataSheetWindow(Files) { Owner = this };
        if (metadataSheetDialog(window) == true) ApplyMetadataSheet(window.Rows);
    }

    /// <summary>Applies the edited names; generated scores show them when made again. Returns how many songs changed.</summary>
    internal int ApplyMetadataSheet(IEnumerable<MetadataSheetRow> lines)
    {
        int changed = 0, regenerate = 0;
        foreach (var line in lines.Where(l => l.Changed))
        {
            var row = line.Row;
            bool scoreNames = line.Title != row.ScoreTitle || line.Artist != row.ScoreArtist || line.Composer != row.ScoreComposer;
            if (scoreNames && row.ScorePath is not null && !row.UsesNativeScoreWorkflow) { RememberVersion(row); row.HasPendingScoreProperties = true; regenerate++; }
            row.ScoreTitle = line.Title; row.ScoreArtist = line.Artist; row.ScoreComposer = line.Composer; row.Collection = line.Collection;
            changed++;
        }
        if (changed == 0) { Status = "Nenhum nome alterado."; return 0; }
        Notify(nameof(LibraryCollections)); RefreshLibraryFilters(); RefreshSelection();
        Status = $"{changed} música(s) atualizada(s)." + (regenerate > 0 ? $" {regenerate} partitura(s) já gerada(s) recebem os novos nomes ao gerar de novo (Piloto ou fila)." : "");
        return changed;
    }
}
