#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$PythonPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Aplicativo\runtime\python.exe')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# One step of every delivery: notes from CHANGELOG since the latest published release, then
# tools/publish_release.ps1 uploads installer, ZIP and SHA256SUMS (verified by hash) and publishes.
$root = Split-Path $PSScriptRoot -Parent
[xml]$project = [IO.File]::ReadAllText((Join-Path $root 'src/Teclas.App/Teclas.App.csproj'))
$version = [string]$project.Project.PropertyGroup.Version
$state = & (Join-Path $PSScriptRoot 'publish_release.ps1') -Version $version -RepositoryDirectory $root | ConvertFrom-Json
$since = if ($state.latestRelease) { ([string]$state.latestRelease).TrimStart('v') } else { $null }
if ($since -eq $version) { [pscustomobject]@{ release = "v$version"; status = 'already-published' } | ConvertTo-Json; return }
$notes = Join-Path ([IO.Path]::GetTempPath()) ("teclascore-notas-$version-" + [Guid]::NewGuid().ToString('N') + '.md')
try {
    $arguments = @((Join-Path $PSScriptRoot 'release_notes.py'), '--output', $notes)
    if ($since) { $arguments += @('--since', $since) }
    & $PythonPath @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Não foi possível gerar as notas da release a partir do CHANGELOG.' }
    & (Join-Path $PSScriptRoot 'publish_release.ps1') -Version $version -RepositoryDirectory $root -NotesPath $notes -Publish
} finally { if (Test-Path -LiteralPath $notes) { Remove-Item -LiteralPath $notes } }
