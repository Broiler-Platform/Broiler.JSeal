#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Version,
    [string] $OutputDirectory,
    [switch] $KeepWorkspace,
    [ValidateRange(1, 1800)][int] $BuildTimeoutSeconds = 300,
    [ValidateRange(1, 300)][int] $RunTimeoutSeconds = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$runId = [guid]::NewGuid().ToString('N')
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot "test-results/package-consumer/$runId" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory to preserve earlier results.' }
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$workspace = [IO.Path]::GetFullPath((Join-Path $tempRoot "jseal-package-consumer-$runId"))
if ($workspace.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The system temporary directory must be outside this checkout.'
}
New-Item -ItemType Directory -Path $OutputDirectory, $workspace | Out-Null
$commands = [Collections.Generic.List[object]]::new()
$packages = [Collections.Generic.List[object]]::new()
$consumers = [Collections.Generic.List[object]]::new()
$report = [ordered]@{
    schemaVersion = 1
    capturedUtc = [DateTime]::UtcNow.ToString('O')
    os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    powerShell = $PSVersionTable.PSVersion.ToString()
    workspace = $workspace
    workspaceRetained = [bool]$KeepWorkspace
    completed = $false
    inputs = @(@('test-package-consumer.ps1', 'pack.ps1', 'package-consumer/Consumer.csproj', 'package-consumer/Program.cs') | ForEach-Object {
        $inputPath = Join-Path $PSScriptRoot $_
        [ordered]@{ path = "eng/$_"; sha256 = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    commands = $commands
    packages = $packages
    consumers = $consumers
}

function Invoke-Step([string] $Name, [string] $Executable, [string[]] $Arguments,
    [string] $Directory, [int] $Timeout, [hashtable] $Environment = @{}, [switch] $ExpectFailure) {
    Write-Host "J02: $Name"
    $record = [ordered]@{ name = $Name; executable = $Executable; arguments = $Arguments;
        workingDirectory = $Directory; exitCode = $null; timedOut = $false; timeoutSeconds = $Timeout;
        expectFailure = [bool]$ExpectFailure }
    $commands.Add($record)
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Executable
    $info.WorkingDirectory = $Directory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $info.Environment[$key] = $Environment[$key] }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        $null = $process.Start()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($Timeout * 1000)) {
            $record.timedOut = $true
            $process.Kill($true)
            $process.WaitForExit()
        }
        $record.stdout = $stdoutTask.GetAwaiter().GetResult()
        $record.stderr = $stderrTask.GetAwaiter().GetResult()
        $record.exitCode = $process.ExitCode
        if ($record.timedOut -or (!$ExpectFailure -and $process.ExitCode -ne 0) -or ($ExpectFailure -and $process.ExitCode -eq 0)) {
            throw "$Name failed (exit $($record.exitCode), timedOut=$($record.timedOut)); see report.json."
        }
        return $record.stdout
    }
    finally { $process.Dispose() }
}

function Package-Hash([string] $Path) {
    [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData([IO.File]::ReadAllBytes($Path)))
}

