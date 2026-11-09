#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$PythonPath = 'python',
    [string]$OutputDirectory,
    [string]$MuseScorePath,
    [ValidateRange(1,20)][int]$BenchmarkIterations = 5,
    [ValidateRange(30,1800)][int]$StepTimeoutSeconds = 600,
    # Also run the engine's native MuseScore tests (labels, lyrics, repeats, score style); needs MuseScore installed.
    [switch]$NativeEngineTests,
    # Native suites to leave out, with the reason recorded in the report (e.g. a known difference of a CI server).
    [string[]]$SkipNativeSuites = @(),
    [string]$SkipNativeReason = '',
    # Run the checks on the user's own desktop. By default they run on a secondary, invisible Windows
    # desktop so the WPF smokes never flash on screen (not on GitHub Actions, where nobody watches).
    [switch]$VisibleWindows
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $IsWindows) { throw 'The TeclaScore CI runner requires Windows for the WPF application.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo ('qa\ci-' + [guid]::NewGuid().ToString('N').Substring(0,8)) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.Directory]::Exists($output) -and [IO.Directory]::EnumerateFileSystemEntries($output).GetEnumerator().MoveNext()) {
    throw 'Choose a new or empty output directory. Existing test results are never overwritten.'
}
$reports = Join-Path $output 'reports'
$work = Join-Path $output 'work'
$artifacts = Join-Path $output 'build'
foreach ($path in @($reports,$work,(Join-Path $output 'tmp'))) { [void][IO.Directory]::CreateDirectory($path) }
$results = [Collections.Generic.List[object]]::new()
$configuration = 'Release'
$started = [DateTimeOffset]::UtcNow

