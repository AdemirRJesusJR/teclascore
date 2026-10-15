param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $root -Force | Out-Null
$helper=Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\installer\RuntimePrerequisite.ps1'
. $helper
$checks=New-Object 'System.Collections.Generic.List[string]'
function Assert($Value,[string]$Name) { if(-not $Value){throw $Name};$checks.Add($Name) }
$complete="Microsoft.NETCore.App 10.0.10 [C:\dotnet\shared\Microsoft.NETCore.App]`nMicrosoft.WindowsDesktop.App 10.0.10 [C:\dotnet\shared\Microsoft.WindowsDesktop.App]"
Assert (Test-TeclaScoreRuntimeText $complete) 'desktop-and-core-10-accepted'
foreach($invalid in @('Microsoft.NETCore.App 10.0.10 [C:\dotnet]', 'Microsoft.WindowsDesktop.App 10.0.10 [C:\dotnet]', ($complete -replace '10\.0\.10','9.0.10'), ($complete -replace '10\.0\.10','10.0.0-preview.7'), 'Microsoft.AspNetCore.App 10.0.10 [C:\dotnet]')) {
    Assert (-not (Test-TeclaScoreRuntimeText $invalid)) ('reject-incomplete-or-incompatible-'+$checks.Count)
}
foreach($entry in @(@(0x8664,'x64'),@(0x014c,'x86'),@(0xaa64,'arm64'))) {
    $path=Join-Path $root ($entry[1]+'.fixture')
    $bytes=New-Object byte[] 128
    [BitConverter]::GetBytes([uint16]0x5a4d).CopyTo($bytes,0)
    [BitConverter]::GetBytes([int32]64).CopyTo($bytes,60)
    [BitConverter]::GetBytes([uint32]0x4550).CopyTo($bytes,64)
    [BitConverter]::GetBytes([uint16]$entry[0]).CopyTo($bytes,68)
    [IO.File]::WriteAllBytes($path,$bytes)
    Assert ((Get-TeclaScoreArchitecture $path) -eq $entry[1]) ('architecture-'+$entry[1])
    $otherArchitecture=if($entry[1] -eq 'x64'){'x86'}else{'x64'}
    Assert (-not (Test-TeclaScoreRuntimeExecutable $path $otherArchitecture)) ('wrong-runtime-architecture-rejected-'+$entry[1])
    $bytes[0]=0;[IO.File]::WriteAllBytes($path,$bytes)
    $rejected=$false;try{Get-TeclaScoreArchitecture $path | Out-Null}catch{$rejected=$true}
    Assert $rejected ('reject-invalid-executable-'+$entry[1])
}
# Pure discovery inputs: no process/user/machine environment or registry changes,
# no installations, no browser and no launch of fixture executables.
$environmentBefore=[Environment]::GetEnvironmentVariables()
$values=@{'DOTNET_ROOT_X64'='C:\private-x64';'DOTNET_ROOT_X86'='C:\private-x86';'DOTNET_ROOT_ARM64'='C:\private-arm64';
    'DOTNET_ROOT(x86)'='C:\legacy-x86';'DOTNET_ROOT'='C:\generic';'ProgramFiles'='C:\Program Files';
    'ProgramW6432'='C:\Program Files';'ProgramFiles(x86)'='C:\Program Files (x86)';'PATH'='C:\path-only'}
