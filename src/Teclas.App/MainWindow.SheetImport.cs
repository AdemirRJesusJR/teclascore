using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Teclas.Core;

namespace Teclas.App;

/// <summary>Finds an Audiveris installed by the user (TeclaScore does not download or install it).</summary>
internal static class AudiverisLocator
{
    public const string DownloadUrl = "https://github.com/Audiveris/audiveris/releases";
    public static readonly string[] Extensions = [".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff"];

    public static IEnumerable<string> Candidates()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") }.Where(r => r.Length > 0).Distinct())
        {
            yield return Path.Combine(root, "Audiveris", "Audiveris.exe");
            yield return Path.Combine(root, "Audiveris", "bin", "Audiveris.bat");
        }
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(folder, "Audiveris.exe");
            yield return Path.Combine(folder, "Audiveris.bat");
        }
    }

    public static string? Find(IEnumerable<string>? candidates = null)
    {
        foreach (string path in candidates ?? Candidates())
        {
            try { if (File.Exists(path)) return path; } catch (ArgumentException) { }
        }
        return null;
    }
}

/// <summary>
/// PDF or photo to score: an installed Audiveris reads the printed music (optical music recognition) into MusicXML,
/// which MuseScore converts to .mscz like any imported MusicXML. Without Audiveris, the button points to its official page.
/// </summary>
public partial class MainWindow
{
    internal Func<string?> audiverisPath = () => AudiverisLocator.Find();
    internal Func<string[]?>? sheetFilesPicker = null;
    /// <summary>Test hook: (Audiveris, image or PDF, output folder) → MusicXML files written.</summary>
    internal Func<string, string, string, CancellationToken, Task>? audiverisRunner = null;
    internal Func<bool>? audiverisMissingPrompt = null;

    public bool HasAudiveris => audiverisPath() is not null;
    public string SheetImportToolTip => HasAudiveris
        ? "Lê uma partitura impressa (PDF ou foto) com o Audiveris instalado neste computador e cria a partitura editável. Confira o resultado: o reconhecimento pode errar notas."
        : "Lê partituras impressas (PDF ou foto) com o Audiveris, programa gratuito que você instala à parte. Clique para ver como instalar.";

