# I02 and I04 buffer transfer measurement

This opt-in probe measures one `TryGetArrayBufferBytes` (I02) or one `NewArrayBuffer` (I04) call
of the VM provider: managed bytes allocated, host crossings charged and fuel charged. It is not a
timing benchmark. The retained [before](before.json) report was collected on Windows x64,
.NET 10.0.12, with tiered compilation disabled, against the pinned Broiler.VM `0.1.0-preview.3`
packages and the captured-intrinsic implementation (typed-array `join` and decimal parsing out,
`%TypedArray%.prototype.set` over per-byte value chunks in).

The native host operations these slices adopt are not in the pinned packages, so the adoption and
its `after` report wait for a Broiler.VM release (roadmap I02, I04). The probe is kept here so that
the comparison is made on identical sizes with identical code.

Per operation, minimum of three samples, `in-step` mode (the call made inside a running step, which
is how a DOM binding reaches it). `host-turn` mode, where each call opens its own turn, shows the
same host-call and fuel figures.

| Bytes | Read: allocated / host calls / fuel | Construct: allocated / host calls / fuel |
| ---: | ---: | ---: |
| 0 | 152 / 1 / 8 | 584 / 2 / 26 |
| 1 | 576 / 3 / 33 | 1,161 / 4 / 43 |
| 64 | 1,856 / 3 / 96 | 5,778 / 4 / 232 |
| 8,000 | 130,904 / 3 / 8,032 | 602,350 / 4 / 24,040 |
| 8,001 | 131,312 / 5 / 8,057 | 602,845 / 6 / 24,057 |
| 65,536 | 1,075,928 / 19 / 65,760 | 4,953,325 / 20 / 196,760 |
| 1,048,576 | 17,145,104 / 265 / 1,051,752 | 78,956,912 / 266 / 3,147,602 |

## What each measurement includes

- **Allocated:** `GC.GetTotalAllocatedBytes(precise: true)` around the measured calls, so the
  turn's guest thread is included. Each case creates its realm, source bytes and (for reads) the
  buffer before measuring, warms one call, then records three samples; iterations shrink with size
  (64 up to 64 bytes, 16 at 8,000/8,001, 8 at 64 KiB, 1 at 1 MiB).
- **Host calls and fuel:** the difference in `VmRuntime.GetBudgetSnapshot()` consumption for
  `HostCalls` and `Fuel` across the samples, read through diagnostic-only reflection on the realm's
  runtime. A turn invocation is not a host call.
- Each read is checked for its length and last byte, and a checksum ensures every call ran.

## Reproduce

From the repository root:

```powershell
$previousTiering = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    dotnet run --project diagnostics/I02-I04/BufferTransfer/BufferTransfer.csproj `
        -c Release-VM -- current test-results/i02-i04-buffers.json
    if ($LASTEXITCODE -ne 0) { throw 'Buffer transfer probe failed.' }
} finally {
    $env:DOTNET_TieredCompilation = $previousTiering
}
```
