using System.Windows;
using System.Windows.Controls;
using Teclas.Core;

namespace Teclas.App;

public sealed partial class MusicEditorWindow
{
    private int comfortableVoicingSpan = 12;
    public int ComfortableDiagramMaximumSpan
    {
        get => comfortableVoicingSpan;
        set => comfortableVoicingSpan = value is 7 or 9 or 10 or 12 ? value : 12;
    }

    private Button CreateVoicingSuggestionsButton()
    {
        var result = Button("Sugerir posições confortáveis", "MusicEditorSuggestVoicings", (_, _) => SuggestNativeVoicings());
        result.ToolTip = "Comparar diagramas reconhecidos da pauta e voz selecionadas, preservando cada posição até você autorizar uma sugestão.";
        result.IsEnabled = harmonies.Any(row => row.CanEditDiagram);
        return result;
    }

    private void SuggestNativeVoicings()
    {
        if (operation is not null || closed || staff.SelectedItem is not int selectedStaff) return;
        if (!harmonyGrid.CommitEdit(DataGridEditingUnit.Cell, true) || !harmonyGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        int? voice = (harmonyGrid.SelectedItem as MusicHarmonyRow)?.Harmony.Voice ??
            harmonies.FirstOrDefault(row => row.Harmony.Staff == selectedStaff && row.CanEditDiagram)?.Harmony.Voice;
        if (voice is null) { status.Text = "Esta pauta não contém cifras com diagramas de teclado reconhecidos."; return; }
        var rows = harmonies.Where(row => row.Harmony.Staff == selectedStaff && row.Harmony.Voice == voice)
            .OrderBy(row => row.Harmony.Measure).ThenBy(row => row.Harmony.Quarter).ToArray();
        var sequence = rows.Select(row => new VoicingOccurrence(row.Harmony.Id,
            $"Pauta {selectedStaff} · voz {voice}\nCompasso {row.Harmony.Measure}",
            row.CanEditDiagram ? row.Root : "Não reconhecido", row.Quality, row.Inversion, true)).ToArray();
        var dialog = new ComfortableVoicingsWindow(sequence, comfortableVoicingSpan, allowReleasingChoices: true) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        comfortableVoicingSpan = dialog.Plan.MaximumSpan;
        var proposals = dialog.Plan.Suggestions.Where(s => s.Changed).ToDictionary(s => s.Source.Id, StringComparer.Ordinal);
        if (proposals.Count == 0) return;
        restoring = true;
        try
        {
            foreach (var row in rows)
                if (row.CanEditDiagram && proposals.TryGetValue(row.Harmony.Id, out var suggestion) && !suggestion.Source.PreserveChoice)
                    row.Inversion = suggestion.SuggestedInversion;
        }
        finally { restoring = false; }
        Changed();
        status.Text = $"{proposals.Count} posições sugeridas no rascunho da pauta {selectedStaff}, voz {voice}. Atualize a prévia para conferir; Desfazer restaura o conjunto.";
    }
}
