using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    internal Func<PracticeWindow, bool?> practiceDialog = window => window.ShowDialog();

    private async void PracticeClick(object sender, RoutedEventArgs e)
    {
        if (!CanExportPdf || CurrentScorePath is not { } scorePath) return;
        IReadOnlyList<PracticeStep>? steps = null;
        await RunOperation(async () =>
        {
            var model = await Task.Run(() => NativeMusicEditor.Open(scorePath, cancellation!.Token), cancellation!.Token);
            steps = PracticeSession.FromScore(model);
        }, operationTitle: "Preparando a prática", operationItem: Path.GetFileNameWithoutExtension(scorePath));
        if (steps is null) return;
        if (audioPlaying || countingIn) StopPlayback();
        StopTrackAudition();
        var window = new PracticeWindow(selectedFile?.ScoreTitle is { Length: > 0 } title ? title : Path.GetFileNameWithoutExtension(scorePath), steps, settings.PracticeAnyOctave) { Owner = this };
        practiceDialog(window);
        if (settings.PracticeAnyOctave != window.Session.AnyOctave) { settings.PracticeAnyOctave = window.Session.AnyOctave; PersistPreferences(); }
        Status = window.Session.Hits + window.Session.Misses == 0 ? "Prática encerrada." :
            $"Prática: {window.Session.Index} de {window.Session.Steps.Count} passos · {window.Session.Hits} acertos · {window.Session.Misses} erros.";
    }
}
