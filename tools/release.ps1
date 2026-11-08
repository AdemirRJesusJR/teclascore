#Requires -Version 7.0
[CmdletBinding()]
param(
    # Python used by the build scripts (any 3.11+; the published runtime is the default).
    [string]$PythonPath,
    [string]$CompilerPath = (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
    # Create the annotated tag vX.Y.Z on HEAD and push HEAD and the tag to origin/main.
    [switch]$Tag,
    # Publish the GitHub release after the tag (implies -Tag).
    [switch]$Publish,
    [switch]$AllowDirty
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# One command for a delivery, in the order the build scripts depend on:
# dotnet publish → prepare_distribution → build_installer (official + complete) → package_app --installer
# → verify_delivery → optional tag/push → optional GitHub release. Commit your own files before running it;
# run it from a clean worktree so other sessions' uncommitted work never enters an installer.
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$project = [IO.File]::ReadAllText((Join-Path $root 'src/Teclas.App/Teclas.App.csproj'))
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'A versão do projeto deve ser X.Y.Z.' }
if ($Publish) { $Tag = $true }

function Step([string]$name, [scriptblock]$action) {
    Write-Host "== $name" -ForegroundColor Cyan
    $started = Get-Date; $global:LASTEXITCODE = 0
    & $action
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Falhou: $name (código $LASTEXITCODE)." }
    Write-Host ("   concluído em {0:n0} s" -f ((Get-Date) - $started).TotalSeconds)
}

# Runs tools/<script>.py with tools/ on sys.path: an embedded Python (._pth) ignores the script's own folder.
function Invoke-Tool([string]$script, [string[]]$arguments = @()) {
    $code = "import runpy, sys; sys.path.insert(0, 'tools'); sys.argv = sys.argv[1:]; runpy.run_path(sys.argv[0], run_name='__main__')"
    & $PythonPath -c $code "tools/$script" @arguments
}

Push-Location $root
try {
    $dirty = git status --porcelain --untracked-files=no
    if ($dirty -and -not $AllowDirty) { throw "Há alterações não registradas no Git:`n$($dirty -join "`n")`nFaça o commit ou use -AllowDirty." }
    if (-not (Test-Path -LiteralPath $CompilerPath)) { throw "Inno Setup não encontrado em $CompilerPath." }

    Step "dotnet publish $version" {
        dotnet publish src/Teclas.App/Teclas.App.csproj -c Release -r win-x64 --self-contained true --source https://api.nuget.org/v3/index.json -o Aplicativo
    }
    if (-not $PythonPath) { $PythonPath = Join-Path $root 'Aplicativo\runtime\python.exe' }
    if (-not (Test-Path -LiteralPath $PythonPath)) { throw "Python não encontrado em $PythonPath." }
    Step 'Preparar distribuição' { Invoke-Tool prepare_distribution.py }
    Step 'Instaladores oficial e completo' { & (Join-Path $PSScriptRoot 'build_installer.ps1') -CompilerPath $CompilerPath }
    Step 'Pacote ZIP e checksums' { Invoke-Tool package_app.py @('--installer') }
    Step 'Conferir entrega' { Invoke-Tool verify_delivery.py }

    if ($Tag) {
        $tagName = "v$version"
        if (git tag --list $tagName) { throw "A tag $tagName já existe; tags publicadas não são reescritas." }
        Step "Tag $tagName e push" {
            git tag -a $tagName -m "TeclaScore $version"
            if ($LASTEXITCODE -eq 0) { git push origin HEAD:main }
            if ($LASTEXITCODE -eq 0) { git push origin $tagName }
        }
    }
    if ($Publish) { Step 'Release no GitHub' { & (Join-Path $PSScriptRoot 'release_github.ps1') -PythonPath $PythonPath } }

    Write-Host "`nEntrega $version pronta:" -ForegroundColor Green
    Get-ChildItem -LiteralPath $root -Filter "TeclaScore-$version*" -File | ForEach-Object { Write-Host "  $($_.FullName)" }
    $complete = Join-Path $root 'debug\saida'
    if (Test-Path $complete) { Get-ChildItem -LiteralPath $complete -Filter "TeclaScore-$version*" -File | ForEach-Object { Write-Host "  $($_.FullName)" } }
} finally { Pop-Location }
