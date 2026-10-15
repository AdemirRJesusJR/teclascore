#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$PythonPath = 'python',
    [string]$OutputDirectory,
    [string]$MuseScorePath,
    [ValidateRange(1,20)][int]$BenchmarkIterations = 5,
    [ValidateRange(30,1800)][int]$StepTimeoutSeconds = 600
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
        [void]$info.Environment.Remove('TECLAS_TEST_NATIVE_LABELS')
        # ProcessStartInfo bypasses pwsh's automatic WinPSModulePath handling.
        # Let Windows PowerShell reconstruct its own module paths instead of
        # loading incompatible PS7 modules (which hides cmdlets like Get-FileHash).
        # Only the child environment is changed; the runner/user environment stays intact.
        if ([IO.Path]::GetFileName($Executable) -ieq 'powershell.exe') {
            [void]$info.Environment.Remove('PSModulePath')
        }
        $process=[Diagnostics.Process]::Start($info)
        $outTask=$process.StandardOutput.ReadToEndAsync();$errTask=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($Timeout*1000)) {
            $process.Kill($true);[void]$process.WaitForExit(10000);throw "Timed out after $Timeout seconds; owned child processes were stopped."
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
        if ($process -and -not $process.HasExited) { try {$process.Kill($true)} catch {} }
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
        [void](Invoke-Check 'python-unittests' $python @('-B',(Join-Path $PSScriptRoot 'ci\run_python_tests.py'),'--output',(Join-Path $reports 'python-tests.json')) $work -JsonReport (Join-Path $reports 'python-tests.json'))
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
    $report=[ordered]@{schemaVersion=1;status=$(if($failed){'failed'}else{'passed'});sourceCommit=$sourceCommit;startedUtc=$started.ToString('O');completedUtc=[DateTimeOffset]::UtcNow.ToString('O');passed=$passed;failed=$failed;skipped=$skipped;steps=$results.ToArray()}
    $report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $reports 'ci-report.json') -Encoding utf8
    $summary=@('# TeclaScore validation', '',"Passed: $passed · Failed: $failed · Skipped: $skipped",'', '| Check | Result | Seconds | Note |','| --- | --- | ---: | --- |')
    foreach($result in $results){$summary+='| '+$result.name+' | '+$result.status+' | '+$result.seconds+' | '+($result.reason -replace '[\r\n|]',' ')+' |'}
    $summary+='';$summary+='Timing is observational. Shared-runner speed is not a pass/fail threshold.'
    $summary|Set-Content -LiteralPath (Join-Path $reports 'summary.md') -Encoding utf8
    if($env:GITHUB_STEP_SUMMARY){$summary|Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8}
    Write-Host "CI result: $passed passed, $failed failed, $skipped skipped. Reports: $reports"
}
if ($failed) { exit 1 }
exit 0
