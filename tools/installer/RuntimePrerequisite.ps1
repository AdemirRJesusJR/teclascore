# Windows PowerShell 5.1 compatible: this check runs before .NET 10 is available.
# Read-only until the user explicitly chooses to open Microsoft's download page.
function Get-TeclaScoreArchitecture([string]$Executable) {
    $stream = [IO.File]::OpenRead($Executable)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw 'Executavel Windows invalido.' }
        $stream.Position = 0x3c
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt $stream.Length - 6) { throw 'Cabecalho Windows invalido.' }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550) { throw 'Assinatura Windows invalida.' }
        switch ($reader.ReadUInt16()) {
            0x8664 { return 'x64' }
            0xaa64 { return 'arm64' }
            0x014c { return 'x86' }
            default { throw 'Arquitetura do aplicativo nao reconhecida.' }
        }
    } finally { $reader.Dispose(); $stream.Dispose() }
}

function Test-TeclaScoreRuntimeText([string]$Text) {
    # SDK, ASP.NET, another major version, and preview runtimes do not satisfy WPF.
    return ($Text -match '(?m)^Microsoft\.WindowsDesktop\.App 10\.0\.\d+ \[') -and
        ($Text -match '(?m)^Microsoft\.NETCore\.App 10\.0\.\d+ \[')
}

function Resolve-TeclaScoreRuntimeRoot(
    [string]$Architecture, [string]$OperatingSystemArchitecture,
    [System.Collections.IDictionary]$EnvironmentValues, [AllowNull()][object]$RegisteredRoot,
    [string]$ApplicationDirectory,
    [scriptblock]$PathExists = { param($Path) Test-Path -LiteralPath $Path }) {
    # Default framework-dependent apphost search; PATH does not participate.
    # https://learn.microsoft.com/dotnet/core/tools/dotnet-environment-variables#dotnet_root-dotnet_rootx86-dotnet_root_x86-dotnet_root_x64
    # https://github.com/dotnet/runtime/blob/v10.0.0/src/native/corehost/fxr_resolver.cpp
    if ($ApplicationDirectory -and (& $PathExists (Join-Path $ApplicationDirectory 'hostfxr.dll'))) {
        return [PSCustomObject]@{ Root=$ApplicationDirectory; Source='AppLocal' }
    }
    $names = @('DOTNET_ROOT_' + $Architecture.ToUpperInvariant())
    if ($Architecture -eq 'x86' -and $OperatingSystemArchitecture -ne 'x86') { $names += 'DOTNET_ROOT(x86)' }
    $names += 'DOTNET_ROOT'
    foreach ($name in $names) {
        $value = [string]$EnvironmentValues[$name]
        # hostmisc/utils.cpp ignores nonexistent env paths, but selects an existing
        # path even if it contains no usable runtime. Never try another root then.
        if ($value -and (& $PathExists $value)) {
            return [PSCustomObject]@{ Root=[IO.Path]::GetFullPath($value); Source=$name }
        }
    }
    if ($null -ne $RegisteredRoot) {
        return [PSCustomObject]@{ Root=$RegisteredRoot; Source='Registry32' }
    }
    $programFiles = [string]$EnvironmentValues['ProgramFiles']
    if ($OperatingSystemArchitecture -ne 'x86') {
        if ($Architecture -eq 'x86') { $programFiles = [string]$EnvironmentValues['ProgramFiles(x86)'] }
        elseif ($EnvironmentValues['PROCESSOR_ARCHITECTURE'] -eq 'x86' -and $EnvironmentValues['ProgramW6432']) {
            $programFiles = [string]$EnvironmentValues['ProgramW6432']
        }
    }
    if ($programFiles -and (& $PathExists $programFiles)) {
        $defaultRoot = Join-Path $programFiles 'dotnet'
        if ($Architecture -eq 'x64' -and $OperatingSystemArchitecture -eq 'arm64') {
            $defaultRoot = Join-Path $defaultRoot 'x64'
        }
        return [PSCustomObject]@{ Root=$defaultRoot; Source='Default' }
    }
    return [PSCustomObject]@{ Root=$null; Source='Unavailable' }
}

function Test-TeclaScoreRuntimeExecutable([string]$Candidate, [string]$Architecture) {
    if (-not (Test-Path -LiteralPath $Candidate -PathType Leaf)) { return $false }
    $process = $null
    try {
        if ((Get-TeclaScoreArchitecture $Candidate) -ne $Architecture) { return $false }
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $Candidate; $start.Arguments = '--list-runtimes'
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($start)
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(5000)) { $process.Kill(); return $false }
        $text = $output.GetAwaiter().GetResult()
        $null = $errors.GetAwaiter().GetResult()
        return ($process.ExitCode -eq 0 -and (Test-TeclaScoreRuntimeText $text))
    } catch { return $false } finally { if ($null -ne $process) { $process.Dispose() } }
}