# Each check is a child process. On a developer's machine they run on a secondary desktop created with
# CreateDesktop: the ~45 WPF smokes open real windows (Opacity 0, ShowActivated false), and Windows still
# shows those on screen and in Alt+Tab for a moment each, so a full run flashes more than a hundred windows.
# Processes on the secondary desktop are invisible, their children (MuseScore, Python) inherit it, and the
# PNG renders are unchanged: the smokes draw with RenderTargetBitmap, which never reads the screen.
# System.Diagnostics.Process cannot set STARTUPINFO.lpDesktop, hence the small CreateProcess wrapper below.
# Fallback: GitHub Actions (-VisibleWindows is implied, nobody watches that desktop) or a CreateDesktop failure.
$ciProcessSource = @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TeclasCi
{
    /// <summary>A checked process: on a hidden desktop through CreateProcess, or the ordinary Process class.</summary>
    public sealed class CiProcess : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb; public IntPtr lpReserved; public string lpDesktop; public IntPtr lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr security);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out int exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle handle, int exitCode);

        private const uint GENERIC_ALL = 0x10000000, CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000;
        private const int STARTF_USESTDHANDLES = 0x100;
        private const uint WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 0x102;

        private readonly Process process;            // visible-desktop path
        private readonly SafeProcessHandle handle;   // hidden-desktop path
        private readonly int processId;
        private int? exitCode;

        public StreamReader StandardOutput { get; private set; }
        public StreamReader StandardError { get; private set; }
        public int Id { get { return process != null ? process.Id : processId; } }
        public bool OnHiddenDesktop { get { return handle != null; } }

        /// <summary>Creates the invisible desktop in the current window station; IntPtr.Zero when Windows refuses.</summary>
        public static IntPtr CreateHiddenDesktop(string name)
        {
            try { return CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero); }
            catch (Exception) { return IntPtr.Zero; }
        }
        public static void CloseHiddenDesktop(IntPtr desktop) { if (desktop != IntPtr.Zero) CloseDesktop(desktop); }
        public static string LastErrorMessage() { return new Win32Exception(Marshal.GetLastWin32Error()).Message; }

        public static CiProcess Start(ProcessStartInfo info, string desktop)
        {
            if (string.IsNullOrEmpty(desktop)) return new CiProcess(Process.Start(info));
            return new CiProcess(info, desktop);
        }

        private CiProcess(Process started)
        {
            process = started;
            StandardOutput = started.StandardOutput; StandardError = started.StandardError;
        }

        private CiProcess(ProcessStartInfo info, string desktop)
        {
            var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            var startup = new STARTUPINFO();
            startup.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            startup.lpDesktop = desktop;
            startup.dwFlags = STARTF_USESTDHANDLES;
            startup.hStdInput = stdin.ClientSafePipeHandle.DangerousGetHandle();
            startup.hStdOutput = stdout.ClientSafePipeHandle.DangerousGetHandle();
            startup.hStdError = stderr.ClientSafePipeHandle.DangerousGetHandle();
            var commandLine = new StringBuilder(Quote(info.FileName));
            foreach (string argument in info.ArgumentList) commandLine.Append(' ').Append(Quote(argument));
            IntPtr environment = Marshal.StringToHGlobalUni(EnvironmentBlock(info.Environment));
            PROCESS_INFORMATION information;
            bool created;
            try
            {
                created = CreateProcessW(info.FileName, commandLine, IntPtr.Zero, IntPtr.Zero, true, CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                    environment, string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory, ref startup, out information);
            }
            finally { Marshal.FreeHGlobal(environment); }
            int error = Marshal.GetLastWin32Error();
            stdin.DisposeLocalCopyOfClientHandle(); stdout.DisposeLocalCopyOfClientHandle(); stderr.DisposeLocalCopyOfClientHandle();
            stdin.Dispose(); // the child reads end-of-file, like a CI runner without a console
            if (!created) { stdout.Dispose(); stderr.Dispose(); throw new Win32Exception(error, "CreateProcess failed for " + info.FileName); }
            CloseHandle(information.hThread);
            handle = new SafeProcessHandle(information.hProcess, true);
            processId = information.dwProcessId;
            var encoding = info.StandardOutputEncoding ?? new UTF8Encoding(false);
            StandardOutput = new StreamReader(stdout, encoding, true);
            StandardError = new StreamReader(stderr, info.StandardErrorEncoding ?? encoding, true);
        }

        public bool WaitForExit(int milliseconds)
        {
            if (process != null) return process.WaitForExit(milliseconds);
            uint result = WaitForSingleObject(handle, milliseconds < 0 ? uint.MaxValue : (uint)milliseconds);
            if (result == WAIT_TIMEOUT) return false;
            if (result != WAIT_OBJECT_0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Waiting for the process failed.");
            return true;
        }

        public bool HasExited { get { return process != null ? process.HasExited : WaitForSingleObject(handle, 0) == WAIT_OBJECT_0; } }

        public int ExitCode
        {
            get
            {
                if (process != null) return process.ExitCode;
                if (exitCode.HasValue) return exitCode.Value;
                int code;
                if (!GetExitCodeProcess(handle, out code)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Reading the exit code failed.");
                exitCode = code; return code;
            }
        }

        /// <summary>Stops the process and every child it owns (a smoke's MuseScore or Python).</summary>
        public void Kill()
        {
            if (process != null) { process.Kill(true); return; }
            try { using (var owner = Process.GetProcessById(processId)) owner.Kill(true); }
            catch (Exception) { TerminateProcess(handle, -1); }
        }

        public void Dispose()
        {
            if (process != null) process.Dispose();
            if (handle != null) handle.Dispose();
        }

        private static string EnvironmentBlock(IDictionary<string, string> variables)
        {
            var keys = new List<string>(variables.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            var block = new StringBuilder();
            foreach (string key in keys) block.Append(key).Append('=').Append(variables[key]).Append('\0');
            return block.Append('\0').ToString();
        }

        // Same rules as .NET's own argument pasting: the child sees exactly the ArgumentList entries.
        private static string Quote(string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return argument;
            var quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { quoted.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
                quoted.Append('\\', backslashes).Append(c); backslashes = 0;
            }
            return quoted.Append('\\', backslashes).Append('"').ToString();
        }
    }
}
'@
Add-Type -TypeDefinition $ciProcessSource
$hiddenDesktop = $null; $hiddenDesktopHandle = [IntPtr]::Zero
if ($VisibleWindows) { $desktopNote = 'visible desktop (-VisibleWindows)' }
elseif ($env:GITHUB_ACTIONS) { $desktopNote = 'visible desktop (GitHub Actions)' }
else {
    $name = 'TeclaScoreCI-' + [guid]::NewGuid().ToString('N').Substring(0,8)
    $hiddenDesktopHandle = [TeclasCi.CiProcess]::CreateHiddenDesktop($name)
    if ($hiddenDesktopHandle -ne [IntPtr]::Zero) { $hiddenDesktop = $name; $desktopNote = 'hidden desktop ' + $name }
    else { $desktopNote = 'visible desktop (CreateDesktop failed: ' + [TeclasCi.CiProcess]::LastErrorMessage() + ')' }
}
Write-Host ('Checks run on the ' + $desktopNote + '.')

function Record-Skip([string]$Name, [string]$Reason) {
    $results.Add([pscustomobject]@{name=$Name;status='skipped';seconds=0;exitCode=$null;reason=$Reason;log=$null})
    Write-Host ('SKIP ' + $Name + ': ' + $Reason)
}
function Invoke-Check {
    param([string]$Name,[string]$Executable,[string[]]$Arguments=@(),[string]$Directory=$work,
          [int]$Timeout=$StepTimeoutSeconds,[string]$JsonReport)
    Write-Host ('RUN  ' + $Name)
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $log=Join-Path $reports ($Name + '.log')
    $process=$null; $code=-1; $reason=''; $stdout=''; $stderr=''
    try {
        [void][IO.Directory]::CreateDirectory($Directory)
        $info=[Diagnostics.ProcessStartInfo]::new($Executable)
        foreach ($argument in $Arguments) { [void]$info.ArgumentList.Add($argument) }
        $info.WorkingDirectory=$Directory; $info.UseShellExecute=$false; $info.CreateNoWindow=$true
        $info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
        $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
        $info.Environment['TEMP']=Join-Path $output 'tmp';$info.Environment['TMP']=Join-Path $output 'tmp'
        $info.Environment['PARTITURAS_JOB']=Join-Path $work 'engine-job'
        $info.Environment['PYTHONUTF8']='1'; $info.Environment['PYTHONDONTWRITEBYTECODE']='1'
        $info.Environment['DOTNET_CLI_HOME']=Join-Path $output 'dotnet-home'
        $info.Environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE']='1';$info.Environment['DOTNET_CLI_TELEMETRY_OPTOUT']='1'
        $info.Environment['MSBuildEnableWorkloadResolver']='false'
        if ($NativeEngineTests) { $info.Environment['TECLAS_TEST_NATIVE_LABELS']='1' } else { [void]$info.Environment.Remove('TECLAS_TEST_NATIVE_LABELS') }
        # ProcessStartInfo bypasses pwsh's automatic WinPSModulePath handling.
        # Let Windows PowerShell reconstruct its own module paths instead of
        # loading incompatible PS7 modules (which hides cmdlets like Get-FileHash).
        # Only the child environment is changed; the runner/user environment stays intact.
        if ([IO.Path]::GetFileName($Executable) -ieq 'powershell.exe') {
            [void]$info.Environment.Remove('PSModulePath')
        }
        $process=[TeclasCi.CiProcess]::Start($info,$hiddenDesktop)
        $outTask=$process.StandardOutput.ReadToEndAsync();$errTask=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($Timeout*1000)) {
            $process.Kill();[void]$process.WaitForExit(10000);throw "Timed out after $Timeout seconds; owned child processes were stopped."
        }
        $stdout=$outTask.GetAwaiter().GetResult();$stderr=$errTask.GetAwaiter().GetResult();$code=$process.ExitCode
        if ($code -ne 0) { throw "Process exited with code $code." }
        if ($JsonReport) {
            if (-not [IO.File]::Exists($JsonReport)) { throw 'Expected structured test report was not produced.' }
            $data=Get-Content -LiteralPath $JsonReport -Raw | ConvertFrom-Json
            $reportStatus=$data.PSObject.Properties['status']
            if ($null -eq $reportStatus -or $reportStatus.Value -ne 'passed') { throw 'Structured report did not confirm success.' }
        }
    } catch {
        $reason=$_.Exception.Message
        if ($code -eq 0) { $code=-1 }
        if ($process -and -not $process.HasExited) { try {$process.Kill()} catch {} }
    } finally {
        if ($process) { $process.Dispose() }
        $timer.Stop()
        [IO.File]::WriteAllText($log,$stdout+[Environment]::NewLine+$stderr+[Environment]::NewLine+$reason)
    }
    $status=if($code -eq 0 -and -not $reason){'passed'}else{'failed'}
    $results.Add([pscustomobject]@{name=$Name;status=$status;seconds=[Math]::Round($timer.Elapsed.TotalSeconds,3);exitCode=$code;reason=$reason;log=[IO.Path]::GetFileName($log)})
    Write-Host ($status.ToUpperInvariant()+' '+$Name+' ('+$timer.Elapsed.TotalSeconds.ToString('0.0')+'s) '+$reason)
    if ($status -eq 'failed') { Write-Host ($stdout+$stderr) }
    return $status -eq 'passed'
}
function Binary([string]$Project,[string]$Name) {
    $directory=Join-Path $artifacts ('bin\'+$Project+'\release')
    if (-not $Name) { $Name=$Project+'.dll' }
    return Join-Path $directory $Name
}

try {
    # A machine can expose several matching installations. Use the first PATH match,
    # exactly as an ordinary invocation does, rather than joining executable paths.
    $dotnet=(Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $python=(Get-Command $PythonPath -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    [void](Invoke-Check 'dotnet-version' $dotnet @('--version'))
    $pythonReady=Invoke-Check 'python-dependencies' $python @('-c','import sys,PIL; assert sys.version_info >= (3,11); print(sys.version); print("Pillow",PIL.__version__)')
    $built=Invoke-Check 'build-solution' $dotnet @('build',(Join-Path $repo 'Teclas.Partituras.sln'),'--configuration',$configuration,'--artifacts-path',$artifacts,'--nologo','-m:1','/p:UseSharedCompilation=false') $repo
    $directorySuites=@('Teclas.Arrangement.Tests','Teclas.Backup.Tests','Teclas.BatchPdf.Tests','Teclas.Dependencies.Tests','Teclas.Export.Tests','Teclas.PortableLibrary.Tests','Teclas.Presentation.Tests','Teclas.Queue.Tests','Teclas.Recovery.Tests','Teclas.ScoreEditing.Tests','Teclas.Study.Tests','Teclas.Transposition.Tests','Teclas.Voicing.Tests')
    $noArgumentSuites=@('Teclas.Dashboard.Tests','Teclas.Settings.Tests','Teclas.Updates.Tests','Teclas.Viewer.Tests','Teclas.PerformanceTests')
    $projects=Get-ChildItem -LiteralPath (Join-Path $repo 'tests') -Directory -Filter 'Teclas.*' | Sort-Object Name
    foreach ($project in $projects) {
        $name=$project.Name
        if (-not (Test-Path -LiteralPath (Join-Path $project.FullName ($name+'.csproj')))) { continue }
        if ($name -eq 'Teclas.PlaybackMap.Tests') { continue } # explicitly accounted for in the native stage below
        if (-not $built) { Record-Skip $name 'Solution build failed; no stale executable is run.';continue }
        $suiteWork=Join-Path $work $name; [void][IO.Directory]::CreateDirectory($suiteWork)
        $testArguments=@()
        if ($name -in $directorySuites) {$testArguments=@($suiteWork)}
        elseif ($name -in $noArgumentSuites) {}
        elseif ($name -eq 'Teclas.Tests') {$testArguments=@('--no-inventory')}
        elseif ($name -eq 'Teclas.Audition.Tests') {$testArguments=@((Join-Path $suiteWork 'report.json'))}
        else { throw "New console suite $name needs a reviewed synthetic argument mapping in verify_ci.ps1." }
        [void](Invoke-Check $name $dotnet (@((Binary $name ''))+$testArguments) $suiteWork)
    }
    if ($pythonReady) {
        [void](Invoke-Check 'python-unittests' $python (@('-B',(Join-Path $PSScriptRoot 'ci\run_python_tests.py'),'--output',(Join-Path $reports 'python-tests.json')) + @(if ($NativeEngineTests) { '--native' })) $work -JsonReport (Join-Path $reports 'python-tests.json'))
        $fixtures=Join-Path $work 'fixtures'
        $fixturesReady=Invoke-Check 'synthetic-fixtures' $python @('-B',(Join-Path $PSScriptRoot 'ci\make_fixtures.py'),$fixtures)
        $parity=Join-Path $work 'Teclas.Voicing.Tests\renderer-parity.json'
        if ($built -and [IO.File]::Exists($parity)) { [void](Invoke-Check 'voicing-renderer-parity' $python @('-B',(Join-Path $repo 'tests\verify_voicing_renderer.py'),$parity)) }
        else { Record-Skip 'voicing-renderer-parity' 'The managed voicing suite did not produce its parity cases.' }
    } else { Record-Skip 'python-unittests' 'Python/Pillow dependency check failed.';$fixturesReady=$false }
    if ($built) {
        $app=Binary 'Teclas.App' 'Teclas.Partituras.exe'
        [void](Invoke-Check 'wpf-ui' $app @('--verify-v018-ui',(Join-Path $reports 'wpf-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-ui.json'))
        [void](Invoke-Check 'wpf-v019-ui' $app @('--verify-v019-ui',(Join-Path $reports 'wpf-v019-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v019-ui.json'))
        [void](Invoke-Check 'wpf-diagram-preview-ui' $app @('--verify-diagram-preview-ui',(Join-Path $reports 'wpf-diagram-preview-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-diagram-preview-ui.json'))
        [void](Invoke-Check 'wpf-library-playback-ui' $app @('--verify-library-playback-ui',(Join-Path $reports 'wpf-library-playback-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-library-playback-ui.json'))
        [void](Invoke-Check 'wpf-library-group-ui' $app @('--verify-library-group-ui',(Join-Path $reports 'wpf-library-group-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-library-group-ui.json'))
        [void](Invoke-Check 'wpf-audio-preparation-ui' $app @('--verify-audio-preparation-ui',(Join-Path $reports 'wpf-audio-preparation-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-audio-preparation-ui.json'))
        [void](Invoke-Check 'wpf-output-directory-ui' $app @('--verify-output-directory-ui',(Join-Path $reports 'wpf-output-directory-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-output-directory-ui.json'))
        [void](Invoke-Check 'wpf-score-audio-ui' $app @('--verify-score-audio-ui',(Join-Path $reports 'wpf-score-audio-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-score-audio-ui.json'))
        [void](Invoke-Check 'wpf-v028-ui' $app @('--verify-v028-ui',(Join-Path $reports 'wpf-v028-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v028-ui.json'))
        [void](Invoke-Check 'wpf-v029-ui' $app @('--verify-v029-ui',(Join-Path $reports 'wpf-v029-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v029-ui.json'))
        [void](Invoke-Check 'wpf-v030-ui' $app @('--verify-v030-ui',(Join-Path $reports 'wpf-v030-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v030-ui.json'))
        [void](Invoke-Check 'wpf-v031-ui' $app @('--verify-v031-ui',(Join-Path $reports 'wpf-v031-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v031-ui.json'))
        [void](Invoke-Check 'wpf-v032-ui' $app @('--verify-v032-ui',(Join-Path $reports 'wpf-v032-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v032-ui.json'))
        [void](Invoke-Check 'wpf-v033-ui' $app @('--verify-v033-ui',(Join-Path $reports 'wpf-v033-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v033-ui.json'))
        [void](Invoke-Check 'wpf-v034-ui' $app @('--verify-v034-ui',(Join-Path $reports 'wpf-v034-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v034-ui.json'))
        [void](Invoke-Check 'wpf-v035-ui' $app @('--verify-v035-ui',(Join-Path $reports 'wpf-v035-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v035-ui.json'))
        [void](Invoke-Check 'wpf-v036-ui' $app @('--verify-v036-ui',(Join-Path $reports 'wpf-v036-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v036-ui.json'))
        [void](Invoke-Check 'wpf-v037-ui' $app @('--verify-v037-ui',(Join-Path $reports 'wpf-v037-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v037-ui.json'))
        [void](Invoke-Check 'wpf-v038-ui' $app @('--verify-v038-ui',(Join-Path $reports 'wpf-v038-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v038-ui.json'))
        [void](Invoke-Check 'wpf-v039-ui' $app @('--verify-v039-ui',(Join-Path $reports 'wpf-v039-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v039-ui.json'))
        [void](Invoke-Check 'wpf-v040-ui' $app @('--verify-v040-ui',(Join-Path $reports 'wpf-v040-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v040-ui.json'))
        [void](Invoke-Check 'wpf-v041-ui' $app @('--verify-v041-ui',(Join-Path $reports 'wpf-v041-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v041-ui.json'))
        [void](Invoke-Check 'wpf-v042-ui' $app @('--verify-v042-ui',(Join-Path $reports 'wpf-v042-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v042-ui.json'))
        [void](Invoke-Check 'wpf-v043-ui' $app @('--verify-v043-ui',(Join-Path $reports 'wpf-v043-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v043-ui.json'))
        [void](Invoke-Check 'wpf-v044-ui' $app @('--verify-v044-ui',(Join-Path $reports 'wpf-v044-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v044-ui.json'))
        [void](Invoke-Check 'wpf-v045-ui' $app @('--verify-v045-ui',(Join-Path $reports 'wpf-v045-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v045-ui.json'))
        [void](Invoke-Check 'wpf-v046-ui' $app @('--verify-v046-ui',(Join-Path $reports 'wpf-v046-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v046-ui.json'))
        [void](Invoke-Check 'wpf-v047-ui' $app @('--verify-v047-ui',(Join-Path $reports 'wpf-v047-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v047-ui.json'))
        [void](Invoke-Check 'wpf-v048-ui' $app @('--verify-v048-ui',(Join-Path $reports 'wpf-v048-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v048-ui.json'))
        [void](Invoke-Check 'wpf-v049-ui' $app @('--verify-v049-ui',(Join-Path $reports 'wpf-v049-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v049-ui.json'))
        [void](Invoke-Check 'wpf-v052-ui' $app @('--verify-v052-ui',(Join-Path $reports 'wpf-v052-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v052-ui.json'))
        [void](Invoke-Check 'wpf-batch-guard-ui' $app @('--verify-batch-guard-ui',(Join-Path $reports 'wpf-batch-guard-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-batch-guard-ui.json'))
        [void](Invoke-Check 'wpf-v190-triage-ui' $app @('--verify-v190-triage-ui',(Join-Path $reports 'wpf-v190-triage-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v190-triage-ui.json'))
        [void](Invoke-Check 'wpf-v190-review-ui' $app @('--verify-v190-review-ui',(Join-Path $reports 'wpf-v190-review-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-v190-review-ui.json'))
        [void](Invoke-Check 'wpf-recorded-melody-ui' $app @('--verify-recorded-melody-ui',(Join-Path $reports 'wpf-recorded-melody-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-recorded-melody-ui.json'))
        [void](Invoke-Check 'wpf-bulk-pdf-ui' $app @('--verify-bulk-pdf-ui',(Join-Path $reports 'wpf-bulk-pdf-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-bulk-pdf-ui.json'))
        [void](Invoke-Check 'wpf-track-audition-ui' $app @('--verify-track-audition',(Join-Path $reports 'wpf-track-audition-ui.json'),'--synthetic') $work -JsonReport (Join-Path $reports 'wpf-track-audition-ui.json'))
        [void](Invoke-Check 'wpf-track-mix-ui' $app @('--verify-track-mix-ui',(Join-Path $reports 'wpf-track-mix-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-track-mix-ui.json'))
        [void](Invoke-Check 'wpf-score-flow-ui' $app @('--verify-score-flow-ui',(Join-Path $reports 'wpf-score-flow-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-score-flow-ui.json'))
        [void](Invoke-Check 'wpf-diagram-propagation-ui' $app @('--verify-diagram-propagation-ui',(Join-Path $reports 'wpf-diagram-propagation-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-diagram-propagation-ui.json'))
        [void](Invoke-Check 'wpf-midi-folder-ui' $app @('--verify-midi-folder-ui',(Join-Path $reports 'wpf-midi-folder-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-midi-folder-ui.json'))
        [void](Invoke-Check 'wpf-folder-watch-ui' $app @('--verify-folder-watch-ui',(Join-Path $reports 'wpf-folder-watch-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-folder-watch-ui.json'))
        [void](Invoke-Check 'wpf-metronome-ui'$app @('--verify-metronome-ui',(Join-Path $reports 'wpf-metronome-ui.json')) $work -JsonReport (Join-Path $reports 'wpf-metronome-ui.json'))
        if ($fixturesReady) {
            [void](Invoke-Check 'wpf-preferences-ui' $app @('--verify-preferences',(Join-Path $reports 'wpf-preferences-ui.json'),(Join-Path $fixtures 'synthetic.mid')) $work -JsonReport (Join-Path $reports 'wpf-preferences-ui.json'))
        } else { Record-Skip 'wpf-preferences-ui' 'Synthetic MIDI fixture was not created.' }
        [void](Invoke-Check 'synthetic-benchmarks' $dotnet @((Binary 'Teclas.Benchmarks' ''),'--output',(Join-Path $reports 'benchmarks.json'),'--iterations',"$BenchmarkIterations") $work -JsonReport (Join-Path $reports 'benchmarks.json'))
    } else { Record-Skip 'wpf-ui' 'Solution build failed.';Record-Skip 'synthetic-benchmarks' 'Solution build failed.' }
    $windowsPowerShell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    [void](Invoke-Check 'installer-runtime' $windowsPowerShell @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $repo 'tests\test_installer_runtime.ps1'),'-OutputDirectory',(Join-Path $work 'installer-runtime')) $work -JsonReport (Join-Path $work 'installer-runtime\report.json'))
    if ($MuseScorePath -and $built -and $fixturesReady) {
        if (-not [IO.File]::Exists($MuseScorePath)) { throw 'The explicitly requested MuseScore executable does not exist.' }
        $native=Join-Path $fixtures 'synthetic.mscz'
        $nativeReady=Invoke-Check 'native-synthetic-fixture' ([IO.Path]::GetFullPath($MuseScorePath)) @('-o',$native,(Join-Path $fixtures 'synthetic.musicxml')) $fixtures
        if ($nativeReady) {
            foreach ($name in @('Teclas.BatchPdf.Tests','Teclas.Study.Tests','Teclas.PlaybackMap.Tests')) {
                if ($SkipNativeSuites -contains $name) { Record-Skip ('native-'+$name) ($(if ($SkipNativeReason) { $SkipNativeReason } else { 'Left out on request.' })); continue }
                $nativeWork=Join-Path $work ('native-'+$name);[void][IO.Directory]::CreateDirectory($nativeWork)
                $nativeArgs=if($name -eq 'Teclas.BatchPdf.Tests'){@($nativeWork,$native,$MuseScorePath)}else{@($nativeWork,$MuseScorePath)}
                [void](Invoke-Check ('native-'+$name) $dotnet (@((Binary $name ''))+$nativeArgs) $nativeWork)
            }
        } else { Record-Skip 'native-suites' 'Synthetic native fixture conversion failed.' }
    } else { Record-Skip 'native-rendering' 'MuseScore integration is opt-in: pass -MuseScorePath to run synthetic PDF, study-audio and playback-map checks. No native renderer was installed or inferred.' }
    Record-Skip 'private-collection-regression' 'Personal MIDI inventories and local QA collections are intentionally excluded.'
    Record-Skip 'native-specialized-score-fixtures' 'Native instrument-label, recognized-diagram editing and arrangement branches require their separate score fixtures; their synthetic managed/unit cases run above.'
    Record-Skip 'audio-device-playback' 'Real audio-device playback requires an interactive local session; synthetic MIDI transport tests still run.'
} catch {
    $results.Add([pscustomobject]@{name='runner';status='failed';seconds=0;exitCode=-1;reason=$_.Exception.Message;log=$null})
    Write-Host ('FAILED runner: '+$_.Exception.Message)
} finally {
    $failed=@($results|Where-Object status -eq 'failed').Count
    $passed=@($results|Where-Object status -eq 'passed').Count
    $skipped=@($results|Where-Object status -eq 'skipped').Count
    $sourceCommit=if($env:GITHUB_SHA -match '^[0-9a-fA-F]{40}$'){$env:GITHUB_SHA}else{$null}
    $report=[ordered]@{schemaVersion=1;status=$(if($failed){'failed'}else{'passed'});sourceCommit=$sourceCommit;startedUtc=$started.ToString('O');completedUtc=[DateTimeOffset]::UtcNow.ToString('O');passed=$passed;failed=$failed;skipped=$skipped;desktop=$desktopNote;steps=$results.ToArray()}
    $report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $reports 'ci-report.json') -Encoding utf8
    $summary=@('# TeclaScore validation', '',"Passed: $passed · Failed: $failed · Skipped: $skipped",'', '| Check | Result | Seconds | Note |','| --- | --- | ---: | --- |')
    foreach($result in $results){$summary+='| '+$result.name+' | '+$result.status+' | '+$result.seconds+' | '+($result.reason -replace '[\r\n|]',' ')+' |'}
    $summary+='';$summary+='Timing is observational. Shared-runner speed is not a pass/fail threshold.';$summary+='Checks ran on the '+$desktopNote+'.'
    [TeclasCi.CiProcess]::CloseHiddenDesktop($hiddenDesktopHandle)
    $summary|Set-Content -LiteralPath (Join-Path $reports 'summary.md') -Encoding utf8
    if($env:GITHUB_STEP_SUMMARY){$summary|Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8}
    Write-Host "CI result: $passed passed, $failed failed, $skipped skipped. Reports: $reports"
}
if ($failed) { exit 1 }
exit 0
