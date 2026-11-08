using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Teclas.Core;

namespace Teclas.App;

/// <summary>One title that can get its accents back, chosen in the preview.</summary>
public sealed class TitleAccentChange(MidiRow row, string before, string after)
{
    public MidiRow Row { get; } = row;
    public string Before { get; } = before;
    public string After { get; } = after;
    public bool Selected { get; set; } = true;
}

/// <summary>
/// "Restaurar acentos dos títulos": new cards already get accents from the file name; existing cards only change after
/// the preview, title by title, never on their own.
/// </summary>
public partial class MainWindow
{
    internal Func<Window, bool?> titleAccentsDialog = window => window.ShowDialog();

    /// <summary>The cards whose title would change, with the restored title.</summary>
    internal IReadOnlyList<TitleAccentChange> TitleAccentChanges() => Files
        .Where(row => !string.IsNullOrWhiteSpace(row.ScoreTitle))
        .Select(row => new TitleAccentChange(row, row.ScoreTitle, PortugueseAccents.Restore(row.ScoreTitle)))
        .Where(change => change.After != change.Before).ToArray();

    private void RestoreTitleAccentsClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        var changes = TitleAccentChanges();
        if (changes.Count == 0) { Status = "Nenhum título da biblioteca precisa de acentos."; return; }
        var list = new StackPanel();
        foreach (var change in changes)
        {
            var box = new CheckBox { Content = $"{change.Before}  →  {change.After}", IsChecked = true, Margin = new Thickness(0, 2, 0, 2), Tag = change };
            box.Checked += (_, _) => change.Selected = true; box.Unchecked += (_, _) => change.Selected = false;
            AutomationProperties.SetName(box, $"{change.Before} para {change.After}");
            list.Children.Add(box);
        }
        var scroll = new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0), Name = "TitleAccentsList" };
        var window = SimpleDialog("Restaurar acentos dos títulos",
            $"{changes.Count} título(s) parecem escritos sem acento. Só palavras sem ambiguidade mudam (\"e\" continua \"e\"). Desmarque o que não quiser mudar.",
            "Aplicar", scroll);
        if (titleAccentsDialog(window) != true) return;
        ApplyTitleAccents(changes.Where(c => c.Selected));
    }

    /// <summary>Applies the chosen titles; generated scores show them when made again. Returns how many changed.</summary>
    internal int ApplyTitleAccents(IEnumerable<TitleAccentChange> changes)
    {
        int changed = 0, regenerate = 0;
        foreach (var change in changes)
        {
            var row = change.Row;
            if (row.ScoreTitle != change.Before) continue;
            if (row.ScorePath is not null && !row.UsesNativeScoreWorkflow) { RememberVersion(row); row.HasPendingScoreProperties = true; regenerate++; }
            row.ScoreTitle = change.After;
            changed++;
        }
        if (changed == 0) { Status = "Nenhum título alterado."; return 0; }
        RefreshLibraryFilters(); RefreshSelection(); PersistPreferences();
        Status = $"Acentos restaurados em {changed} título(s)." + (regenerate > 0 ? $" {regenerate} partitura(s) já gerada(s) recebem o novo título ao gerar de novo." : "");
        return changed;
    }
}