$exists={param($Path) $Path -notlike '*hostfxr.dll' -and $Path -ne 'C:\nonexistent'}
foreach($arch in @('x64','x86','arm64')) {
    $selection=Resolve-TeclaScoreRuntimeRoot $arch 'arm64' $values 'C:\registered' 'C:\app' $exists
    Assert ($selection.Root -eq ('C:\private-'+$arch) -and $selection.Source -eq ('DOTNET_ROOT_'+$arch.ToUpperInvariant())) ('app-architecture-env-priority-'+$arch)
}
$legacy=$values.Clone();$legacy.Remove('DOTNET_ROOT_X86')
$selection=Resolve-TeclaScoreRuntimeRoot 'x86' 'x64' $legacy 'C:\registered' 'C:\app' $exists
Assert ($selection.Root -eq 'C:\legacy-x86') 'wow64-legacy-root-before-generic'
$selection=Resolve-TeclaScoreRuntimeRoot 'x86' 'x86' $legacy 'C:\registered' 'C:\app' $exists
Assert ($selection.Root -eq 'C:\generic') 'native-x86-ignores-wow64-root'
$generic=$values.Clone();$generic.Remove('DOTNET_ROOT_X64')
$selection=Resolve-TeclaScoreRuntimeRoot 'x64' 'x64' $generic 'C:\registered' 'C:\app' $exists
Assert ($selection.Root -eq 'C:\generic') 'x64-ignores-legacy-x86-root'
$generic['DOTNET_ROOT_X64']='C:\nonexistent'
$selection=Resolve-TeclaScoreRuntimeRoot 'x64' 'x64' $generic 'C:\registered' 'C:\app' $exists
Assert ($selection.Root -eq 'C:\generic') 'nonexistent-env-root-falls-through-like-apphost'
$global=$values.Clone()
foreach($name in @('DOTNET_ROOT_X64','DOTNET_ROOT_X86','DOTNET_ROOT_ARM64','DOTNET_ROOT(x86)','DOTNET_ROOT')) { $global.Remove($name) }
$selection=Resolve-TeclaScoreRuntimeRoot 'x64' 'x64' $global 'C:\registered' 'C:\app' $exists
Assert ($selection.Root -eq 'C:\registered' -and $selection.Source -eq 'Registry32') 'registered-root-before-default'
$selection=Resolve-TeclaScoreRuntimeRoot 'x64' 'x64' $global 'C:\nonexistent' 'C:\app' $exists
Assert ($selection.Root -eq 'C:\nonexistent') 'missing-registered-root-does-not-fall-through'
$selection=Resolve-TeclaScoreRuntimeRoot 'x64' 'x64' $global '' 'C:\app' $exists
Assert ($selection.Source -eq 'Registry32' -and -not $selection.Root) 'empty-registered-root-does-not-certify-default'
foreach($entry in @(@('x64','x64','C:\Program Files\dotnet'),@('x86','x64','C:\Program Files (x86)\dotnet'),
    @('arm64','arm64','C:\Program Files\dotnet'),@('x64','arm64','C:\Program Files\dotnet\x64'))) {
    $selection=Resolve-TeclaScoreRuntimeRoot $entry[0] $entry[1] $global $null 'C:\app' $exists
    Assert ($selection.Root -eq $entry[2]) ('default-root-'+$entry[0]+'-on-'+$entry[1])
}
$fromX86=$global.Clone();$fromX86['ProgramFiles']='C:\Program Files (x86)';$fromX86['PROCESSOR_ARCHITECTURE']='x86'
$selection=Resolve-TeclaScoreRuntimeRoot 'x64' 'x64' $fromX86 $null 'C:\app' $exists
Assert ($selection.Root -eq 'C:\Program Files\dotnet') 'x86-installer-resolves-x64-program-files'
$nativeX64=$global.Clone();$nativeX64['PROCESSOR_ARCHITECTURE']='AMD64';$nativeX64['ProgramW6432']='C:\unrelated'
$selection=Resolve-TeclaScoreRuntimeRoot 'x64' 'x64' $nativeX64 $null 'C:\app' $exists
Assert ($selection.Root -eq 'C:\Program Files\dotnet') 'native-x64-apphost-uses-programfiles'
$nativeX86=@{'ProgramFiles'='C:\Program Files'}
$selection=Resolve-TeclaScoreRuntimeRoot 'x86' 'x86' $nativeX86 $null 'C:\app' $exists
Assert ($selection.Root -eq 'C:\Program Files\dotnet') 'x86-os-default-root'
$calls=New-Object 'System.Collections.Generic.List[string]'
$probe={param($Candidate,$Arch) $calls.Add($Candidate);return ($Candidate -eq 'C:\registered\dotnet.exe')}
$selection=Resolve-TeclaScoreRuntimeRoot 'x64' 'x64' $values 'C:\registered' 'C:\app' $exists
$status=Test-TeclaScoreSelectedRuntime $selection 'x64' $probe
Assert (-not $status.Ready -and $calls.Count -eq 1 -and $calls[0] -eq 'C:\private-x64\dotnet.exe') 'incomplete-env-root-never-certifies-healthy-global'
$calls.Clear()
$selection=Resolve-TeclaScoreRuntimeRoot 'x64' 'x64' $global 'C:\registered' 'C:\app' $exists
$status=Test-TeclaScoreSelectedRuntime $selection 'x64' $probe
Assert ($status.Ready -and $calls.Count -eq 1 -and $status.DotnetPath -eq 'C:\registered\dotnet.exe') 'selected-complete-global-is-ready'
$calls.Clear()
$selection=Resolve-TeclaScoreRuntimeRoot 'x64' 'x64' @{'PATH'='C:\registered'} $null 'C:\app' $exists
$status=Test-TeclaScoreSelectedRuntime $selection 'x64' $probe
Assert (-not $status.Ready -and $calls.Count -eq 0) 'path-only-runtime-is-not-apphost-ready'
$calls.Clear()
$selection=Resolve-TeclaScoreRuntimeRoot 'x64' 'x64' $global 'C:\registered' 'C:\app' {param($Path) $true}
$status=Test-TeclaScoreSelectedRuntime $selection 'x64' $probe
Assert (-not $status.Ready -and $selection.Source -eq 'AppLocal' -and $calls.Count -eq 0) 'local-hostfxr-does-not-certify-unrelated-global'
$status=Test-TeclaScoreSelectedRuntime ([PSCustomObject]@{Root='C:\broken';Source='DOTNET_ROOT'}) 'x64' {throw 'probe failed'}
Assert (-not $status.Ready) 'failed-probe-is-not-ready'
# Existing but incomplete local fixture takes precedence through the real path check.
$fixtureRoot=Join-Path $root 'empty-runtime'
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
$selection=Resolve-TeclaScoreRuntimeRoot 'x64' 'x64' @{'DOTNET_ROOT_X64'=$fixtureRoot} 'C:\registered' 'C:\app'
$status=Test-TeclaScoreSelectedRuntime $selection 'x64'
Assert (-not $status.Ready -and $selection.Root -eq $fixtureRoot) 'real-empty-private-directory-is-not-ready'
Assert (-not (Test-TeclaScoreRuntimeExecutable (Join-Path $root 'x64.fixture') 'x64')) 'invalid-runtime-executable-is-not-ready'
# Self-contained detection needs both framework declarations and compatible files.
$selfDirectory=Join-Path $root 'self-contained'
New-Item -ItemType Directory -Path $selfDirectory -Force | Out-Null
$selfExecutable=Join-Path $selfDirectory 'Teclas.Partituras.exe'
$selfConfig=Join-Path $selfDirectory 'Teclas.Partituras.runtimeconfig.json'
$nativeBytes=New-Object byte[] 128
[BitConverter]::GetBytes([uint16]0x5a4d).CopyTo($nativeBytes,0)
[BitConverter]::GetBytes([int32]64).CopyTo($nativeBytes,60)
[BitConverter]::GetBytes([uint32]0x4550).CopyTo($nativeBytes,64)
[BitConverter]::GetBytes([uint16]0x8664).CopyTo($nativeBytes,68)
foreach($name in @('Teclas.Partituras.exe','hostfxr.dll','hostpolicy.dll','coreclr.dll','clrjit.dll')) {
    [IO.File]::WriteAllBytes((Join-Path $selfDirectory $name),$nativeBytes)
}
$managedNames=@('Teclas.Partituras.dll','Teclas.Core.dll','System.Private.CoreLib.dll','System.Runtime.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll')
foreach($name in $managedNames) { [IO.File]::WriteAllBytes((Join-Path $selfDirectory $name),[byte[]]@(77,90,1)) }
$included=@(@{name='Microsoft.NETCore.App';version='10.0.10'},@{name='Microsoft.WindowsDesktop.App';version='10.0.10'})
function Write-SelfConfig($Options) { @{runtimeOptions=$Options} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $selfConfig -Encoding UTF8 }
Write-SelfConfig @{tfm='net10.0';includedFrameworks=$included}
Assert (Test-TeclaScoreSelfContainedRuntime $selfExecutable) 'self-contained-complete-x64-layout-accepted'
$status=Get-TeclaScoreRuntimeStatus $selfExecutable
Assert ($status.Ready -and $status.RootSource -eq 'SelfContained' -and $null -eq $status.DotnetPath) 'self-contained-ready-without-global-dotnet-probe'
foreach($name in $managedNames) {
    [IO.File]::WriteAllBytes((Join-Path $selfDirectory $name),[byte[]]@())
    Assert (-not (Test-TeclaScoreSelfContainedRuntime $selfExecutable)) ('self-contained-rejects-empty-'+$name)
    [IO.File]::WriteAllBytes((Join-Path $selfDirectory $name),[byte[]]@(77,90,1))
}
$wrongNative=$nativeBytes.Clone();[BitConverter]::GetBytes([uint16]0x014c).CopyTo($wrongNative,68)
foreach($name in @('hostfxr.dll','hostpolicy.dll','coreclr.dll','clrjit.dll')) {
    [IO.File]::WriteAllBytes((Join-Path $selfDirectory $name),$wrongNative)
    Assert (-not (Test-TeclaScoreSelfContainedRuntime $selfExecutable)) ('self-contained-rejects-wrong-architecture-'+$name)
    [IO.File]::WriteAllBytes((Join-Path $selfDirectory $name),$nativeBytes)
}
foreach($options in @(@{frameworks=$included},@{includedFrameworks=$included;frameworks=$included},
    @{includedFrameworks=$included;framework=@{name='Microsoft.NETCore.App';version='10.0.10'}},
    @{includedFrameworks=@($included[0])},@{includedFrameworks=@($included[1])},
    @{includedFrameworks=@($included[0],$included[1],$included[1])},
    @{includedFrameworks=@($included[0],@{name='Microsoft.WindowsDesktop.App';version='10.0.0-preview.1'})},
    @{includedFrameworks=@(@{name='Microsoft.NETCore.App';version='9.0.10'},$included[1])})) {
    Write-SelfConfig $options
    Assert (-not (Test-TeclaScoreSelfContainedRuntime $selfExecutable)) ('self-contained-rejects-incompatible-config-'+$checks.Count)
}
[IO.File]::WriteAllText($selfConfig,'{bad json')
Assert (-not (Test-TeclaScoreSelfContainedRuntime $selfExecutable)) 'self-contained-rejects-corrupt-json'
[IO.File]::WriteAllText($selfConfig,(' ' * (1MB+1)))
Assert (-not (Test-TeclaScoreSelfContainedRuntime $selfExecutable)) 'self-contained-config-read-is-bounded'
$status=Get-TeclaScoreRuntimeStatus $selfExecutable
Assert (-not $status.Ready -and $status.RootSource -eq 'AppLocal') 'stray-local-hostfxr-never-certifies-global-runtime'
Write-SelfConfig @{tfm='net10.0';includedFrameworks=$included}
# Extract only the publisher's pure artifact validator. Never execute its Git,
# credentials, network or release publication code in this regression suite.
$publisherPath=Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\publish_release.ps1'
$publisherTokens=$null;$publisherErrors=$null
$publisherAst=[Management.Automation.Language.Parser]::ParseFile($publisherPath,[ref]$publisherTokens,[ref]$publisherErrors)
Assert ($publisherErrors.Count -eq 0) 'powershell-parser-publish-release'
$artifactFunction=$publisherAst.Find({param($Node) $Node -is [Management.Automation.Language.FunctionDefinitionAst] -and $Node.Name -eq 'Get-TeclaScoreReleaseArtifacts'},$true)
. ([scriptblock]::Create($artifactFunction.Extent.Text))
$releaseDirectory=Join-Path $root 'release-fixtures'
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$zipName='TeclaScore-0.19.1-Windows.zip';$setupName='TeclaScore-0.19.1-Setup-x64.exe'
[IO.File]::WriteAllText((Join-Path $releaseDirectory $zipName),'ZIP fixture')
[IO.File]::WriteAllText((Join-Path $releaseDirectory $setupName),'EXE fixture')
$zipHash=(Get-FileHash -LiteralPath (Join-Path $releaseDirectory $zipName) -Algorithm SHA256).Hash.ToLowerInvariant()
$setupHash=(Get-FileHash -LiteralPath (Join-Path $releaseDirectory $setupName) -Algorithm SHA256).Hash.ToLowerInvariant()
$sumFile=Join-Path $releaseDirectory 'SHA256SUMS'
$zipLine=$zipHash+'  '+$zipName;$setupLine=$setupHash+'  '+$setupName
[IO.File]::WriteAllText($sumFile,($zipLine+"`r`n"+$setupLine+"`r`n"))
$artifacts=Get-TeclaScoreReleaseArtifacts $releaseDirectory '0.19.1'
Assert ($artifacts.Assets.Count -eq 3 -and $artifacts.ZipSha256 -eq $zipHash) 'publisher-accepts-exact-zip-exe-and-checksums'
[IO.File]::WriteAllText($sumFile,($setupHash.ToUpperInvariant()+' *'+$setupName+"`n"+$zipLine+"`n"))
Assert ((Get-TeclaScoreReleaseArtifacts $releaseDirectory '0.19.1').Assets.Count -eq 3) 'publisher-accepts-checksum-order-and-standard-binary-marker'
foreach($invalidSums in @($zipLine,($zipLine+"`n"+$zipLine+"`n"+$setupLine),
    ($zipLine+"`n"+$setupLine+"`n"+$zipHash+'  unrelated.exe'),
    ($zipLine+"`n"+('0'*64)+'  '+$setupName),($zipLine+"`n"+$setupLine.Replace('Setup','setup')),
    ($zipLine+"`nnot a checksum"),(' '*(1MB+1)))) {
    [IO.File]::WriteAllText($sumFile,$invalidSums)
    $rejected=$false;try{Get-TeclaScoreReleaseArtifacts $releaseDirectory '0.19.1' | Out-Null}catch{$rejected=$true}
    Assert $rejected ('publisher-rejects-invalid-checksum-set-'+$checks.Count)
}
[IO.File]::WriteAllText($sumFile,($zipLine+"`n"+$setupLine+"`n"))
[IO.File]::WriteAllText((Join-Path $releaseDirectory $setupName),'changed EXE fixture')
$rejected=$false;try{Get-TeclaScoreReleaseArtifacts $releaseDirectory '0.19.1' | Out-Null}catch{$rejected=$true}
Assert $rejected 'publisher-rejects-changed-exe'
$environmentAfter=[Environment]::GetEnvironmentVariables()
$environmentUnchanged=$environmentBefore.Count -eq $environmentAfter.Count
foreach($key in $environmentBefore.Keys) { if($environmentBefore[$key] -cne $environmentAfter[$key]) { $environmentUnchanged=$false } }
Assert $environmentUnchanged 'discovery-tests-do-not-change-process-environment'
$ready=Show-TeclaScoreRuntimeGuidance ([PSCustomObject]@{Ready=$true;Architecture='x64';DotnetPath='fixture'})
Assert (($ready -join ' ') -match 'Preparar este computador') 'ready-guides-to-setup'
$missing=Show-TeclaScoreRuntimeGuidance ([PSCustomObject]@{Ready=$false;Architecture='arm64';DotnetPath=$null})
Assert (($missing -join ' ') -match 'Windows arm64') 'missing-shows-architecture'
Assert (($missing -join ' ') -match 'https://dotnet\.microsoft\.com/pt-br/download/dotnet/10\.0') 'missing-shows-official-link-without-opening'
foreach($file in @('Install.ps1','RuntimePrerequisite.ps1')) {
    $tokens=$null;$errors=$null
    [void][Management.Automation.Language.Parser]::ParseFile((Join-Path (Split-Path -Parent $PSScriptRoot) ('tools\installer\'+$file)),[ref]$tokens,[ref]$errors)
    Assert ($errors.Count -eq 0) ('powershell-parser-'+$file)
}
[pscustomobject]@{status='passed';count=$checks.Count;checks=$checks.ToArray();powershell=$PSVersionTable.PSVersion.ToString()} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'report.json') -Encoding UTF8
Write-Output ($checks.Count.ToString()+' verificacoes do preparo de runtime aprovadas.')
