#requires -Version 7.2
<#
.SYNOPSIS
Tests the official installer on a clean Windows inside Windows Sandbox: silent install, an app smoke test
without MuseScore, silent uninstall and a check that the program folder is gone. Results are written to
qa/sandbox-<id>/resultado/resultado.json.

Windows Sandbox must be enabled once (Windows 11 Pro: "Ativar ou desativar recursos do Windows" →
"Área Restrita do Windows", then restart). This script never enables it: that is a system setting.
#>
param(
    [string]$Installer,
    [int]$TimeoutMinutes = 20
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sandbox = Join-Path $env:WINDIR 'System32\WindowsSandbox.exe'
if (-not (Test-Path -LiteralPath $sandbox)) {
    Write-Host 'O Windows Sandbox não está ativado neste computador.'
    Write-Host 'Ative em "Ativar ou desativar recursos do Windows" → "Área Restrita do Windows", reinicie e rode este script de novo.'
    exit 2
}
if (-not $Installer) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $root 'src\Teclas.App\Teclas.App.csproj') -Raw
    $version = ($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
    $Installer = Join-Path $root "TeclaScore-$version-Setup-x64.exe"
}
if (-not (Test-Path -LiteralPath $Installer)) { throw "Instalador não encontrado: $Installer" }

$job = Join-Path $root ('qa\sandbox-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
$package = Join-Path $job 'pacote'; $results = Join-Path $job 'resultado'
New-Item -ItemType Directory -Force -Path $package, $results | Out-Null
Copy-Item -LiteralPath $Installer -Destination $package

# Runs inside the sandbox as WDAGUtilityAccount, on a Windows without .NET, MuseScore or earlier TeclaScore.
@'
$ErrorActionPreference = 'Continue'
$in = 'C:\TeclaScore\pacote'; $out = 'C:\TeclaScore\resultado'
$result = [ordered]@{ started = (Get-Date).ToString('o'); os = [Environment]::OSVersion.VersionString }
try {
    $setup = Get-ChildItem $in -Filter 'TeclaScore-*-Setup-x64.exe' | Select-Object -First 1
    $result.installer = $setup.Name
    $install = Start-Process $setup.FullName -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$out\instalacao.log`"" -Wait -PassThru
    $result.installExitCode = $install.ExitCode
    $folder = Join-Path $env:LOCALAPPDATA 'Programs\TeclaScore'
    $exe = (Get-ChildItem $folder -Recurse -Filter 'Teclas.Partituras.exe' -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
    $result.installedExecutable = [bool]$exe -and (Test-Path $exe)
    $result.startMenuShortcut = [bool](Get-ChildItem "$env:APPDATA\Microsoft\Windows\Start Menu" -Recurse -Filter 'TeclaScore*.lnk' -ErrorAction SilentlyContinue)
    if ($result.installedExecutable) {
        $smoke = Start-Process $exe -ArgumentList '--verify-v029-ui', "`"$out\reports\smoke.json`"" -Wait -PassThru
        $result.smokeExitCode = $smoke.ExitCode
        $result.smokeStatus = if (Test-Path "$out\reports\smoke.json") { (Get-Content "$out\reports\smoke.json" -Raw | ConvertFrom-Json).status } else { 'sem relatório' }
        $uninstaller = Get-ChildItem $folder -Filter 'unins*.exe' | Select-Object -First 1
        if ($uninstaller) {
            $remove = Start-Process $uninstaller.FullName -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
            $result.uninstallExitCode = $remove.ExitCode
            Start-Sleep -Seconds 3
            $result.removedExecutable = -not (Test-Path $exe)
        } else { $result.uninstallExitCode = 'desinstalador não encontrado' }
    }
} catch { $result.error = $_.ToString() }
$result.passed = $result.installExitCode -eq 0 -and $result.installedExecutable -and $result.smokeStatus -eq 'passed' -and $result.uninstallExitCode -eq 0 -and $result.removedExecutable
$result.finished = (Get-Date).ToString('o')
$result | ConvertTo-Json | Set-Content -Encoding utf8 "$out\resultado.json"
shutdown.exe /s /t 5
'@ | Set-Content -Encoding utf8 (Join-Path $package 'testar.ps1')

$config = @"
<Configuration>
  <Networking>Disable</Networking>
  <vGPU>Disable</vGPU>
  <MappedFolders>
    <MappedFolder><HostFolder>$package</HostFolder><SandboxFolder>C:\TeclaScore\pacote</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$results</HostFolder><SandboxFolder>C:\TeclaScore\resultado</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\TeclaScore\pacote\testar.ps1</Command></LogonCommand>
</Configuration>
"@
$wsb = Join-Path $job 'teste.wsb'
Set-Content -LiteralPath $wsb -Value $config -Encoding utf8
Write-Host "Abrindo o Windows Sandbox com $([IO.Path]::GetFileName($Installer))… a janela fecha sozinha ao terminar."
Start-Process -FilePath $sandbox -ArgumentList "`"$wsb`""
$report = Join-Path $results 'resultado.json'
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while (-not (Test-Path -LiteralPath $report) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
if (-not (Test-Path -LiteralPath $report)) { Write-Host "Sem resultado em $TimeoutMinutes minutos. Veja $results."; exit 1 }
Start-Sleep -Seconds 2
$outcome = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
$outcome | Format-List
if ($outcome.passed) { Write-Host "Instalador aprovado em máquina limpa. Relatório: $report"; exit 0 }
Write-Host "Instalador reprovado em máquina limpa. Relatório: $report"; exit 1
