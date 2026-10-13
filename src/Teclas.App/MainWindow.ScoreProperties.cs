using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Teclas.App;

public partial class MainWindow
{
    public sealed record DiagramInversionOption(int Value, string Label);
    public IReadOnlyList<DiagramInversionOption> DiagramInversionOptions { get; } =
        [new(0, "Posição fundamental"), new(1, "1ª inversão"), new(2, "2ª inversão")];
    public int DefaultDiagramInversion
    {
        get => settings.DefaultDiagramInversion;
        set
        {
            int normalized = Settings.NormalizeDiagramInversion(value);
            if (settings.DefaultDiagramInversion == normalized) return;
            settings.DefaultDiagramInversion = normalized;
            Notify();
            PersistPreferences();
        }
    }
    public bool CanEditScoreProperties => IsIdle && selectedFile is not null;
    public string ScorePropertiesSummary => selectedFile is { } row
        ? row.ScoreTitle + " · " + ScoreMetadataDefaults.InversionLabel(row.EffectiveDiagramInversion)
          + (row.HasPendingScoreProperties ? " · atualização pendente" : "")
        : "Título, subtítulo, autor e inversão dos diagramas.";

    private async void ScorePropertiesClick(object sender, RoutedEventArgs e)
    {
        if (!CanEditScoreProperties || selectedFile is not { } row || !CommitEdits()) return;
        var dialog = new ScorePropertiesWindow(row.ScoreTitle, row.ScoreSubtitle, row.ScoreComposer,
            row.DiagramInversion, row.DefaultDiagramInversion) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        bool changed = row.ScoreTitle != dialog.TitleValue || row.ScoreSubtitle != dialog.SubtitleValue
            || row.ScoreComposer != dialog.ComposerValue || row.DiagramInversion != dialog.InversionOverride;
        if (!changed && !row.HasPendingScoreProperties) return;
        row.ScoreTitle = string.IsNullOrWhiteSpace(dialog.TitleValue) ? ScoreMetadataDefaults.Title(row.FullPath) : dialog.TitleValue;
        row.ScoreSubtitle = dialog.SubtitleValue;
        row.ScoreComposer = dialog.ComposerValue;
        row.DiagramInversion = dialog.InversionOverride;
        row.HasPendingScoreProperties = true;
        ResetPlayback();
        RefreshSelection();
        PersistPreferences();

        if (row.Document is null)
        {
            Status = "Propriedades salvas. Reconecte o MIDI original para atualizar a partitura.";
            return;
        }
        if (row.AnalysisPath is null && !row.UseReviewedProfile && !row.Tracks.Any(track => track.IsMelody))
        {
            Status = "Propriedades salvas. Escolha a pista de melodia para gerar a partitura.";
            return;
        }
        await RunOperation(async () =>
        {
            if (row.AnalysisPath is null || !File.Exists(row.AnalysisPath))
            {
                // Rebuilding a missing temporary analysis must not discard edited chords or key.
                var chords = row.Chords.ToArray();
                int fifths = row.KeyFifths; bool minor = row.KeyMinor;
                await Analyze(row);
                if (chords.Length > 0)
                {
                    row.Chords.Clear(); foreach (var chord in chords) row.Chords.Add(chord);
                    row.KeyFifths = fifths; row.KeyMinor = minor;
                }
            }
            await Generate(row);
            EditorTabs.SelectedIndex = 2;
        }, row);
        PersistPreferences();
    }

    private async void SaveMuseScoreClick(object sender, RoutedEventArgs e)
    {
        if (!CanExportPdf || CurrentScorePath is not { } source || !CommitEdits()) return;
        var dialog = new SaveFileDialog
        {
            Title = "Salvar partitura no formato MuseScore", Filter = "Partitura MuseScore (*.mscz)|*.mscz",
            DefaultExt = ".mscz", AddExtension = true, OverwritePrompt = true,
            FileName = Path.GetFileName(source), InitialDirectory = Path.GetDirectoryName(source)
        };
        if (dialog.ShowDialog(this) != true) return;
        await RunOperation(async () =>
        {
            await Task.Run(() => SaveMuseScoreCopy(source, dialog.FileName), cancellation!.Token);
            Status = "Partitura MuseScore salva em " + dialog.FileName;
        });
    }

    internal static void SaveMuseScoreCopy(string source, string destination)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return;
        // Copy first, then replace atomically, so a failed write leaves an existing destination intact.
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".teclascore-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { File.Copy(source, temporary); File.Move(temporary, destination, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
