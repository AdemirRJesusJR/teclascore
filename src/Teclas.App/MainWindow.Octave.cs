using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    public bool CanShiftOctave => IsIdle && SelectedScoreExists;

    private async void RaiseOctaveClick(object sender, RoutedEventArgs e) => await ShiftOctave(12);
    private async void LowerOctaveClick(object sender, RoutedEventArgs e) => await ShiftOctave(-12);

    private async Task ShiftOctave(int semitones)
    {
        if (semitones is not (12 or -12) || !CanShiftOctave || !CommitEdits()
            || selectedFile is not { ScorePath: { } path } row) return;
        if (!File.Exists(MuseScorePath))
        { Status = "Use Preparar este computador em Configurações para localizar o MuseScore Studio 4."; return; }
        StopTrackAudition(); ResetPlayback();
        await RunOperation(async () =>
        {
            var token = cancellation!.Token;
            var progress = CreateOperationProgress();
            progress.Report("Conferindo notas e limites de altura da partitura…");
            var plan = await Task.Run(() => NativeScoreTransposition.CreatePlan(path, new(semitones), token), token);
            if (!plan.CanPrepare) throw new InvalidOperationException(string.Join(Environment.NewLine, plan.Limitations));
            var service = new NativeScoreTranspositionService(MuseScorePath, Path.Combine(LocalJobsDirectory, "octaves"));
            var prepared = await service.PrepareAsync(plan, progress, token);
            var result = await service.PublishAsync(plan, prepared, await PrepareOutputDirectoryAsync(token), progress, token);
            ApplyMusicalEditorResult(row, result);
            row.Status = (semitones > 0 ? "Uma oitava acima · " : "Uma oitava abaixo · ") + row.PageCountText;
            PersistPreferences();
            Status = "Oitava atualizada na partitura e na reprodução. A versão anterior está no Histórico.";
        }, operationTitle: semitones > 0 ? "Subindo uma oitava" : "Descendo uma oitava", operationItem: row.ScoreTitle);
    }
}
