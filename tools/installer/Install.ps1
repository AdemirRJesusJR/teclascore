[CmdletBinding()]
param(
    [string]$SourceDirectory = $PSScriptRoot,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\TeclaScore'),
    [switch]$NoShortcuts,
    [switch]$PrepareComputer
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Versioned folders keep a running/previous version intact. Settings are stored
# separately by the application and are never read or changed by this installer.
function Assert-ChildPath([string]$Base, [string]$Candidate) {
    $basePath = [IO.Path]::GetFullPath($Base).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $candidatePath = [IO.Path]::GetFullPath($Candidate)
    if (-not $candidatePath.StartsWith($basePath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Caminho fora da pasta de instalacao: $candidatePath"
    }
    return $candidatePath
}
function Write-Shortcut([string]$Path, [string]$Executable) {
    $shortcut = $script:shell.CreateShortcut($Path)
    $shortcut.TargetPath = $Executable
    $shortcut.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
    $shortcut.IconLocation = "$Executable,0"
    $shortcut.Description = 'TeclaScore - estudio de partituras para teclado'
    $shortcut.Save()
}

$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$manifestPath = Join-Path $source 'installation-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Pacote incompleto: manifesto de instalacao ausente.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Versao invalida no manifesto.' }
if ($manifest.product -ne 'TeclaScore') { throw 'O pacote nao pertence ao TeclaScore.' }
$entries = @($manifest.files.PSObject.Properties)
if ($entries.Count -lt 5 -or $entries.Count -gt 10000) { throw 'Manifesto de instalacao invalido.' }
foreach ($required in @('Teclas.Partituras.exe', 'Teclas.Partituras.dll', 'Teclas.Core.dll', 'Teclas.Partituras.runtimeconfig.json', 'engine/worker.py', 'RuntimePrerequisite.ps1')) {
    if ($required -notin $entries.Name) { throw "Arquivo obrigatorio nao listado: $required" }
}
foreach ($entry in $entries) {
    if ($entry.Value -notmatch '^[a-fA-F0-9]{64}$' -or $entry.Name -match '(^[/\\])|(^|[/\\])\.\.?([/\\]|$)|:') { throw 'Entrada invalida no manifesto.' }
    $file = Assert-ChildPath $source (Join-Path $source $entry.Name)
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Arquivo ausente: $($entry.Name)" }
    if ((Get-Item -LiteralPath $file).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Links de arquivos nao sao permitidos no pacote.' }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Value) { throw "Arquivo alterado ou corrompido: $($entry.Name)" }
}

# Load the prerequisite helper only after every package hash has been verified.
. (Join-Path $source 'RuntimePrerequisite.ps1')
$runtimeStatus = Get-TeclaScoreRuntimeStatus (Join-Path $source 'Teclas.Partituras.exe')

$installBase = [IO.Path]::GetFullPath($InstallRoot)
$versions = Assert-ChildPath $installBase (Join-Path $installBase 'versions')
$installId = $manifest.version + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$destination = Assert-ChildPath $versions (Join-Path $versions $installId)
$pending = Assert-ChildPath $versions (Join-Path $versions ('.pending-' + [Guid]::NewGuid().ToString('N')))
$shortcutBackups = @()
$createdShortcuts = @()
$promoted = $false
$complete = $false
try {
    New-Item -ItemType Directory -Path $pending -Force | Out-Null
    foreach ($entry in $entries) {
        $sourceFile = Assert-ChildPath $source (Join-Path $source $entry.Name)
        $targetFile = Assert-ChildPath $pending (Join-Path $pending $entry.Name)
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($targetFile)) -Force | Out-Null
        Copy-Item -LiteralPath $sourceFile -Destination $targetFile
        if ((Get-FileHash -LiteralPath $targetFile -Algorithm SHA256).Hash -ne $entry.Value) { throw "Falha ao conferir a copia: $($entry.Name)" }
    }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $pending 'installation-manifest.json')
    # Both resolved absolute paths are within the exact managed versions folder.
    $pending = Assert-ChildPath $versions $pending
    $destination = Assert-ChildPath $versions $destination
    Move-Item -LiteralPath $pending -Destination $destination
    $promoted = $true
    $executable = Join-Path $destination 'Teclas.Partituras.exe'
    if (-not $NoShortcuts) {
        $script:shell = New-Object -ComObject WScript.Shell
        $desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'TeclaScore.lnk'
        $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'TeclaScore'
        New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
        $menuShortcut = Join-Path $startMenu 'TeclaScore.lnk'
        foreach ($shortcutPath in @($desktopShortcut, $menuShortcut)) {
            if (Test-Path -LiteralPath $shortcutPath) {
                $backupPath = Join-Path $destination ([Guid]::NewGuid().ToString('N') + '.shortcut-backup')
                Copy-Item -LiteralPath $shortcutPath -Destination $backupPath
                $shortcutBackups += [PSCustomObject]@{ Path = $shortcutPath; Backup = $backupPath }
            } else { $createdShortcuts += $shortcutPath }
            Write-Shortcut $shortcutPath $executable
        }
    }
    $complete = $true
    Write-Output ("TeclaScore {0} instalado em {1}" -f $manifest.version, $destination)
    Write-Output 'Biblioteca, configuracoes e partituras anteriores preservadas.'
    Write-Output 'Requisitos: .NET Desktop Runtime 10, MuseScore 4 e Python 3.11+ com Pillow.'
    try { Show-TeclaScoreRuntimeGuidance $runtimeStatus -Interactive:$PrepareComputer }
    catch { Write-Output 'Instalacao concluida. Consulte LEIA-ME.txt para preparar as dependencias.' }
}
finally {
    if (-not $complete) {
        foreach ($backup in $shortcutBackups) { Copy-Item -LiteralPath $backup.Backup -Destination $backup.Path -Force }
        foreach ($shortcutPath in $createdShortcuts) { if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath } }
        # Delete only this failed, newly created installation; never previous versions.
        $failedPath = if ($promoted) { $destination } else { $pending }
        $failedPath = Assert-ChildPath $versions $failedPath
        if (Test-Path -LiteralPath $failedPath) { Remove-Item -LiteralPath $failedPath -Recurse -Force }
    } else {
        foreach ($backup in $shortcutBackups) { if (Test-Path -LiteralPath $backup.Backup) { Remove-Item -LiteralPath $backup.Backup } }
    }
}