try {
    $report.revision = (Invoke-Step 'revision' git @('rev-parse', 'HEAD') $repoRoot 10).Trim()
    $report.workingTree = Invoke-Step 'working-tree' git @('status', '--porcelain=v1') $repoRoot 10
    $report.dotnetSdk = (Invoke-Step 'sdk' dotnet @('--version') $workspace 10).Trim()
    $feed = Join-Path $workspace 'feed'
    $packArgs = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'pack.ps1'), '-Output', $feed)
    if ($Version) { $packArgs += @('-Version', $Version) }
    $null = Invoke-Step 'pack' (Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })) $packArgs $repoRoot $BuildTimeoutSeconds

    # Reuse immutable NuGet archives resolved by the provider builds, never bin/ DLLs or test assets.
    # The *consumer* restore below uses only this feed and a new empty cache for each provider.
    $seen = @{}
    foreach ($provider in @('BroilerJs', 'Vm')) {
        $assetsPath = Join-Path $repoRoot "Broiler.JSeal.$provider/obj/project.assets.json"
        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
        foreach ($entry in $assets.libraries.GetEnumerator() | Sort-Object Key) {
            if ($entry.Value.type -ne 'package' -or $seen.ContainsKey($entry.Key)) { continue }
            $archiveName = $entry.Key.Replace('/', '.').ToLowerInvariant() + '.nupkg'
            $source = @($assets.packageFolders.Keys | ForEach-Object {
                Join-Path (Join-Path $_ $entry.Value.path) $archiveName
            } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)
            if ($source.Count -ne 1) { throw "Missing NuGet archive for $($entry.Key); restore the provider packages first." }
            $hash = Package-Hash $source[0]
            if ($hash -cne $entry.Value.sha512) { throw "NuGet archive hash mismatch for $($entry.Key)." }
            Copy-Item -LiteralPath $source[0] -Destination (Join-Path $feed $archiveName)
            $seen[$entry.Key] = $true
        }
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $identities = @{}
    foreach ($archive in Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' | Sort-Object Name) {
        $zip = [IO.Compression.ZipFile]::OpenRead($archive.FullName)
        try {
            $nuspec = @($zip.Entries | Where-Object FullName -like '*.nuspec')
            if ($nuspec.Count -ne 1) { throw "Invalid manifest in $($archive.Name)." }
            $reader = [IO.StreamReader]::new($nuspec[0].Open())
            try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $id = $manifest.package.metadata.id
            $packageVersion = $manifest.package.metadata.version
            $identity = "$id/$packageVersion"
            if ($identities.ContainsKey($identity)) { throw "Duplicate package identity: $identity" }
            $identities[$identity] = $archive.FullName
            $packages.Add([ordered]@{ identity = $identity; archive = $archive.Name;
                sha512 = Package-Hash $archive.FullName; origin = $(if ($id -in @('Broiler.JSeal', 'Broiler.JSeal.BroilerJs', 'Broiler.JSeal.Vm')) { 'local pack' } else { 'resolved NuGet archive' }) })
        }
        finally { $zip.Dispose() }
    }
    $shipping = @($packages | Where-Object origin -eq 'local pack')
    $versions = @($shipping.identity | ForEach-Object { $_.Split('/')[1] } | Select-Object -Unique)
    if ($shipping.Count -ne 3 -or $versions.Count -ne 1) { throw 'Expected the three shipping packages at one version.' }
    $report.packageVersion = $versions[0]

    foreach ($provider in @('BroilerJs', 'Vm')) {
        $directory = Join-Path $workspace $provider
        New-Item -ItemType Directory -Path $directory | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'package-consumer/Consumer.csproj'), (Join-Path $PSScriptRoot 'package-consumer/Program.cs') -Destination $directory
        foreach ($name in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
            [IO.File]::WriteAllText((Join-Path $directory $name), '<Project />')
        }
        $escapedFeed = [Security.SecurityElement]::Escape($feed)
        $config = Join-Path $directory 'NuGet.config'
        [IO.File]::WriteAllText($config, @"
<configuration>
  <packageSources><clear /><add key="isolated" value="$escapedFeed" /></packageSources>
  <disabledPackageSources><clear /></disabledPackageSources>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
  <packageSourceMapping><clear /><packageSource key="isolated"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
"@)
        $cache = Join-Path $directory 'packages'
        $environment = @{ NUGET_PACKAGES = $cache; NUGET_HTTP_CACHE_PATH = (Join-Path $directory 'http-cache');
            NUGET_FALLBACK_PACKAGES = ''; DOTNET_ADDITIONAL_DEPS = ''; DOTNET_SHARED_STORE = '';
            DOTNET_STARTUP_HOOKS = ''; DOTNET_CLI_UI_LANGUAGE = 'en-US' }
        $properties = @("-p:Provider=$provider", "-p:ConsumerPackageVersion=$($report.packageVersion)", '-p:NuGetAudit=false', '-p:RestoreFallbackFolders=')
        $null = Invoke-Step "$provider-restore" dotnet (@('restore', 'Consumer.csproj', '--configfile', $config, '--packages', $cache, '--no-http-cache') + $properties) $directory $BuildTimeoutSeconds $environment
        $restored = Get-Content -LiteralPath (Join-Path $directory 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
        $direct = $restored.project.frameworks['net10.0'].dependencies
        if ($direct.Count -ne 1 -or !$direct.ContainsKey("Broiler.JSeal.$provider")) {
            throw "$provider consumer must reference only its provider package directly."
        }
        if ($restored.packageFolders.Count -ne 1 -or [IO.Path]::TrimEndingDirectorySeparator(@($restored.packageFolders.Keys)[0]) -ne $cache) {
            throw "$provider restore used a package folder outside its fresh cache."
        }
        if ($restored.project.restore.sources.Count -ne 1 -or @($restored.project.restore.sources.Keys)[0] -ne $feed) {
            throw "$provider restore used a source outside the isolated feed."
        }
        foreach ($entry in $restored.libraries.GetEnumerator()) {
            if ($entry.Value.type -ne 'package' -or !$identities.ContainsKey($entry.Key)) {
                throw "$provider resolved a project or an unstaged package: $($entry.Key)"
            }
            if ($entry.Value.sha512 -cne (Package-Hash $identities[$entry.Key])) { throw "Restored hash differs for $($entry.Key)." }
        }
        $null = Invoke-Step "$provider-build" dotnet (@('build', 'Consumer.csproj', '-c', 'Release', '--no-restore', '--nologo') + $properties) $directory $BuildTimeoutSeconds $environment
        $output = Join-Path $directory 'bin/Release/net10.0'
        $stdout = Invoke-Step "$provider-run" dotnet @((Join-Path $output 'Consumer.dll')) $output $RunTimeoutSeconds $environment
        $records = @($stdout -split '\r?\n' | Where-Object { $_.StartsWith("J02`t") })
        if ($records.Count -ne 1) { throw "$provider did not emit exactly one completed consumer record." }
        $observation = $records[0].Substring(4) | ConvertFrom-Json -AsHashtable
        $consumers.Add([ordered]@{ provider = $provider; packages = @($restored.libraries.Keys | Sort-Object);
            observation = $observation; assemblies = @(Get-ChildItem -LiteralPath $output -Filter '*.dll' | Sort-Object Name | ForEach-Object {
                [ordered]@{ name = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
            }) })
        Copy-Item -LiteralPath (Join-Path $directory 'obj/project.assets.json') -Destination (Join-Path $OutputDirectory "$provider.assets.json")

        # Negative controls: a warm successful restore must not conceal an empty feed, and
        # a fresh process must not find a removed contracts DLL in the checkout or package cache.
        $emptyFeed = Join-Path $directory 'empty-feed'
        New-Item -ItemType Directory -Path $emptyFeed | Out-Null
        $emptyConfig = Join-Path $directory 'empty.config'
        [IO.File]::WriteAllText($emptyConfig, [IO.File]::ReadAllText($config).Replace($escapedFeed, [Security.SecurityElement]::Escape($emptyFeed)))
        $negative = Invoke-Step "$provider-empty-feed" dotnet (@('restore', 'Consumer.csproj', '--configfile', $emptyConfig,
            '--packages', (Join-Path $directory 'negative-packages'), '--no-http-cache', '--force',
            "-p:BaseIntermediateOutputPath=$(Join-Path $directory 'negative-obj')/") + $properties) $directory $BuildTimeoutSeconds $environment -ExpectFailure
        if ($negative -notmatch 'NU1101|NU1102') { throw "$provider empty-feed control did not fail for a missing package." }
        $incomplete = Join-Path $directory 'incomplete-runtime'
        New-Item -ItemType Directory -Path $incomplete | Out-Null
        Get-ChildItem -LiteralPath $output -File | Where-Object Name -ne 'Broiler.JSeal.dll' |
            Copy-Item -Destination $incomplete
        $null = Invoke-Step "$provider-missing-contracts" dotnet @((Join-Path $incomplete 'Consumer.dll')) $incomplete $RunTimeoutSeconds $environment -ExpectFailure
        if ($commands[$commands.Count - 1].stderr -notmatch 'Broiler.JSeal, Version=') {
            throw "$provider missing-assembly control failed for an unexpected reason."
        }
    }
    $report.completed = $true
    Write-Host 'J02: both isolated package consumers passed.'
}
catch {
    $report.failure = $_.Exception.Message
    throw
}
finally {
    $report | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'report.json') -Encoding utf8NoBOM
    Write-Host "J02 report: $OutputDirectory"
    if (!$KeepWorkspace) {
        # Only remove the unique directory created above, after checking its resolved parent/name.
        $target = Get-Item -LiteralPath $workspace
        if ($target.Parent.FullName -ne [IO.Path]::TrimEndingDirectorySeparator($tempRoot) -or $target.Name -ne "jseal-package-consumer-$runId") {
            throw "Refusing to clean unexpected workspace: $workspace"
        }
        Remove-Item -LiteralPath $target.FullName -Recurse -Force
    }
}
