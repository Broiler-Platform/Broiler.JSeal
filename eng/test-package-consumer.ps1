#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Version,
    [string] $OutputDirectory,
    [switch] $KeepWorkspace,
    [ValidateRange(1, 1800)][int] $BuildTimeoutSeconds = 300,
    [ValidateRange(1, 300)][int] $RunTimeoutSeconds = 30,
    # J19 candidate lane: a local directory of upstream .nupkg archives and one exact version per
    # engine family (Broiler.VM=X or Broiler.JavaScript=Y). Default pins are never edited.
    [string] $CandidateFeed,
    [string[]] $CandidateVersion,
    [ValidateRange(1, 3600)][int] $TestTimeoutSeconds = 1800
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if ([bool]$CandidateFeed -ne [bool]$CandidateVersion) {
    throw 'A candidate needs both -CandidateFeed and -CandidateVersion Family=version.'
}
if ($CandidateFeed) {
    # `pwsh -File` passes a comma list as one string; accept both forms.
    $CandidateVersion = @($CandidateVersion | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $CandidateFeed = [IO.Path]::GetFullPath($CandidateFeed)
    if (!(Test-Path -LiteralPath $CandidateFeed -PathType Container)) { throw "Candidate feed not found: $CandidateFeed" }
}
$gateScript = Join-Path $PSScriptRoot 'package-gate.mjs'
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
$graphs = [Collections.Generic.List[object]]::new()
$report = [ordered]@{
    schemaVersion = 2
    lane = $(if ($CandidateFeed) { 'candidate' } else { 'pinned' })
    capturedUtc = [DateTime]::UtcNow.ToString('O')
    os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    powerShell = $PSVersionTable.PSVersion.ToString()
    workspace = $workspace
    workspaceRetained = [bool]$KeepWorkspace
    completed = $false
    inputs = @(@('test-package-consumer.ps1', 'pack.ps1', 'package-gate.mjs', 'package-consumer/Consumer.csproj', 'package-consumer/Program.cs') | ForEach-Object {
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

# Identity and every declared dependency range of one archive, from its nuspec.
function Read-PackageManifest([string] $Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $nuspec = @($zip.Entries | Where-Object FullName -like '*.nuspec')
        if ($nuspec.Count -ne 1) { throw "Invalid manifest in $([IO.Path]::GetFileName($Path))." }
        $reader = [IO.StreamReader]::new($nuspec[0].Open())
        try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        # GetAttribute, not $_.version: under strict mode a dependency without a version attribute
        # would throw here instead of reaching the gate, which refuses it by name.
        [ordered]@{ id = $manifest.package.metadata.id; version = $manifest.package.metadata.version
            sha512 = Package-Hash $Path; dependencies = @($manifest.SelectNodes('//*[local-name()="dependency"]') |
                ForEach-Object { [ordered]@{ id = $_.GetAttribute('id')
                    range = $(if ($_.HasAttribute('version')) { $_.GetAttribute('version') } else { $null }) } }) }
    }
    finally { $zip.Dispose() }
}

# Copies the archive of every package a restore resolved, from its package folder, after checking it
# against the SHA-512 the restore recorded. Returns the identities copied; skips those in $Seen and
# those $Skip answers true for.
function Copy-ResolvedArchives([hashtable] $Assets, [string] $Destination, [hashtable] $Seen, [scriptblock] $Skip = { $false }) {
    foreach ($entry in $Assets.libraries.GetEnumerator() | Sort-Object Key) {
        if ($entry.Value.type -ne 'package' -or $Seen.ContainsKey($entry.Key) -or (& $Skip ($entry.Key.Split('/')[0]))) { continue }
        $archiveName = $entry.Key.Replace('/', '.').ToLowerInvariant() + '.nupkg'
        $source = @($Assets.packageFolders.Keys | ForEach-Object {
            Join-Path (Join-Path $_ $entry.Value.path) $archiveName
        } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)
        if ($source.Count -ne 1) { throw "Missing NuGet archive for $($entry.Key); restore the provider packages first." }
        $hash = Package-Hash $source[0]
        if ($hash -cne $entry.Value.sha512) { throw "NuGet archive hash mismatch for $($entry.Key)." }
        Copy-Item -LiteralPath $source[0] -Destination (Join-Path $Destination $archiveName)
        $Seen[$entry.Key] = $true
        $entry.Key
    }
}

# J19: the pure version-compatibility rules live in package-gate.mjs (unit-tested separately).
function Invoke-Gate([string] $Name) {
    $request = [ordered]@{ packagesPropsPath = (Join-Path $sourceRoot 'Directory.Packages.props')
        candidates = $candidates; candidateFeed = @($candidateArchives); feed = @($packages); graphs = @($graphs) }
    $requestPath = Join-Path $OutputDirectory "$Name.request.json"
    $request | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $requestPath -Encoding utf8NoBOM
    $report[$Name] = "$Name.json"
    $null = Invoke-Step $Name node @($gateScript, 'check', $requestPath, (Join-Path $OutputDirectory "$Name.json")) $repoRoot 60
}

try {
    $report.revision = (Invoke-Step 'revision' git @('rev-parse', 'HEAD') $repoRoot 10).Trim()
    $report.workingTree = Invoke-Step 'working-tree' git @('status', '--porcelain=v1') $repoRoot 10
    $report.dotnetSdk = (Invoke-Step 'sdk' dotnet @('--version') $workspace 10).Trim()
    $report.node = (Invoke-Step 'node' node @('--version') $workspace 10).Trim()
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $feed = Join-Path $workspace 'feed'
    $sourceRoot = $repoRoot
    $buildEnvironment = @{}
    $candidates = @{}
    $candidateArchives = [Collections.Generic.List[object]]::new()
    if ($CandidateFeed) {
        # Build from a copy of this checkout (tracked and untracked, not ignored) whose pins and
        # NuGet sources name the candidate. The checkout, its pins and its obj/ stay untouched.
        $sourceRoot = Join-Path $workspace 'source'
        $files = (Invoke-Step 'candidate-source-files' git @('ls-files', '-z', '--cached', '--others', '--exclude-standard') $repoRoot 30).Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)
        foreach ($file in $files) {
            $from = Join-Path $repoRoot $file
            if (!(Test-Path -LiteralPath $from -PathType Leaf)) { continue }
            $to = Join-Path $sourceRoot $file
            New-Item -ItemType Directory -Force -Path (Split-Path $to -Parent) | Out-Null
            Copy-Item -LiteralPath $from -Destination $to
        }
        $candidateInfo = (Invoke-Step 'candidate-pins' node (@($gateScript, 'candidate-props',
            (Join-Path $repoRoot 'Directory.Packages.props'), (Join-Path $sourceRoot 'Directory.Packages.props')) + $CandidateVersion) $repoRoot 30) | ConvertFrom-Json -AsHashtable
        $candidates = $candidateInfo.candidates
        foreach ($archive in Get-ChildItem -LiteralPath $CandidateFeed -Filter '*.nupkg' | Sort-Object Name) {
            $manifest = Read-PackageManifest $archive.FullName
            if ($manifest.id -like 'Broiler.JSeal*') { throw "The candidate feed must not contain JSeal packages: $($archive.Name)" }
            $manifest.archive = $archive.Name
            $candidateArchives.Add($manifest)
        }
        if (!$candidateArchives.Count) { throw "The candidate feed has no .nupkg archives: $CandidateFeed" }
        # Candidate families, and every id the candidate feed carries, resolve only from that feed.
        $configPath = Join-Path $sourceRoot 'NuGet.config'
        [xml] $nugetConfig = [IO.File]::ReadAllText($configPath)
        $candidateSource = $nugetConfig.CreateElement('add')
        $candidateSource.SetAttribute('key', 'j19-candidate')
        $candidateSource.SetAttribute('value', $CandidateFeed)
        $null = $nugetConfig.configuration.packageSources.AppendChild($candidateSource)
        $candidateMapping = $nugetConfig.CreateElement('packageSource')
        $candidateMapping.SetAttribute('key', 'j19-candidate')
        foreach ($pattern in @($candidateInfo.patterns) + @($candidateArchives | ForEach-Object { $_.id } | Sort-Object -Unique)) {
            $entry = $nugetConfig.CreateElement('package')
            $entry.SetAttribute('pattern', $pattern)
            $null = $candidateMapping.AppendChild($entry)
        }
        $null = $nugetConfig.configuration.packageSourceMapping.AppendChild($candidateMapping)
        # The copy restores into a fresh cache, so every package the checkout already resolved would be
        # fetched again - the Broiler.* engines from an authenticated feed. Stage those archives from the
        # checkout's own restores instead, hash-checked, and map each by exact id to that local source:
        # everything outside the candidate families then restores byte-identical to the pinned build,
        # with no credentials. Only a package the candidate newly introduces uses the normal sources.
        $pinnedFeed = Join-Path $workspace 'pinned-archives'
        New-Item -ItemType Directory -Path $pinnedFeed | Out-Null
        $candidatePatterns = @($candidateInfo.patterns) + @($candidateArchives | ForEach-Object { $_.id })
        $isCandidate = { param($id) @($candidatePatterns | Where-Object { $id -like $_ }).Count -gt 0 }
        $pinnedSeen = @{}
        # The shipping projects' graphs carry the whole Broiler.* engine closure; test-only packages
        # (xunit, the test SDK) come from nuget.org as usual and need no staging.
        $pinnedArchives = @(foreach ($project in @('Broiler.JSeal', 'Broiler.JSeal.BroilerJs', 'Broiler.JSeal.Vm')) {
            $assetsPath = Join-Path $repoRoot "$project/obj/project.assets.json"
            if (!(Test-Path -LiteralPath $assetsPath -PathType Leaf)) { throw "Restore $project before a candidate run." }
            Copy-ResolvedArchives (Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable) $pinnedFeed $pinnedSeen $isCandidate
        })
        if (!$pinnedArchives.Count) { throw 'The checkout has no restored packages to stage; restore it before a candidate run.' }
        $pinnedSource = $nugetConfig.CreateElement('add')
        $pinnedSource.SetAttribute('key', 'j19-pinned')
        $pinnedSource.SetAttribute('value', $pinnedFeed)
        $null = $nugetConfig.configuration.packageSources.AppendChild($pinnedSource)
        $pinnedMapping = $nugetConfig.CreateElement('packageSource')
        $pinnedMapping.SetAttribute('key', 'j19-pinned')
        foreach ($id in @($pinnedArchives | ForEach-Object { $_.Split('/')[0] } | Sort-Object -Unique)) {
            $entry = $nugetConfig.CreateElement('package')
            $entry.SetAttribute('pattern', $id)
            $null = $pinnedMapping.AppendChild($entry)
        }
        $null = $nugetConfig.configuration.packageSourceMapping.AppendChild($pinnedMapping)
        $nugetConfig.Save($configPath)
        # A fresh cache keeps unpublished candidate identities out of the user's global packages folder.
        $buildEnvironment = @{ NUGET_PACKAGES = (Join-Path $workspace 'source-packages')
            NUGET_HTTP_CACHE_PATH = (Join-Path $workspace 'source-http-cache'); DOTNET_CLI_UI_LANGUAGE = 'en-US' }
        $report.candidate = [ordered]@{ feed = $CandidateFeed; versions = $candidates; archives = $candidateArchives
            pinnedArchives = $pinnedArchives }
    }
    $packArgs = @('-NoProfile', '-File', (Join-Path $sourceRoot 'eng/pack.ps1'), '-Output', $feed)
    if ($Version) { $packArgs += @('-Version', $Version) }
    $null = Invoke-Step 'pack' (Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })) $packArgs $sourceRoot $BuildTimeoutSeconds $buildEnvironment

    # Reuse immutable NuGet archives resolved by the provider builds, never bin/ DLLs or test assets.
    # The *consumer* restore below uses only this feed and a new empty cache for each provider.
    $seen = @{}
    foreach ($provider in @('BroilerJs', 'Vm')) {
        $assetsPath = Join-Path $sourceRoot "Broiler.JSeal.$provider/obj/project.assets.json"
        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
        $graphCopy = Join-Path $OutputDirectory "source-$provider.assets.json"
        Copy-Item -LiteralPath $assetsPath -Destination $graphCopy
        $graphs.Add([ordered]@{ name = "source-$provider"; kind = 'source'; assetsPath = $graphCopy })
        $null = Copy-ResolvedArchives $assets $feed $seen
    }

    $identities = @{}
    foreach ($archive in Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' | Sort-Object Name) {
        $manifest = Read-PackageManifest $archive.FullName
        $identity = "$($manifest.id)/$($manifest.version)"
        if ($identities.ContainsKey($identity)) { throw "Duplicate package identity: $identity" }
        $identities[$identity] = $archive.FullName
        $packages.Add([ordered]@{ identity = $identity; id = $manifest.id; version = $manifest.version; archive = $archive.Name
            sha512 = $manifest.sha512; dependencies = $manifest.dependencies
            origin = $(if ($manifest.id -in @('Broiler.JSeal', 'Broiler.JSeal.BroilerJs', 'Broiler.JSeal.Vm')) { 'local pack' } else { 'resolved NuGet archive' }) })
    }
    $shipping = @($packages | Where-Object origin -eq 'local pack')
    $versions = @($shipping.identity | ForEach-Object { $_.Split('/')[1] } | Select-Object -Unique)
    if ($shipping.Count -ne 3 -or $versions.Count -ne 1) { throw 'Expected the three shipping packages at one version.' }
    $report.packageVersion = $versions[0]
    # Fail on mixed families, unpinned versions or an open feed before any consumer is built.
    Invoke-Gate 'gate-source'

    # Each provider alone, then both providers (both engine families) in one consumer process.
    foreach ($provider in @('BroilerJs', 'Vm', 'Both')) {
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
        $expectedDirect = @(if ($provider -eq 'Both') { 'Broiler.JSeal.BroilerJs', 'Broiler.JSeal.Vm' } else { "Broiler.JSeal.$provider" })
        if ($direct.Count -ne $expectedDirect.Count -or @($expectedDirect | Where-Object { !$direct.ContainsKey($_) }).Count) {
            throw "$provider consumer must reference only its provider package(s) directly."
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
        $graphs.Add([ordered]@{ name = "consumer-$provider"; kind = 'consumer'; assetsPath = (Join-Path $OutputDirectory "$provider.assets.json") })

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
    # Every resolved identity of every graph is recorded in gate.json; families must agree across all.
    Invoke-Gate 'gate'
    if ($CandidateFeed) {
        # The explicit pin update must later pass both configurations; run them on the candidate now.
        foreach ($configuration in @('Release', 'Release-VM')) {
            $null = Invoke-Step "candidate-test-$configuration" dotnet @('test', 'Broiler.JSeal.slnx', '-c', $configuration, '--nologo') $sourceRoot $TestTimeoutSeconds $buildEnvironment
        }
    }
    $report.completed = $true
    Write-Host "J19: $($report.lane) package gate passed (BroilerJs, Vm and Both consumers)."
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
