[CmdletBinding()]
param([ValidateSet('Debug', 'Release', 'Debug-VM', 'Release-VM')][string] $Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    # A fresh directory prevents reports from earlier runs masking missing tests.
    $results = Join-Path 'test-results' ("unit-" + [guid]::NewGuid())
    & dotnet test Broiler.JSeal.slnx -c $Configuration --no-build --nologo `
        --blame-hang-timeout 10m --blame-hang-dump-type none `
        --logger 'trx;LogFilePrefix=broiler' --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
    $executed = 0
    foreach ($report in Get-ChildItem -LiteralPath $results -Filter *.trx) {
        [xml] $trx = Get-Content -LiteralPath $report.FullName -Raw
        $counters = $trx.SelectSingleNode('//*[local-name()="ResultSummary"]/*[local-name()="Counters"]')
        if (!$counters) { throw "No test counters in $($report.FullName)." }
        $executed += [int] $counters.executed
    }
    Write-Host "Executed $executed tests under $Configuration."

    # The -VM pair compiles the cases guarded by #if BROILER_VM_JS and links the VM
    # JSEAL provider, so the conformance run enumerates two engines instead of one.
    $floor = if ($Configuration -like '*-VM') { 100 } else { 50 }
    if ($executed -lt $floor) {
        throw "Executed test count collapsed to $executed under ${Configuration}; expected at least $floor."
    }
} finally { Pop-Location }
