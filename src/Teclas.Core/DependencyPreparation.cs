using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Teclas.Core;

public sealed record DependencyProgress(string Message);
public sealed record DependencyProcessRequest(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout);
public sealed record DependencyProcessResult(int ExitCode, string Output, string Error);
public interface IDependencyProcessRunner
{
    Task<DependencyProcessResult> RunAsync(DependencyProcessRequest request, CancellationToken token);
}
public sealed record PythonDependencyCheck(string Path, bool Supported, bool Ready, string Version, string PillowVersion, string Message);
public sealed record MuseScoreDependencyCheck(string Path, bool Ready, string Version, string Message);
public sealed record DependencyCandidates(IReadOnlyList<string> Python, IReadOnlyList<string> MuseScore);

/// <summary>Preparation is opt-in. It never updates an existing Python installation or application settings.</summary>
public sealed class DependencyPreparation
{
    public const string PythonDownloadUrl = "https://www.python.org/downloads/windows/";
    public const string MuseScoreDownloadUrl = "https://musescore.org/en/download";
    public const string DotNetDownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";
    public const string PillowVersion = "12.3.0";
    private readonly IDependencyProcessRunner runner;
    private readonly string managedRoot;
    private readonly string scratchRoot;
    public string ManagedRoot => managedRoot;

    public DependencyPreparation(string managedRoot, string scratchRoot, IDependencyProcessRunner? runner = null)
    {
        this.managedRoot = Path.GetFullPath(managedRoot);
        this.scratchRoot = Path.GetFullPath(scratchRoot);
        this.runner = runner ?? new DependencyProcessRunner();
    }