    private bool AskToInstallAudiveris()
    {
        var answer = MessageBox.Show(this,
            "Para ler partituras em PDF ou foto, o TeclaScore usa o Audiveris, um programa gratuito e de código aberto que você instala à parte.\n\n" +
            "Abrir a página oficial de downloads do Audiveris? Depois de instalar, volte aqui e clique de novo em \"PDF ou foto\".",
            "Audiveris não encontrado", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return false;
        try { Process.Start(new ProcessStartInfo(AudiverisLocator.DownloadUrl) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { Status = "Abra no navegador: " + AudiverisLocator.DownloadUrl; }
        return true;
    }

    private async void SheetToScoreClick(object sender, RoutedEventArgs e)
    {
        if (!IsIdle || !CommitEdits()) return;
        if (audiverisPath() is null) { (audiverisMissingPrompt ?? AskToInstallAudiveris)(); Notify(nameof(HasAudiveris)); Notify(nameof(SheetImportToolTip)); return; }
        string[]? files = sheetFilesPicker is { } pick ? pick() : null;
        if (sheetFilesPicker is null)
        {
            var dialog = new OpenFileDialog { Title = "Escolha partituras impressas (PDF ou foto)", Multiselect = true,
                Filter = "PDF ou imagem|" + string.Join(";", AudiverisLocator.Extensions.Select(x => "*" + x)) };
            files = dialog.ShowDialog(this) == true ? dialog.FileNames : null;
        }
        if (files is not { Length: > 0 }) return;
        var scores = await ConvertMusicXmlAsync(await RecognizeSheetsAsync(files));
        if (scores.Count == 0) return;
        await ImportAndOpenLibraryAsync(scores);
        await ReviewRecognizedAsync(scores);
    }

    internal Func<Window, bool?> recognitionReviewDialog = window => window.ShowDialog();

    /// <summary>
    /// Optical recognition misreads some measures; after the import, the measures worth checking are listed (beats
    /// missing or extra, staves that disagree, notes out of range) so they can be fixed in the editor. Returns the count.
    /// </summary>
    internal async Task<int> ReviewRecognizedAsync(IReadOnlyList<string> scores)
    {
        var found = new List<(string Name, IReadOnlyList<SuspectMeasure> Measures)>();
        foreach (string score in scores)
        {
            try
            {
                var suspects = await Task.Run(() => RecognitionAudit.Check(NativeMusicEditor.Open(score, CancellationToken.None)));
                if (suspects.Count > 0) found.Add((Path.GetFileNameWithoutExtension(score), suspects));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Xml.XmlException) { found.Add((Path.GetFileNameWithoutExtension(score), [new SuspectMeasure(0, "não foi possível conferir: " + error.Message)])); }
        }
        int total = found.Sum(f => f.Measures.Count);
        if (total == 0) { Status = "Partitura lida e conferida: nenhum compasso com tempos faltando ou sobrando. Ouça e compare com o original."; return 0; }
        var text = new System.Text.StringBuilder();
        foreach (var (name, measures) in found)
        {
            text.AppendLine(name);
            foreach (var measure in measures) text.AppendLine(measure.Number > 0 ? $"  Compasso {measure.Number}: {measure.Reason}" : "  " + measure.Reason);
            text.AppendLine();
        }
        Status = $"Partitura lida: {total} compasso(s) para conferir. Abra no editor e compare com o original.";
        var window = new Window { Title = "Conferir a leitura", Width = 640, Height = 460, Owner = this };
        DialogUi.Theme(window);
        var panel = new System.Windows.Controls.DockPanel { Margin = new Thickness(20) }; window.Content = panel;
        var head = new System.Windows.Controls.TextBlock { Text = $"A leitura óptica pode errar. Confira estes {total} compasso(s) comparando com o PDF ou a foto original:", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        System.Windows.Controls.DockPanel.SetDock(head, System.Windows.Controls.Dock.Top); panel.Children.Add(head);
        var ok = DialogUi.Button("Entendi", (_, _) => window.Close(), true); ok.IsCancel = true; ok.HorizontalAlignment = HorizontalAlignment.Right; ok.Margin = new Thickness(0, 10, 0, 0);
        System.Windows.Controls.DockPanel.SetDock(ok, System.Windows.Controls.Dock.Bottom); panel.Children.Add(ok);
        var list = new System.Windows.Controls.TextBox { Text = text.ToString().TrimEnd(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, Name = "RecognitionReviewText" };
        System.Windows.Automation.AutomationProperties.SetName(list, "Compassos para conferir");
        panel.Children.Add(list);
        recognitionReviewDialog(window);
        return total;
    }

    /// <summary>Runs Audiveris on each file; returns the MusicXML files it produced (kept in the output folder).</summary>
    internal async Task<IReadOnlyList<string>> RecognizeSheetsAsync(IReadOnlyList<string> files)
    {
        var xml = new List<string>(); var failed = new List<string>();
        string? audiveris = audiverisPath();
        if (audiveris is null) return xml;
        await RunOperation(async () =>
        {
            var token = cancellation!.Token;
            string folder = Path.Combine(await PrepareOutputDirectoryAsync(token), "Partituras lidas");
            for (int i = 0; i < files.Count; i++)
            {
                string name = Path.GetFileNameWithoutExtension(files[i]);
                SetOperationItems(i, files.Count, name); Status = $"Lendo {name} com o Audiveris… (pode levar alguns minutos por página)";
                string target = Path.Combine(folder, ScoreFileName.Stem(null, name) + "-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                Directory.CreateDirectory(target);
                try
                {
                    await (audiverisRunner ?? RunAudiverisAsync)(audiveris, files[i], target, token);
                    var found = Directory.EnumerateFiles(target, "*.*", SearchOption.AllDirectories)
                        .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".mxl" or ".musicxml").Order().ToList();
                    if (found.Count == 0) failed.Add(name); else xml.AddRange(found);
                }
                catch (Exception error) when (error is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { failed.Add(name + " (" + error.Message + ")"); }
            }
        }, operationTitle: "Lendo partituras impressas");
        if (failed.Count > 0) Status = "O Audiveris não reconheceu música em: " + string.Join("; ", failed) + ". Use uma imagem nítida e reta, de 300 dpi, com a partitura inteira.";
        return xml;
    }

    private static async Task RunAudiverisAsync(string audiveris, string input, string output, CancellationToken token)
    {
        bool script = audiveris.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(script ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : audiveris)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        if (script) { start.ArgumentList.Add("/c"); start.ArgumentList.Add(audiveris); }
        foreach (string arg in new[] { "-batch", "-export", "-output", output, "--", input }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("O Audiveris não abriu.");
        var errors = process.StandardError.ReadToEndAsync(token); _ = process.StandardOutput.ReadToEndAsync(token);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromMinutes(15));
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        if (process.ExitCode != 0)
        {
            string tail = (await errors).Trim(); tail = tail.Length > 300 ? tail[^300..] : tail;
            throw new InvalidOperationException($"o Audiveris terminou com erro {process.ExitCode}{(tail.Length > 0 ? ": " + tail : "")}");
        }
    }
}
