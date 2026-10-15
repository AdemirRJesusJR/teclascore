#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CompilerPath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The source is deliberately fixed to this checkout's published application.
# Never enumerate a user's library, AppData, or another installation to build it.
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$payloadRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'Aplicativo'))
$projectPath = Join-Path $projectRoot 'src/Teclas.App/Teclas.App.csproj'
[xml]$project = [IO.File]::ReadAllText($projectPath)
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'A versão do projeto deve ser X.Y.Z.' }
if (-not $OutputDirectory) { $OutputDirectory = $projectRoot }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path
$versionStart = [Diagnostics.ProcessStartInfo]::new()
$versionStart.FileName = $compiler; $versionStart.UseShellExecute = $false; $versionStart.CreateNoWindow = $true
$versionStart.RedirectStandardOutput = $true; $versionStart.RedirectStandardError = $true
$versionStart.ArgumentList.Add('--version')
$versionProcess = [Diagnostics.Process]::new(); $versionProcess.StartInfo = $versionStart
try {
    if (-not $versionProcess.Start()) { throw 'Não foi possível consultar a versão do Inno Setup.' }
    $versionOut = $versionProcess.StandardOutput.ReadToEndAsync(); $versionError = $versionProcess.StandardError.ReadToEndAsync()
    if (-not $versionProcess.WaitForExit(10000)) { $versionProcess.Kill($true); throw 'O compilador não respondeu à consulta de versão.' }
    $versionText = $versionOut.GetAwaiter().GetResult() + $versionError.GetAwaiter().GetResult()
    if ($versionProcess.ExitCode -ne 0 -or $versionText -notmatch '\b(7\.\d+\.\d+)\b') { throw 'Use o compilador oficial Inno Setup 7.' }
    $compilerVersion = $Matches[1]
} finally { $versionProcess.Dispose() }

function Get-PayloadFile([string]$RelativeName) {
    if ([string]::IsNullOrWhiteSpace($RelativeName) -or
        $RelativeName -match '[\x00-\x1f<>:"|?*{}]' -or
        $RelativeName -match '(^[/\\])|(^|[/\\])\.\.?([/\\]|$)' -or
        ($RelativeName -split '[/\\]' | Where-Object { $_ -eq '' -or $_ -match '[ .]$' })) {
        throw "Caminho inválido no pacote: $RelativeName"
    }
    $path = [IO.Path]::GetFullPath((Join-Path $payloadRoot $RelativeName))
    $prefix = $payloadRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or -not [IO.File]::Exists($path)) {
        throw "Arquivo ausente ou fora do pacote: $RelativeName"
    }
    $item = Get-Item -LiteralPath $path -Force
    $current = $item
    while ($null -ne $current) {
        if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Links não são permitidos no pacote: $RelativeName" }
        if ($current.FullName.Equals($payloadRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
        # FileInfo exposes Directory, whereas DirectoryInfo exposes Parent.
        $current = if ($current -is [IO.FileInfo]) { $current.Directory } else { $current.Parent }
    }
    return $path
}

$manifestPath = Get-PayloadFile 'installation-manifest.json'
if ((Get-Item -LiteralPath $manifestPath).Length -gt 5MB) { throw 'Manifesto excessivamente grande.' }
$manifestJson = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($manifestPath))
$verified = [Collections.Generic.List[object]]::new()
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$deliveryDocs = @('LEIA-ME.txt', 'VALIDACAO.txt', 'ROTEIRO-DE-TESTES.txt')
try {
    $manifest = $manifestJson.RootElement
    if ($manifest.GetProperty('product').GetString() -cne 'TeclaScore' -or $manifest.GetProperty('version').GetString() -cne $version) {
        throw 'Produto ou versão do manifesto não corresponde ao projeto.'
    }
    $files = $manifest.GetProperty('files')
    if ($files.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'Lista de arquivos inválida no manifesto.' }
    foreach ($entry in $files.EnumerateObject()) {
        $name = $entry.Name.Replace('\', '/')
        if (-not $names.Add($name) -or $name -in (@('installation-manifest.json') + $deliveryDocs)) { throw "Arquivo duplicado ou reservado no manifesto: $name" }
        if ($entry.Value.ValueKind -ne [Text.Json.JsonValueKind]::String -or $entry.Value.GetString() -notmatch '^[a-fA-F0-9]{64}$') { throw "SHA-256 inválido: $name" }
        $path = Get-PayloadFile $name
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -cne $entry.Value.GetString().ToLowerInvariant()) { throw "Arquivo alterado: $name" }
        $verified.Add([pscustomobject]@{ name = $name; path = $path; sha256 = $hash; bytes = (Get-Item -LiteralPath $path).Length })
    }
} finally { $manifestJson.Dispose() }
if ($verified.Count -lt 5 -or $verified.Count -gt 10000) { throw 'Quantidade de arquivos inválida no manifesto.' }
$manifestFileCount = $verified.Count
foreach ($name in @('Teclas.Partituras.exe', 'Teclas.Partituras.dll', 'Teclas.Core.dll',
    'Teclas.Partituras.runtimeconfig.json', 'Teclas.Partituras.deps.json', 'engine/worker.py',
    'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'clrjit.dll', 'System.Private.CoreLib.dll',
    'System.Runtime.dll', 'WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll')) {
    if (-not $names.Contains($name)) { throw "Arquivo obrigatório não listado no manifesto: $name" }
}
foreach ($name in @('installation-manifest.json') + $deliveryDocs) {
    $path = Get-PayloadFile $name
    $verified.Add([pscustomobject]@{ name = $name; path = $path; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = (Get-Item -LiteralPath $path).Length })
}

