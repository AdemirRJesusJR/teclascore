using System.IO;
using System.Text;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>The AI teacher: an optional Anthropic key in Settings and questions about the open song from the preview.</summary>
public partial class MainWindow
{
    internal Func<TeacherWindow, bool?> teacherDialog = window => window.ShowDialog();
    /// <summary>Replaces the Claude API in smoke tests: (song context, question) → answer.</summary>
    internal Func<string, string, CancellationToken, Task<string>>? teacherClient = null;
    /// <summary>Replaces the exercise request in smoke tests: (song context, kind) → exercise JSON.</summary>
    internal Func<string, string, CancellationToken, Task<string>>? teacherExerciseClient = null;
    internal Func<string?> teacherKey = () => TeacherKeyStore.Read();

    public bool HasTeacherKey => !string.IsNullOrWhiteSpace(teacherKey());
    public bool CanAskTeacher => CanShowChordSheetText;
    public string TeacherKeyStatus => HasTeacherKey
        ? "Chave guardada no Gerenciador de Credenciais do Windows. O botão Professor da prévia já pode perguntar."
        : "Sem chave: nada é enviado. Crie uma chave em console.anthropic.com (o uso é cobrado pela Anthropic na sua conta) e cole aqui.";

    private void SaveTeacherKeyClick(object sender, RoutedEventArgs e)
    {
        string key = TeacherKeyBox.Password.Trim();
        if (!key.StartsWith("sk-ant-", StringComparison.Ordinal) || key.Length < 30) { Status = "Isso não parece uma chave da Anthropic (começa com sk-ant-)."; return; }
        try { TeacherKeyStore.Write(key); TeacherKeyBox.Clear(); Status = "Chave do professor guardada no Windows."; }
        catch (InvalidOperationException error) { Status = error.Message; }
        NotifyTeacher();
    }

    private void RemoveTeacherKeyClick(object sender, RoutedEventArgs e)
    {
        TeacherKeyStore.Delete(); TeacherKeyBox.Clear(); Status = "Chave do professor removida deste computador."; NotifyTeacher();
    }

    private void NotifyTeacher() { Notify(nameof(HasTeacherKey)); Notify(nameof(TeacherKeyStatus)); }

    /// <summary>What the teacher knows about the song: title, chord sheet with lyrics and the practice results.</summary>
    internal static string TeacherContext(string title, string artist, string chordSheet, IEnumerable<StudyRecord> practice, string measures = "")
    {
        var text = new StringBuilder();
        text.AppendLine("Título: " + title);
        if (artist.Length > 0) text.AppendLine("Artista: " + artist);
        string sheet = chordSheet.Length > 12000 ? chordSheet[..12000] + "\n[cifra cortada]" : chordSheet;
        text.AppendLine().AppendLine("Cifra (acordes sobre a letra; compassos em ordem):").AppendLine(sheet.TrimEnd());
        if (measures.Length > 0) text.AppendLine().AppendLine("Compassos (número: acordes | notas da melodia com duração em tempos):").AppendLine(measures.TrimEnd());
        var attempts = practice.OrderByDescending(r => r.Date).Take(12).ToList();
        text.AppendLine().AppendLine(attempts.Count == 0 ? "Prática: o aluno ainda não praticou esta música no TeclaScore." : "Últimas práticas (mais recente primeiro):");
        foreach (var r in attempts)
            text.AppendLine($"- {r.Date:dd/MM} modo {r.Mode}{(r.Bpm is { } bpm ? $" a {bpm} bpm" : "")}: {r.Score:0}% de acerto, {r.StepsDone}/{r.Steps} passos, {r.Misses} erros{(r.Completed ? ", até o fim" : "")}");
        return text.ToString();
    }

