using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Teclas.App;

public static class DependencyDiagnostics
{
    public static async Task<string> CheckAsync(string pythonPath, string museScorePath, CancellationToken cancellationToken = default)
    {
        var lines = new List<string> { $"✓ .NET Desktop {Environment.Version} — aplicativo em execução." };
        if (File.Exists(museScorePath))
        {
            var version = FileVersionInfo.GetVersionInfo(museScorePath);
            if (version.FileMajorPart == 4) lines.Add($"✓ MuseScore {version.FileVersion}.");
            else
            {
                // Some official Windows builds have no PE version resource.
                using var museTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                museTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                try
                {
                    var start = new ProcessStartInfo(museScorePath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
                    start.ArgumentList.Add("--version");
                    using var process = Process.Start(start) ?? throw new IOException("MuseScore não iniciou.");
                    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    try { await process.WaitForExitAsync(museTimeout.Token); }
                    catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
                    var text = (await stdout + " " + await stderr).Trim();
                    lines.Add(process.ExitCode == 0 && System.Text.RegularExpressions.Regex.IsMatch(text, @"MuseScore(?:4| Studio)?\s+4\.")
                        ? "✓ " + text[..Math.Min(text.Length, 120)] + "."
                        : "Atenção: selecione o executável do MuseScore 4 em Configurações.");
                }
                catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException)
                { lines.Add("Não foi possível conferir a versão do MuseScore configurado."); }
            }
        }
        else lines.Add("MuseScore 4 não encontrado no caminho configurado.");
        if (!File.Exists(pythonPath)) { lines.Add("Python não encontrado no caminho configurado."); return string.Join(Environment.NewLine, lines); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var start = new ProcessStartInfo(pythonPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import sys,json; import PIL; print(json.dumps({'python':sys.version.split()[0],'pillow':PIL.__version__,'ok':sys.version_info >= (3,10)}))");
            using var process = Process.Start(start) ?? throw new IOException("Python não iniciou.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
            var text = await output;
            await errors;
            if (process.ExitCode != 0) lines.Add("Python iniciou, mas Pillow não está disponível. Instale Pillow nesse ambiente Python.");
            else
            {
                using var json = JsonDocument.Parse(text);
                lines.Add($"{(json.RootElement.GetProperty("ok").GetBoolean() ? "✓" : "Atenção:")} Python {json.RootElement.GetProperty("python").GetString()} · Pillow {json.RootElement.GetProperty("pillow").GetString()} (necessário Python 3.10 ou superior).");
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or JsonException or UnauthorizedAccessException)
        { lines.Add("Não foi possível executar o Python configurado. Confira o caminho e a instalação do Pillow."); }
        return string.Join(Environment.NewLine, lines);
    }
}
