#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('All', 'Providers', 'Sources')][string] $Mode = 'All',
    [string] $BroilerJsRoot,
    [string] $BroilerVmRoot,
    [string] $OutputDirectory,
    [ValidateRange(1, 300)][int] $ProbeTimeoutSeconds = 30,
    [ValidateRange(1, 1800)][int] $BuildTimeoutSeconds = 300
)

# A fixed review-bundle collector, not a replacement for VM's differential runner (J01).
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$siblingRoot = Split-Path $repoRoot -Parent
if (!$BroilerJsRoot) { $BroilerJsRoot = Join-Path $siblingRoot 'Broiler.JS' }
if (!$BroilerVmRoot) { $BroilerVmRoot = Join-Path $siblingRoot 'Broiler.VM' }
$BroilerJsRoot = [IO.Path]::GetFullPath($BroilerJsRoot)
$BroilerVmRoot = [IO.Path]::GetFullPath($BroilerVmRoot)
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot ("test-results/J00/" + [guid]::NewGuid().ToString('N'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory to preserve earlier evidence.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$commands = [Collections.Generic.List[object]]::new()
$runs = [Collections.Generic.List[object]]::new()
$report = [ordered]@{
    schemaVersion = 1
    purpose = 'J00 targeted review observations; not a conformance score'
    capturedUtc = [DateTime]::UtcNow.ToString('O')
    mode = $Mode
    os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    powerShell = $PSVersionTable.PSVersion.ToString()
    completed = $false
    repositories = [ordered]@{}
    inputHashes = @()
    commands = $commands
    runs = $runs
}

function Portable-Path([string] $Value) {
    $Value.Replace($repoRoot, '<JSeal>').Replace($BroilerJsRoot, '<Broiler.JS>').Replace(
        $BroilerVmRoot, '<Broiler.VM>').Replace('\', '/')
}

function Invoke-Recorded([string] $Name, [string] $Executable, [string[]] $Arguments,
    [string] $WorkingDirectory, [int] $TimeoutSeconds) {
    $command = [ordered]@{
        name = $Name
        executable = $Executable
        arguments = @($Arguments | ForEach-Object { Portable-Path $_ })
        workingDirectory = Portable-Path $WorkingDirectory
        timeoutSeconds = $TimeoutSeconds
        stdoutLog = "$Name.stdout.txt"
        stderrLog = "$Name.stderr.txt"
        exitCode = $null
        timedOut = $false
    }
    $commands.Add($command)
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Executable
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (!$process.Start()) { throw "Could not start $Name" }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $elapsed = [Diagnostics.Stopwatch]::StartNew()
        while (!$process.WaitForExit(500)) {
            if ($elapsed.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                $command.timedOut = $true
                $process.Kill($true)
                $process.WaitForExit()
                break
            }
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $OutputDirectory $command.stdoutLog), $stdout)
        [IO.File]::WriteAllText((Join-Path $OutputDirectory $command.stderrLog), $stderr)
        # Keep the retained JSON self-contained; separate files preserve the original raw logs.
        $command.stdout = $stdout.Replace($repoRoot, '<JSeal>').Replace($BroilerJsRoot, '<Broiler.JS>').Replace($BroilerVmRoot, '<Broiler.VM>')
        $command.stderr = $stderr.Replace($repoRoot, '<JSeal>').Replace($BroilerJsRoot, '<Broiler.JS>').Replace($BroilerVmRoot, '<Broiler.VM>')
        $command.exitCode = $process.ExitCode
        if ($command.timedOut) { throw "$Name timed out; see $OutputDirectory" }
        if ($process.ExitCode -ne 0) { throw "$Name exited $($process.ExitCode); see $OutputDirectory" }
        return $stdout
    }
    finally { $process.Dispose() }
}

function Repository-Identity([string] $Name, [string] $Root) {
    if (!(Test-Path -LiteralPath $Root -PathType Container)) { throw "Missing $Name checkout: $Root" }
    $revision = Invoke-Recorded "$Name-revision" git @('rev-parse', 'HEAD') $Root 10
    $status = Invoke-Recorded "$Name-status" git @('status', '--porcelain=v1', '--untracked-files=normal') $Root 10
    return [ordered]@{
        revision = $revision.Trim()
        workingTree = @($status -split '\r?\n' | Where-Object { $_ })
    }
}

function Binary-Identities([string] $MainAssembly) {
    $directory = Split-Path $MainAssembly -Parent
    $files = @(Get-Item -LiteralPath $MainAssembly) + @(Get-ChildItem -LiteralPath $directory -Filter 'Broiler.*.dll')
    @($files | Sort-Object FullName -Unique | ForEach-Object {
        [ordered]@{
            name = $_.Name
            productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($_.FullName).ProductVersion
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
}

function Capture-Probes([string] $Name, [string] $Assembly, [string[]] $Arguments,
    [string] $Root, [int] $ExpectedCount, [string] $TargetKind) {
    $output = Invoke-Recorded $Name dotnet (@($Assembly) + $Arguments) $Root $ProbeTimeoutSeconds
    $records = @($output -split '\r?\n' | Where-Object { $_.StartsWith("J00`t") } |
        ForEach-Object { $_.Substring(4) | ConvertFrom-Json -AsHashtable })
    $end = @($output -split '\r?\n' | Where-Object { $_.StartsWith("J00-DONE`t") })
    if ($end.Count -ne 1 -or $end[0] -ne "J00-DONE`t$ExpectedCount" -or $records.Count -ne $ExpectedCount) {
        throw "$Name produced an incomplete fixture run; expected $ExpectedCount observations."
    }
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($record in $records) {
        if (!$ids.Add($record.id)) { throw "$Name repeated case $($record.id)" }
        if ($record.expected -isnot [string] -or $record.actual -isnot [string]) {
            throw "$Name emitted invalid observation values."
        }
        $record.outcome = if ($record.actual -ceq $record.expected) { 'matches-target' } else { 'differs-from-target' }
        $record.kind = if ($record.id -match '^(global|method|symbol|inventory)\.') { 'availability' } else { 'behavior' }
    }
    $runs.Add([ordered]@{
        name = $Name
        targetKind = $TargetKind
        assemblies = @(Binary-Identities $Assembly)
        observations = $records
    })
    $differences = @($records | Where-Object { $_.outcome -eq 'differs-from-target' }).Count
    Write-Host "${Name}: captured $($records.Count) observations; $differences differ from target behavior/availability."
}

try {
    $report.repositories.jseal = Repository-Identity 'jseal' $repoRoot
    $report.dotnetSdk = (Invoke-Recorded 'dotnet-sdk' dotnet @('--version') $repoRoot 10).Trim()
    $inputs = @(
        (Join-Path $PSScriptRoot 'run.ps1'),
        (Join-Path $PSScriptRoot 'language.js'),
        (Join-Path $PSScriptRoot 'ProviderProbes/Program.cs'),
        (Join-Path $PSScriptRoot 'ProviderProbes/ProviderProbes.csproj'),
        (Join-Path $repoRoot 'Directory.Packages.props')
    )
    $report.inputHashes = @($inputs | ForEach-Object {
        [ordered]@{ path = Portable-Path $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    if ($Mode -ne 'Sources') {
        $project = Join-Path $PSScriptRoot 'ProviderProbes/ProviderProbes.csproj'
        $null = Invoke-Recorded 'build-providers' dotnet @('build', $project, '-c', 'Release-VM', '--nologo') $repoRoot $BuildTimeoutSeconds
        $assets = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ProviderProbes/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
        $report.resolvedEnginePackages = @($assets.libraries.Keys | Sort-Object | Where-Object {
            $_ -like 'Broiler.*/*' -and $assets.libraries[$_].type -eq 'package'
        } | ForEach-Object { [ordered]@{ identity = $_; sha512 = $assets.libraries[$_].sha512 } })
        $assembly = Join-Path $PSScriptRoot 'ProviderProbes/bin/Release-VM/net10.0/ProviderProbes.dll'
        Capture-Probes 'provider-js' $assembly @('broiler-js') $repoRoot 10 'JSeal source adapter over pinned NuGet engines'
        Capture-Probes 'provider-vm' $assembly @('broiler-vm') $repoRoot 10 'JSeal source adapter over pinned NuGet engines'
        Capture-Probes 'registry' $assembly @('registry') $repoRoot 1 'JSeal contracts in an isolated process'
    }
    if ($Mode -ne 'Providers') {
        $report.repositories.broilerJs = Repository-Identity 'js' $BroilerJsRoot
        $report.repositories.broilerVm = Repository-Identity 'vm' $BroilerVmRoot
        $jsProject = Join-Path $BroilerJsRoot 'Broiler.JS/Broiler.JavaScript/Broiler.JavaScript.csproj'
        $vmProject = Join-Path $BroilerVmRoot 'src/compositions/Broiler.VM.Composition.JavaScript.Cli/Broiler.VM.Composition.JavaScript.Cli.csproj'
        $null = Invoke-Recorded 'build-source-js' dotnet @('build', $jsProject, '-c', 'Release', '--nologo') $BroilerJsRoot $BuildTimeoutSeconds
        $null = Invoke-Recorded 'build-source-vm' dotnet @('build', $vmProject, '-c', 'Release', '--nologo') $BroilerVmRoot $BuildTimeoutSeconds
        $fixture = Join-Path $PSScriptRoot 'language.js'
        $jsAssembly = Join-Path (Split-Path $jsProject -Parent) 'bin/Release/net10.0/BroilerJS.dll'
        $vmAssembly = Join-Path (Split-Path $vmProject -Parent) 'bin/Release/net10.0/Broiler.VM.Composition.JavaScript.Cli.dll'
        Capture-Probes 'source-js' $jsAssembly @('--script-host', $fixture) $BroilerJsRoot 39 'Current-source CLI; explicit script goal'
        Capture-Probes 'source-vm' $vmAssembly @('--all', '--quiet', $fixture) $BroilerVmRoot 39 'Current-source CLI; wide script goal with optional surfaces admitted'
    }
    $report.completed = $true
}
catch {
    $report.failure = $_.Exception.Message
    throw
}
finally {
    $report | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'observations.json') -Encoding utf8NoBOM
    Write-Host "J00 evidence: $OutputDirectory"
}
