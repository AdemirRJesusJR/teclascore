[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory=$true)][string]$RepositoryDirectory,
    [string]$PackageDirectory = (Split-Path $PSScriptRoot -Parent),
    [string]$NotesPath,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Get-TeclaScoreReleaseArtifacts([string]$Directory, [string]$ReleaseVersion) {
    $package = Join-Path $Directory ('TeclaScore-' + $ReleaseVersion + '-Windows.zip')
    $setup = Join-Path $Directory ('TeclaScore-' + $ReleaseVersion + '-Setup-x64.exe')
    $sums = Join-Path $Directory 'SHA256SUMS'
    foreach ($file in @($package, $setup, $sums)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw ('Artefato de release ausente: ' + [IO.Path]::GetFileName($file)) }
    }
    if ((Get-Item -LiteralPath $sums).Length -gt 1MB) { throw 'SHA256SUMS excede o limite permitido.' }
    $expected = @{}
    foreach ($file in @($package, $setup)) {
        $expected[[IO.Path]::GetFileName($file)] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $seen = @{}
    foreach ($line in Get-Content -LiteralPath $sums) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^([0-9a-fA-F]{64})\s+\*?([^\r\n]+)$') { throw 'Linha invalida no SHA256SUMS.' }
        $checksum = $matches[1]; $name = $matches[2]
        if ($name -cnotin @($expected.Keys) -or $seen.ContainsKey($name) -or $checksum -ine $expected[$name]) {
            throw 'SHA256SUMS deve conter uma entrada correta e unica para o ZIP e o instalador EXE.'
        }
        $seen[$name] = $true
    }
    if ($seen.Count -ne 2) { throw 'SHA256SUMS deve conter os hashes do ZIP e do instalador EXE.' }
    return [PSCustomObject]@{ Package=$package; Setup=$setup; Sums=$sums;
        ZipSha256=$expected[[IO.Path]::GetFileName($package)]; Assets=@($package, $setup, $sums) }
}