function Test-TeclaScoreSelectedRuntime($Selection, [string]$Architecture,
    [scriptblock]$Probe = { param($Candidate, $Arch) Test-TeclaScoreRuntimeExecutable $Candidate $Arch }) {
    $ready = $false; $candidate = $null
    try {
        # This installer ships a framework-dependent app, not an app-local runtime.
        # An unexpected local hostfxr overrides global discovery; don't certify it
        # by probing an unrelated global dotnet installation.
        if ($Selection.Root -and $Selection.Source -ne 'AppLocal') {
            $candidate = Join-Path $Selection.Root 'dotnet.exe'
            $ready = [bool](& $Probe $candidate $Architecture)
        }
    } catch { $ready = $false }
    return [PSCustomObject]@{ Ready=$ready; Architecture=$Architecture;
        DotnetPath=$(if ($ready) { $candidate } else { $null }); RuntimeRoot=$Selection.Root; RootSource=$Selection.Source }
}

function Get-TeclaScoreRuntimeStatus([string]$Executable) {
    $architecture = Get-TeclaScoreArchitecture $Executable
    $environmentValues = [Environment]::GetEnvironmentVariables()
    $osArchitecture = [string]$environmentValues['PROCESSOR_ARCHITEW6432']
    if (-not $osArchitecture) { $osArchitecture = [string]$environmentValues['PROCESSOR_ARCHITECTURE'] }
    switch ($osArchitecture.ToUpperInvariant()) {
        'ARM64' { $osArchitecture = 'arm64' }
        'AMD64' { $osArchitecture = 'x64' }
        default { $osArchitecture = 'x86' }
    }
    $registeredRoot = $null; $registry = $null; $key = $null
    try {
        # apphost always reads the 32-bit registry view, regardless of its own arch.
        $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry32)
        $key = $registry.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\' + $architecture)
        if ($null -ne $key -and $key.GetValueKind('InstallLocation') -eq [Microsoft.Win32.RegistryValueKind]::String) {
            $registeredRoot = [string]$key.GetValue('InstallLocation')
        }
    } catch { } finally {
        if ($null -ne $key) { $key.Dispose() }
        if ($null -ne $registry) { $registry.Dispose() }
    }
    $selection = Resolve-TeclaScoreRuntimeRoot $architecture $osArchitecture $environmentValues $registeredRoot ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Executable)))
    return Test-TeclaScoreSelectedRuntime $selection $architecture
}

function Show-TeclaScoreRuntimeGuidance($RuntimeStatus, [switch]$Interactive) {
    if ($RuntimeStatus.Ready) {
        Write-Output ('.NET Desktop Runtime 10 (' + $RuntimeStatus.Architecture + ') encontrado.')
        Write-Output 'Abra Configuracoes > Preparar este computador para conferir Python, Pillow e MuseScore.'
        return
    }
    $download = 'https://dotnet.microsoft.com/pt-br/download/dotnet/10.0'
    Write-Output ('Falta conferir o .NET Desktop Runtime 10 para Windows ' + $RuntimeStatus.Architecture + '.')
    Write-Output ('Baixe Windows Desktop Runtime (nao apenas .NET Runtime): ' + $download)
    Write-Output 'Depois da instalacao, abra o TeclaScore e escolha Configuracoes > Preparar este computador.'
    if ($Interactive) {
        Add-Type -AssemblyName System.Windows.Forms
        $message = "O TeclaScore foi instalado. Para abrir o aplicativo, instale o .NET Desktop Runtime 10 para Windows " + $RuntimeStatus.Architecture + ".`r`n`r`nAbrir a pagina oficial da Microsoft? Selecione Windows Desktop Runtime e a arquitetura indicada. Depois abra o TeclaScore pelo atalho."
        $answer = [Windows.Forms.MessageBox]::Show($message, 'Preparar o computador - TeclaScore',
            [Windows.Forms.MessageBoxButtons]::YesNo, [Windows.Forms.MessageBoxIcon]::Information)
        if ($answer -eq [Windows.Forms.DialogResult]::Yes) {
            $start = New-Object Diagnostics.ProcessStartInfo
            $start.FileName = $download; $start.UseShellExecute = $true
            [void][Diagnostics.Process]::Start($start)
        }
    }
}
