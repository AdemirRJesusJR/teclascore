using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Teclas.Core;

namespace Teclas.App;

/// <summary>
/// <c>Teclas.Partituras.exe --gerar …</c>: generation without the window (it is created but never shown), for scripts and
/// scheduled tasks. Messages go to the console that started the app; the exit code says how the run ended.
/// </summary>
public partial class App
{
    private const int AttachParentProcess = -1;
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);

    private bool commandLineFinished;

    private async void RunCommandLine(string[] args)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        OpenCommandLineOutput();
        var start = DateTimeOffset.Now;
        string version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        var parse = BatchCommandLine.Parse(args);
        if (parse.ShowHelp)
        {
            // Started without a console (a shortcut, for example): the help still has to be readable.
            if (ReferenceEquals(commandOut, TextWriter.Null)) MessageBox.Show(BatchCommandLine.Usage, "TeclaScore · linha de comando");
            else Write(BatchCommandLine.Usage);
            Finish(BatchCommandLine.ExitReady); return;
        }
        if (parse.Options is not { } options)
        {
            WriteError("Erro: " + parse.Error + Environment.NewLine + Environment.NewLine + BatchCommandLine.Usage);
            Finish(BatchCommandLine.ExitError);
            return;
        }
        var files = new List<BatchFileReport>();
        string? runError = null;
        MainWindow? window = null;
        DispatcherUnhandledException += (_, e) =>
        {
            // No message box in this mode: the error goes to the console, the crash log and the report.
            e.Handled = true;
            WriteCrashLog(CrashFolder, "linha de comando", e.Exception);
            Complete(e.Exception.Message);
        };
        try
        {
            if (options.SettingsPath is null && OtherInstanceIsRunning())
                throw new InvalidOperationException("Feche o TeclaScore aberto antes de usar --gerar: os dois usariam a mesma biblioteca. Outra opção é --configuracoes com outro arquivo.");
            var midis = BatchCommandLine.CollectMidis(options, files);
            if (midis.Count == 0) throw new InvalidOperationException("Nenhum MIDI para gerar.");
            Write($"TeclaScore {version}: {midis.Count} MIDI(s).");
            window = new MainWindow(false, options.SettingsPath) { ShowInTaskbar = false, ShowActivated = false, WindowState = WindowState.Minimized };
            files.AddRange(await window.RunCommandLineBatchAsync(options, midis, line => Write("  " + line)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        { runError = ex.Message; }
        Complete(runError);

        void Complete(string? error)
        {
            if (commandLineFinished) return;
            var report = BatchRunReport.Create(version, start, files, error);
            foreach (var file in files)
                Write($"{Label(file.Situacao),-16} {Path.GetFileName(file.Arquivo)}" + (file.Partitura is { } score ? " → " + score : "") +
                      (file.Pdf is { } pdf ? " · PDF " + pdf : "") + (file.Motivo is { } reason ? " · " + reason : "") + (file.PdfErro is { } pdfError ? " · PDF: " + pdfError : ""));
            var s = report.Resumo;
            if (options.Simulate) Write($"Resumo da simulação: {files.Count} arquivo(s) avaliados, nada gerado.");
            else Write($"Resumo: {s.Prontas} pronta(s), {s.ParaRevisar} para revisar, {s.PrecisamDeVoce} precisam de você, {s.Falharam} falharam, {s.JaGeradas} já tinham partitura, {s.Pdfs} PDF(s).");
            if (error is not null) WriteError("Erro: " + error);
            if (options.ReportPath is { } reportPath)
            {
                try { BatchCommandLine.WriteReport(report, reportPath); Write("Relatório: " + Path.GetFullPath(reportPath)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WriteError("Não foi possível gravar o relatório: " + ex.Message); report = report with { CodigoSaida = BatchCommandLine.ExitError }; }
            }
            try { window?.Close(); } catch (InvalidOperationException) { }
            Finish(report.CodigoSaida);
        }
    }

    private void Finish(int code)
    {
        commandLineFinished = true;
        try { commandOut.Flush(); commandError.Flush(); } catch (IOException) { }
        Shutdown(code);
    }

    private static string Label(string situation) => situation switch
    {
        BatchFileReport.Ready => "[pronta]", BatchFileReport.Review => "[para revisar]", BatchFileReport.NeedsYou => "[precisa de você]",
        BatchFileReport.AlreadyGenerated => "[já gerada]", BatchFileReport.Simulated => "[simulada]", _ => "[falhou]"
    };

    private static bool OtherInstanceIsRunning()
    {
        using var current = Process.GetCurrentProcess();
        var others = Process.GetProcessesByName(current.ProcessName);
        try { return others.Any(p => p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero); }
        finally { foreach (var process in others) process.Dispose(); }
    }

    private static TextWriter commandOut = TextWriter.Null, commandError = TextWriter.Null;

    /// <summary>
    /// A window app has no console of its own: it borrows the one that started it. Text is written in that console's
    /// code page (850, UTF-8…), also when the output is redirected to a pipe or file, so accents arrive intact.
    /// </summary>
    private static void OpenCommandLineOutput()
    {
        bool attached = AttachConsole(AttachParentProcess);
        uint codePage = attached ? GetConsoleOutputCP() : 0;
        Encoding encoding = new UTF8Encoding(false);
        if (codePage is not (0 or 65001))
        {
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); encoding = Encoding.GetEncoding((int)codePage); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        TextWriter Open(Stream stream) => stream == Stream.Null ? TextWriter.Null : new StreamWriter(stream, encoding) { AutoFlush = true };
        try { commandOut = Open(Console.OpenStandardOutput()); commandError = Open(Console.OpenStandardError()); }
        catch (IOException) { }
    }

    [DllImport("kernel32.dll")] private static extern uint GetConsoleOutputCP();
    private static void Write(string text) { try { commandOut.WriteLine(text); } catch (IOException) { } }
    private static void WriteError(string text) { try { commandError.WriteLine(text); } catch (IOException) { } }
}