    /// <summary>Each measure of the first staff: chord symbols and the melody notes with their length in beats (up to 300 measures).</summary>
    internal static string MeasureSummary(NativeMusicModel model)
    {
        var text = new StringBuilder();
        foreach (var measure in model.Measures.Where(m => m.Staff == model.Measures.Min(x => x.Staff)).OrderBy(m => m.Number).Take(300))
        {
            string chords = string.Join(" ", measure.Harmonies.Where(h => h.Root.Length > 0).Select(h => h.Root + h.Quality + (h.Bass is { } bass ? "/" + bass : "")));
            string notes = string.Join(" ", measure.Events.Where(e => e.Voice <= 1 && e.Quarter is not null).OrderBy(e => e.Quarter).Select(e =>
                (e.Notes.Count == 0 ? "pausa" : string.Join("+", e.Notes.Select(n => PracticeSession.NoteName(n.Pitch)))) +
                (e.DurationQuarter is { } d ? $"({d:0.##})" : "")));
            text.AppendLine($"{measure.Number}: {(chords.Length > 0 ? chords : "-")} | {(notes.Length > 0 ? notes : "-")}");
        }
        return text.ToString();
    }

    /// <summary>Turns the teacher's exercise into a library song (MIDI in the output folder, right hand as the melody).</summary>
    private async Task<string> CreateExerciseAsync(string json, CancellationToken token)
    {
        var exercise = TeacherExercise.Parse(json);
        string folder = Path.Combine(await PrepareOutputDirectoryAsync(token), "Exercícios do professor");
        Directory.CreateDirectory(folder);
        var taken = new HashSet<string>(Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        string midi = Path.Combine(folder, ScoreFileName.Unique(ScoreFileName.Stem(null, exercise.Title), ".mid", taken));
        await File.WriteAllBytesAsync(midi, exercise.ToMidi(), token);
        await ImportAndOpenLibraryAsync([midi]);
        foreach (var row in Files.Where(r => string.Equals(r.FullPath, midi, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var track in row.Tracks) track.IsMelody = track.Source.Key.Track == 1;
            row.Collection = "Exercícios do professor"; row.ScoreTitle = exercise.Title;
        }
        RefreshSelection(); PersistPreferences();
        return $"Exercício “{exercise.Title}” criado na biblioteca (coleção Exercícios do professor). Gere a partitura e pratique a ♩ = {exercise.Bpm:0}. {exercise.Explanation}".Trim();
    }

    private async void TeacherClick(object sender, RoutedEventArgs e)
    {
        if (!CanAskTeacher || selectedFile is not { } row || CurrentScorePath is not { } score) return;
        string? key = teacherKey();
        if (teacherClient is null && string.IsNullOrWhiteSpace(key))
        {
            Status = "O professor precisa de uma chave da Anthropic. Cole a sua em Configurações › Professor com IA.";
            SetCurrentPage("settings"); return;
        }
        string title = string.IsNullOrWhiteSpace(row.ScoreTitle) ? row.Name : row.ScoreTitle;
        try
        {
            var (sheet, measures, count) = await Task.Run(() =>
            {
                var model = NativeMusicEditor.Open(score, CancellationToken.None);
                return (ChordSheetWriter.FromScore(model, row.ScoreTitle), MeasureSummary(model), model.Measures.Select(m => m.Number).DefaultIfEmpty(0).Max());
            });
            string song = row.FullPath ?? score;
            string context = TeacherContext(title, row.ScoreArtist ?? "", sheet, settings.StudyHistory.Where(r => string.Equals(r.Song, song, StringComparison.OrdinalIgnoreCase)), measures);
            Func<string, Action<string>, CancellationToken, Task<string>> ask;
            Func<string, CancellationToken, Task<string>> exerciseJson;
            if (teacherClient is { } stub)
            {
                ask = async (q, onText, token) => { string answer = await stub(context, q, token); onText(answer); return answer; };
                exerciseJson = (kind, token) => teacherExerciseClient?.Invoke(context, kind, token) ?? Task.FromResult("{}");
            }
            else { var teacher = new AiTeacher(key!, context); ask = teacher.AskAsync; exerciseJson = teacher.ExerciseJsonAsync; }
            var window = new TeacherWindow(title, ask, async (kind, token) => await CreateExerciseAsync(await exerciseJson(kind, token), token), count) { Owner = this };
            if (CurrentPlaybackMeasure is { } playing) window.MeasureNumber = playing;
            teacherDialog(window);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or System.Xml.XmlException)
        { Status = "Não foi possível preparar a música para o professor: " + error.Message; }
    }
}