$repoName = 'AdemirRJesusJR/teclascore'
$api = 'https://api.github.com/repos/' + $repoName
$headers = @{ 'User-Agent'='TeclaScore-release-publisher'; 'Accept'='application/vnd.github+json'; 'X-GitHub-Api-Version'='2026-03-10' }
$credentialOutput = $null
$credential = $null
try {
    $remote = (& git -C $RepositoryDirectory remote get-url origin).Trim()
    if ($LASTEXITCODE -ne 0 -or $remote -notin @('https://github.com/AdemirRJesusJR/teclascore.git','https://github.com/AdemirRJesusJR/teclascore','git@github.com:AdemirRJesusJR/teclascore.git')) { throw 'O remote origin nao e o repositorio autorizado do TeclaScore.' }
    # Use the user's existing Git credential helper, never a token in source/args/logs.
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.Arguments = '-c credential.interactive=false credential fill'
    $start.WorkingDirectory = $RepositoryDirectory
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.EnvironmentVariables['GIT_TERMINAL_PROMPT'] = '0'
    $process = [Diagnostics.Process]::Start($start)
    $process.StandardInput.Write("protocol=https`nhost=github.com`n`n"); $process.StandardInput.Close()
    $outputTask = $process.StandardOutput.ReadToEndAsync(); $errorTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'O helper Git excedeu o tempo de espera.' }
    $credentialOutput = $outputTask.GetAwaiter().GetResult()
    $null = $errorTask.GetAwaiter().GetResult()
    if ($process.ExitCode -eq 0) {
        $credential = @{}
        foreach ($line in ($credentialOutput -split "`n")) { if ($line -match '^([^=]+)=(.*)$') { $credential[$matches[1]] = $matches[2].TrimEnd("`r") } }
        if ($credential.ContainsKey('password')) { $headers['Authorization'] = 'Bearer ' + $credential['password'] }
    }
    $process.Dispose()
    $repository = Invoke-RestMethod -Uri $api -Headers $headers -TimeoutSec 30
    if (-not $Publish) {
        $latest = $null
        try { $latest = Invoke-RestMethod -Uri ($api + '/releases/latest') -Headers $headers -TimeoutSec 30 }
        catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
        [PSCustomObject]@{ repository=$repository.full_name; private=$repository.private; defaultBranch=$repository.default_branch; latestRelease=$(if($latest){$latest.tag_name}else{$null}); dryRun=$true } | ConvertTo-Json
        return
    }
    if (-not $headers.ContainsKey('Authorization')) { throw 'Nao ha credencial Git existente para publicar a release.' }
    $tag = 'v' + $Version
    $localCommit = (& git -C $RepositoryDirectory rev-parse ($tag + '^{commit}')).Trim()
    if ($LASTEXITCODE -ne 0 -or $localCommit -notmatch '^[a-f0-9]{40}$') { throw 'Crie e confira a tag local antes de publicar.' }
    $remoteTag = Invoke-RestMethod -Uri ($api + '/git/ref/tags/' + $tag) -Headers $headers -TimeoutSec 30
    $remoteObject = $remoteTag.object
    if ($remoteObject.type -eq 'tag') { $remoteObject = (Invoke-RestMethod -Uri ($api + '/git/tags/' + $remoteObject.sha) -Headers $headers -TimeoutSec 30).object }
    if ($remoteObject.type -ne 'commit' -or $remoteObject.sha -ne $localCommit) { throw 'A tag remota nao aponta para o commit local validado.' }
    $artifacts = Get-TeclaScoreReleaseArtifacts $PackageDirectory $Version
    $hash = $artifacts.ZipSha256
    if (-not $NotesPath -or -not (Test-Path -LiteralPath $NotesPath -PathType Leaf)) { throw 'Forneca NotesPath com as notas de versao revisadas.' }
    $release = $null
    try { $release = Invoke-RestMethod -Uri ($api + '/releases/tags/' + $tag) -Headers $headers -TimeoutSec 30 }
    catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw } }
    if (-not $release) {
        $body = @{ tag_name=$tag; target_commitish=$localCommit; name=('TeclaScore ' + $Version); body=(Get-Content -LiteralPath $NotesPath -Raw); draft=$true; prerelease=$false } | ConvertTo-Json
        $release = Invoke-RestMethod -Method Post -Uri ($api + '/releases') -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 30
    }
    if ($release.prerelease) { throw 'A release existente esta marcada como pre-release; confira-a antes de continuar.' }
    $allowedNames = @($artifacts.Assets | ForEach-Object { [IO.Path]::GetFileName($_) })
    if (@($release.assets | Where-Object { $_.name -cnotin $allowedNames }).Count -gt 0) {
        throw 'A release existente tem assets inesperados; nenhum arquivo sera removido ou substituido.'
    }
    $uploaded = @()
    foreach ($file in $artifacts.Assets) {
        $name = [IO.Path]::GetFileName($file)
        $localHash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        $existing = @($release.assets | Where-Object name -eq $name)
        if ($existing.Count -gt 0) {
            if ($existing.Count -ne 1 -or $existing[0].digest -ne ('sha256:' + $localHash)) { throw "Ja existe outro conteudo para o asset $name. Nao sera sobrescrito." }
            $uploaded += $name; continue
        }
        $url = 'https://uploads.github.com/repos/' + $repoName + '/releases/' + $release.id + '/assets?name=' + [Uri]::EscapeDataString($name)
        $result = Invoke-RestMethod -Method Post -Uri $url -Headers $headers -ContentType 'application/octet-stream' -InFile $file -TimeoutSec 600
        if ($result.digest -ne ('sha256:' + $localHash)) { throw "O hash remoto do asset $name nao corresponde ao arquivo local." }
        $uploaded += $name
    }
    if ($release.draft) {
        # Make the release visible only after all three artifacts have matching server hashes.
        $body = @{ draft=$false; make_latest='true' } | ConvertTo-Json
        $release = Invoke-RestMethod -Method Patch -Uri ($api + '/releases/' + $release.id) -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 30
    }
    [PSCustomObject]@{ release=$release.html_url; tag=$tag; commit=$localCommit; assets=$uploaded; sha256=$hash } | ConvertTo-Json
} finally {
    if ($headers.ContainsKey('Authorization')) { $headers.Remove('Authorization') }
    $credentialOutput = $null; $credential = $null
}
