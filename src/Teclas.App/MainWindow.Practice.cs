using System.IO;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

public partial class MainWindow
{
    internal Func<PracticeWindow, bool?> practiceDialog = window => window.ShowDialog();
    internal Func<StudyHistoryWindow, bool?> studyHistoryDialog = window => window.ShowDialog();
    internal IPracticeAccompaniment? practiceAccompaniment = null;

    private async void PracticeClick(object sender, RoutedEventArgs e)
    {
        if (!CanExportPdf || CurrentScorePath is not { } scorePath) return;
        IReadOnlyList<PracticeStep>? steps = null, chords = null;
        IReadOnlyDictionary<int, double>? starts = null;
        await RunOperation(async () =>
        {
            var model = await Task.Run(() => NativeMusicEditor.Open(scorePath, cancellation!.Token), cancellation!.Token);
            steps = PracticeSession.FromScore(model);
            starts = PracticeSession.MeasureStarts(model);
            chords = PracticeSession.ChordsFromScore(model, selectedFile?.EffectiveDiagramInversion ?? settings.DefaultDiagramInversion);
        }, operationTitle: "Preparando a prática", operationItem: Path.GetFileNameWithoutExtension(scorePath));
        if (steps is null || chords is null) return;
        if (audioPlaying || countingIn) StopPlayback();
        StopTrackAudition();
        string title = selectedFile?.ScoreTitle is { Length: > 0 } scoreTitle ? scoreTitle : Path.GetFileNameWithoutExtension(scorePath);
        // The score follows the practice and can play along; both need MuseScore (audio and measure map, cached per speed).
        string? preview = selectedFile?.PreviewPath is { } page && File.Exists(page) ? page : null;
        using var audio = File.Exists(MuseScorePath) && practiceAccompaniment is null
            ? new EditorScorePlayback(MuseScorePath, Path.Combine(PlaybackCacheDirectory, "pratica")) : null;
        IPracticeAccompaniment? accompaniment = practiceAccompaniment ?? (audio is null ? null : new ScorePracticeAccompaniment(scorePath, audio));
        string songKey = selectedFile?.FullPath ?? scorePath;
        var window = new PracticeWindow(title, steps, chords, settings.PracticeAnyOctave, settings.PracticeBpm, preview, accompaniment, starts)
        {
            Owner = this, DailyGoalMinutes = settings.DailyGoalMinutes, MinutesBeforeToday = StudyHistoryWindow.TodayMinutes(settings.StudyHistory),
            TempoGoal = settings.TempoGoals.TryGetValue(songKey, out int goal) ? goal : null
        };
        practiceDialog(window);
        bool changed = settings.PracticeAnyOctave != window.Session.AnyOctave || settings.PracticeBpm != (int)window.Bpm;
        settings.PracticeAnyOctave = window.Session.AnyOctave; settings.PracticeBpm = (int)window.Bpm;
        foreach (var attempt in window.Attempts) { attempt.Song = selectedFile?.FullPath ?? scorePath; attempt.Title = title; settings.StudyHistory.Add(attempt); }
        if (window.Attempts.Count > 0) { settings.StudyHistory = StudyHistory.Normalize(settings.StudyHistory); changed = true; }
        if (changed) PersistPreferences();
        Status = window.Attempts.Count == 0 ? "Prática encerrada." : window.Attempts[^1] is var last && last.Mode.EndsWith("no tempo", StringComparison.Ordinal)
            ? $"Prática no tempo: {last.Score:0} pontos · {last.Hits} acertos · {last.Misses} perdidas ou erradas · ♩ = {last.Bpm}."
            : $"Prática: {last.StepsDone} de {last.Steps} passos · {last.Hits} acertos · {last.Misses} erros.";
    }

    private void StudyHistoryClick(object sender, RoutedEventArgs e)
    {
        var window = new StudyHistoryWindow(settings.StudyHistory, () => { settings.StudyHistory.Clear(); PersistPreferences(); Status = "Histórico de estudo apagado."; },
            settings.DailyGoalMinutes, settings.TempoGoals, (daily, tempos) =>
            {
                settings.DailyGoalMinutes = daily;
                settings.TempoGoals = new Dictionary<string, int>(tempos, StringComparer.OrdinalIgnoreCase);
                PersistPreferences();
            }) { Owner = this };
        studyHistoryDialog(window);
    }
}