# Use the repository's read-only PE/runtime validator, never code from the payload.
. (Join-Path $PSScriptRoot 'installer/RuntimePrerequisite.ps1')
$appExecutable = Join-Path $payloadRoot 'Teclas.Partituras.exe'
if ((Get-TeclaScoreArchitecture $appExecutable) -ne 'x64' -or -not (Test-TeclaScoreSelfContainedRuntime $appExecutable)) {
    throw 'Publique um aplicativo WPF self-contained win-x64 completo antes de criar o Setup.'
}
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $payloadRoot 'Teclas.Partituras.dll')).Version
if ($assemblyVersion.ToString(3) -cne $version) { throw 'A versão do aplicativo publicado não corresponde ao projeto.' }
$appVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($appExecutable).ProductVersion
if ($appVersion -notmatch ('^' + [regex]::Escape($version) + '(?:[.+-]|$)')) { throw 'A versão do executável publicado não corresponde ao projeto.' }
$dependencies = Get-Content -LiteralPath (Join-Path $payloadRoot 'Teclas.Partituras.deps.json') -Raw -Encoding utf8 | ConvertFrom-Json
if ($dependencies.runtimeTarget.name -notmatch '^\.NETCoreApp,Version=v10\.0/win-x64$') { throw 'O pacote deve usar o runtime win-x64 do .NET 10.' }

$buildRoot = Join-Path $projectRoot ('qa/installer-build/' + $version + '-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($buildRoot) | Out-Null
$includePath = Join-Path $buildRoot 'verified-payload.iss'
$lines = [Collections.Generic.List[string]]::new()
foreach ($file in ($verified | Sort-Object name)) {
    $relativeDirectory = [IO.Path]::GetDirectoryName($file.name.Replace('/', '\'))
    $destination = '{app}\versions\' + $version + '-setup-x64'
    if ($relativeDirectory) { $destination += '\' + $relativeDirectory }
    # Source paths are quoted as Inno strings, not shell arguments; no wildcards.
    $source = $file.path.Replace('"', '""').Replace('{', '{{')
    $lines.Add('Source: "' + $source + '"; DestDir: "' + $destination + '"; Flags: ignoreversion')
}
[IO.File]::WriteAllLines($includePath, $lines, [Text.UTF8Encoding]::new($true))
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $compiler; $start.WorkingDirectory = $projectRoot
$start.UseShellExecute = $false; $start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
foreach ($argument in @('--quiet-progress', '--no-ide-signtools', "--define=AppVersion=$version", "--define=PayloadInclude=$includePath", "--define=BuildOutputDir=$buildRoot",
    ('--define=AppIcon=' + (Join-Path $projectRoot 'src/Teclas.App/Assets/teclas.ico')),
    (Join-Path $PSScriptRoot 'installer/TeclaScore.iss'))) { $start.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::new(); $process.StartInfo = $start
try {
    if (-not $process.Start()) { throw 'Não foi possível iniciar o Inno Setup.' }
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(1200000)) { $process.Kill($true); throw 'A compilação do instalador excedeu vinte minutos.' }
    [IO.File]::WriteAllText((Join-Path $buildRoot 'compiler.stdout.txt'), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $buildRoot 'compiler.stderr.txt'), $stderr.GetAwaiter().GetResult())
    if ($process.ExitCode -ne 0) { throw "O Inno Setup falhou com código $($process.ExitCode). Consulte $buildRoot." }
} finally { $process.Dispose() }

# Detect a parallel publish/change during compilation before promoting the output.
foreach ($file in $verified) {
    $path = Get-PayloadFile $file.name
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) { throw "O pacote mudou durante a compilação: $($file.name)" }
}
$filename = "TeclaScore-$version-Setup-x64.exe"
$compiled = Join-Path $buildRoot $filename
if (-not [IO.File]::Exists($compiled)) { throw 'O compilador não produziu o instalador esperado.' }
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$destination = Join-Path $outputRoot $filename
$pendingOutput = Join-Path $outputRoot ('.' + $filename + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::Copy($compiled, $pendingOutput, $false)
    $hash = (Get-FileHash -LiteralPath $pendingOutput -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne (Get-FileHash -LiteralPath $compiled -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Falha ao conferir a cópia final do instalador.' }
    [IO.File]::Move($pendingOutput, $destination, $true)
} finally { if ([IO.File]::Exists($pendingOutput)) { [IO.File]::Delete($pendingOutput) } }
$report = [ordered]@{
    status = 'passed'; product = 'TeclaScore'; version = $version; architecture = 'win-x64'; selfContained = $true
    installer = $destination; sha256 = $hash; bytes = (Get-Item -LiteralPath $destination).Length
    compilerVersion = $compilerVersion; compilerSha256 = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash.ToLowerInvariant()
    manifestFiles = $manifestFileCount; installedFiles = $verified.Count; payloadBytes = ($verified | Measure-Object bytes -Sum).Sum
    generatedInclude = $includePath; files = @($verified | Select-Object name, sha256, bytes)
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$reportPath = Join-Path $buildRoot 'installer-report.json'
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ installer = $destination; version = $version; sha256 = $hash; installedFiles = $verified.Count; report = $reportPath } | ConvertTo-Json
