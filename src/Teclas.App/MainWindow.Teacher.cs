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
    internal static string TeacherContext(string title, string artist, string chordSheet, IEnumerable<StudyRecord> practice)
    {
        var text = new StringBuilder();
        text.AppendLine("Título: " + title);
        if (artist.Length > 0) text.AppendLine("Artista: " + artist);
        string sheet = chordSheet.Length > 12000 ? chordSheet[..12000] + "\n[cifra cortada]" : chordSheet;
        text.AppendLine().AppendLine("Cifra (acordes sobre a letra; compassos em ordem):").AppendLine(sheet.TrimEnd());
        var attempts = practice.OrderByDescending(r => r.Date).Take(12).ToList();
        text.AppendLine().AppendLine(attempts.Count == 0 ? "Prática: o aluno ainda não praticou esta música no TeclaScore." : "Últimas práticas (mais recente primeiro):");
        foreach (var r in attempts)
            text.AppendLine($"- {r.Date:dd/MM} modo {r.Mode}{(r.Bpm is { } bpm ? $" a {bpm} bpm" : "")}: {r.Score:0}% de acerto, {r.StepsDone}/{r.Steps} passos, {r.Misses} erros{(r.Completed ? ", até o fim" : "")}");
        return text.ToString();
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
            string sheet = await Task.Run(() => ChordSheetWriter.FromScore(NativeMusicEditor.Open(score, CancellationToken.None), row.ScoreTitle));
            string song = row.FullPath ?? score;
            string context = TeacherContext(title, row.ScoreArtist ?? "", sheet, settings.StudyHistory.Where(r => string.Equals(r.Song, song, StringComparison.OrdinalIgnoreCase)));
            Func<string, CancellationToken, Task<string>> ask;
            if (teacherClient is { } stub) ask = (q, token) => stub(context, q, token);
            else { var teacher = new AiTeacher(key!, context); ask = teacher.AskAsync; }
            teacherDialog(new TeacherWindow(title, ask) { Owner = this });
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or System.Xml.XmlException)
        { Status = "Não foi possível preparar a música para o professor: " + error.Message; }
    }
}