    public static bool IsStoreAlias(string path) => path.Replace('/', '\\').Contains("\\Microsoft\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);
    public DependencyCandidates Discover(string configuredPython, string configuredMuseScore)
    {
        var python = new List<string>(); var muse = new List<string>();
        Add(python, configuredPython); Add(muse, configuredMuseScore);
        foreach (string folder in Subdirectories(managedRoot, "python-*"))
            if (File.Exists(Path.Combine(folder, "ready.json"))) Add(python, Path.Combine(folder, "Scripts", "python.exe"));
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            // Store aliases and install-manager shims may open a store or install a runtime.
            if (string.IsNullOrWhiteSpace(folder) || folder.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) || folder.Trim('"').TrimEnd('\\', '/').Replace('/', '\\').EndsWith("Python\\bin", StringComparison.OrdinalIgnoreCase)) continue;
            Add(python, Path.Combine(folder.Trim('"'), "python.exe"));
            Add(muse, Path.Combine(folder.Trim('"'), "MuseScore4.exe"));
        }
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string root in new[] { Path.Combine(local, "Programs", "Python"), Path.Combine(local, "Python"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            foreach (string folder in Subdirectories(root, "*python*")) Add(python, Path.Combine(folder, "python.exe"));
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Path.Combine(local, "Programs") })
            foreach (string folder in Subdirectories(root, "*MuseScore*")) Add(muse, Path.Combine(folder, "bin", "MuseScore4.exe"));
        if (OperatingSystem.IsWindows())
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var registry = RegistryKey.OpenBaseKey(hive, view);
                    using var key = registry.OpenSubKey(@"Software\Python\PythonCore");
                    foreach (string version in key?.GetSubKeyNames().Take(30) ?? [])
                    {
                        using var install = key!.OpenSubKey(version + @"\InstallPath");
                        if (install?.GetValue("ExecutablePath") is string exe) Add(python, exe);
                        if (install?.GetValue(null) is string directory) Add(python, Path.Combine(directory, "python.exe"));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
        return new(python.Take(16).ToArray(), muse.Take(12).ToArray());
    }
    private static void Add(List<string> list, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsStoreAlias(value)) return;
        try { value = Path.GetFullPath(value); if (File.Exists(value) && !list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
    }
    private static IEnumerable<string> Subdirectories(string path, string pattern)
    {
        if (string.IsNullOrEmpty(path)) return [];
        try { return Directory.Exists(path) ? Directory.GetDirectories(path, pattern).OrderByDescending(Directory.GetLastWriteTimeUtc).Take(40).ToArray() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private const string PythonProbe = "import sys,json,io,importlib.util\nr={'python':sys.version.split()[0],'supported':sys.version_info.major==3 and sys.version_info.minor>=11,'pillow':'','works':False}\ntry:\n import PIL\n from PIL import Image\n b=io.BytesIO();Image.new('RGB',(2,2),'white').save(b,format='PNG');b.seek(0);Image.open(b).load()\n r.update(pillow=PIL.__version__,works=True)\nexcept Exception: pass\nprint(json.dumps(r))";
    public async Task<PythonDependencyCheck> CheckPythonAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(path) || IsStoreAlias(path)) return new(path, false, false, "", "", "Python não encontrado. Instale Python 3.11 ou superior pelo site oficial.");
        try
        {
            var result = await runner.RunAsync(new(path, ["-I", "-c", PythonProbe], WorkingDirectory(path), TimeSpan.FromSeconds(15)), token);
            if (result.ExitCode != 0) return new(path, false, false, "", "", "Este executável não respondeu como Python. Escolha python.exe de uma instalação concluída.");
            using var json = JsonDocument.Parse(result.Output);
            var root = json.RootElement;
            bool supported = root.GetProperty("supported").GetBoolean(), pillow = root.GetProperty("works").GetBoolean();
            string version = root.GetProperty("python").GetString() ?? "", pillowVersion = root.GetProperty("pillow").GetString() ?? "";
            return new(path, supported, supported && pillow, version, pillowVersion, !supported ? $"Python {version}: é necessário Python 3.11 ou superior."
                : pillow ? $"Pronto: Python {version} · Pillow {pillowVersion}; criação e leitura de imagem conferidas."
                : $"Python {version} encontrado. Prepare o ambiente do TeclaScore para adicionar Pillow sem mudar essa instalação.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or JsonException or InvalidOperationException or KeyNotFoundException or TimeoutException)
        { return new(path, false, false, "", "", ex is TimeoutException ? "O teste do Python demorou demais. Confira a instalação ou escolha outro executável." : "Não foi possível validar este Python. Confira o arquivo escolhido e as permissões."); }
    }

    public async Task<MuseScoreDependencyCheck> CheckMuseScoreAsync(string path, IProgress<DependencyProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return new(path, false, "", "MuseScore não encontrado. Instale MuseScore Studio 4 e execute Detectar novamente.");
        string? scratch = null;
        try
        {
            progress?.Report(new("Conferindo a versão do MuseScore…"));
            var version = await runner.RunAsync(new(path, ["--version"], WorkingDirectory(path), TimeSpan.FromSeconds(15)), token);
            var match = Regex.Match(version.Output + " " + version.Error, @"MuseScore(?:4| Studio)?\s+(?<version>\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            if (version.ExitCode != 0 || !match.Success || !match.Groups["version"].Value.StartsWith("4.", StringComparison.Ordinal))
                return new(path, false, "", "Selecione MuseScore Studio 4. Outras versões não foram validadas para este aplicativo.");
            scratch = CreateOwnedDirectory(scratchRoot, "verify-");
            string source = Path.Combine(scratch, "check.musicxml"), output = Path.Combine(scratch, "check.svg");
            await File.WriteAllTextAsync(source, SyntheticScore, new UTF8Encoding(false), token);
            progress?.Report(new("Testando uma página de exemplo no MuseScore; nenhuma música da biblioteca é usada…"));
            var rendered = await runner.RunAsync(new(path, ["-o", output, source], scratch, TimeSpan.FromSeconds(60)), token);
            token.ThrowIfCancellationRequested();
            string? svg = Directory.GetFiles(scratch, "check*.svg").FirstOrDefault();
            if (rendered.ExitCode != 0 || svg is null || new FileInfo(svg).Length is < 50 or > 2_000_000)
                return new(path, false, match.Groups["version"].Value, "MuseScore iniciou, mas não concluiu a página de teste. Abra-o uma vez para concluir a instalação e tente novamente.");
            using var reader = XmlReader.Create(svg, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
            if (XDocument.Load(reader).Root?.Name.LocalName != "svg") throw new InvalidDataException("Página inválida.");
            return new(path, true, match.Groups["version"].Value, $"Pronto: MuseScore {match.Groups["version"].Value}; página de exemplo gerada e conferida.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or XmlException or TimeoutException)
        { return new(path, false, "", ex is TimeoutException ? "O MuseScore não terminou o teste no tempo esperado. Abra-o uma vez, feche e tente novamente." : "Não foi possível testar o MuseScore. Confira a instalação e as permissões."); }
        finally { if (scratch is not null) CleanupOwnedDirectory(scratchRoot, scratch); }
    }

    public async Task<PythonDependencyCheck> PreparePythonAsync(string basePython, IProgress<DependencyProgress>? progress, CancellationToken token)
    {
        var before = await CheckPythonAsync(basePython, token);
        if (!before.Supported) throw new InvalidOperationException(before.Message);
        string target = CreateOwnedDirectory(managedRoot, "python-"); bool complete = false;
        try
        {
            progress?.Report(new("Criando um ambiente Python separado para o TeclaScore…"));
            var created = await runner.RunAsync(new(basePython, ["-I", "-m", "venv", target], target, TimeSpan.FromMinutes(2)), token);
            if (created.ExitCode != 0) throw new InvalidOperationException("O Python não conseguiu criar o ambiente separado. Instale a distribuição completa com venv e pip habilitados e tente novamente.");
            string python = Path.Combine(target, "Scripts", "python.exe");
            if (!File.Exists(python)) throw new InvalidOperationException("O ambiente Python não foi criado por completo.");
            progress?.Report(new("Baixando Pillow do PyPI para o ambiente do TeclaScore. Esta etapa precisa de internet…"));
            var installed = await runner.RunAsync(new(python, ["-I", "-m", "pip", "--isolated", "install", "--disable-pip-version-check", "--no-input", "--no-cache-dir", "--only-binary=:all:", "--index-url", "https://pypi.org/simple", "Pillow==" + PillowVersion], target, TimeSpan.FromMinutes(5)), token);
            if (installed.ExitCode != 0) throw new InvalidOperationException("Pillow não pôde ser preparado. Confira a conexão, proxy e permissões. O Python original e as configurações foram preservados.");
            progress?.Report(new("Conferindo Python e Pillow antes de oferecer o novo caminho…"));
            var result = await CheckPythonAsync(python, token);
            if (!result.Ready || result.PillowVersion != PillowVersion) throw new InvalidOperationException("O novo ambiente não passou no teste final. Nenhuma configuração foi alterada.");
            token.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(Path.Combine(target, "ready.json"), JsonSerializer.Serialize(new { schemaVersion = 1, python = result.Version, pillow = result.PillowVersion }), token);
            complete = true; return result;
        }
        finally { if (!complete) CleanupOwnedDirectory(managedRoot, target); }
    }

    private static string WorkingDirectory(string path) => Path.GetDirectoryName(Path.GetFullPath(path))!;
    private static string CreateOwnedDirectory(string root, string prefix)
    {
        Directory.CreateDirectory(root);
        string directory = Path.Combine(root, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return directory;
    }
    private static void CleanupOwnedDirectory(string root, string directory)
    {
        string boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(directory);
        if (!full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(full) != Path.GetFullPath(root)) return;
        try { Directory.Delete(full, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private const string SyntheticScore = "<?xml version=\"1.0\" encoding=\"utf-8\"?><score-partwise version=\"4.0\"><work><work-title>Teste do TeclaScore</work-title></work><part-list><score-part id=\"P1\"><part-name>Piano</part-name></score-part></part-list><part id=\"P1\"><measure number=\"1\"><attributes><divisions>1</divisions><time><beats>4</beats><beat-type>4</beat-type></time><clef><sign>G</sign><line>2</line></clef></attributes><note><pitch><step>C</step><octave>4</octave></pitch><duration>4</duration><type>whole</type></note></measure></part></score-partwise>";
}

public sealed class DependencyProcessRunner : IDependencyProcessRunner
{
    public async Task<DependencyProcessResult> RunAsync(DependencyProcessRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(request.Executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = request.WorkingDirectory };
        foreach (string argument in request.Arguments) start.ArgumentList.Add(argument);
        start.Environment["PYTHONUTF8"] = "1"; start.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("O processo não iniciou.");
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var stdout = ReadBounded(process.StandardOutput, linked.Token); var stderr = ReadBounded(process.StandardError, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            token.ThrowIfCancellationRequested(); throw new TimeoutException("O processo excedeu o tempo de espera.");
        }
    }
    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0) if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length));
        return text.ToString();
    }
}
