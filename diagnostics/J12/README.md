# J12 callback allocation measurement

This opt-in probe measures allocated bytes, not execution time. It uses the actual VM provider
and the pinned `Broiler.VM.*` **0.1.0-preview.3** packages. It does not alter a sibling checkout.
The retained [before](before.json) and [after](after.json) reports were collected on Windows x64,
.NET 10.0.12, with tiered compilation disabled. All three samples agree for every row.

| Arguments | Adapter before → after (bytes/call) | VM host roundtrip, unchanged | JSeal roundtrip before → after |
| ---: | ---: | ---: | ---: |
| 0 | 0 → 0 | 0 | 192 → 192 |
| 1 | 48 → 0 | 96 | 384 → 336 |
| 8 | 216 → 0 | 432 | 1,056 → 840 |
| 9 | 240 → 0 | 480 | 1,152 → 912 |
| 32 | 792 → 0 | 1,584 | 3,360 → 2,568 |

The removed allocation is one `Broiler.JSeal.JsValue[]` per non-empty callback: on this runtime,
24 bytes of array overhead plus 24 bytes per element. Calls through eight arguments now use a
managed inline buffer. Larger calls rent an array, returning it with `clearArray: true` in `finally`.
Cold pool misses can still allocate, and recursion can require additional rented arrays.

## What each measurement includes

- **adapter:** The real private `VmRealm.Trampoline` delegate, called with already prepared numeric
  `JsHostValue` arguments inside a live VM host turn. Diagnostic-only reflection obtains this delegate
  and the host realm before measurement. It excludes the VM's argument projection, JSeal's outbound
  conversion and the outer turn. This is the allocation directly targeted by J12.
- **vm-host-roundtrip:** `JsHostRealm.Invoke` calling a raw VM host callback without JSeal adaptation.
  For non-empty calls, the VM allocates its internal argument array in `UnwrapAll` and a
  `JsHostValue[]` in `Bind`. Those two arrays remain: together, `48 + 48 × argument count` bytes in
  these measurements. The sibling source corroborates these sites in
  `src/Broiler.VM.Profile.JavaScript/JsHostRealm.cs`; the reports identify the measured package DLL
  by SHA-256 so that source-checkout behavior is not confused with packaged behavior.
- **jseal-roundtrip:** `IJsRealm.Invoke` calling a JSeal host method while the VM turn is already
  active. Includes the VM roundtrip, JSeal's outbound `JsHostValue[]` and invocation closures,
  plus the callback adapter. Its before/after difference exactly matches the adapter measurement.

Each case creates its realm, functions, delegates and argument arrays before measuring, warms
128 calls, then records three samples of 512 calls using `GC.GetAllocatedBytesForCurrentThread`.
The callback returns the argument count; a checked checksum ensures the measured calls ran.
The input uses numbers to isolate argument-array allocation from identity boxing and other value
conversions. These results do not claim zero allocations for object arguments, arbitrary guest
code, first calls, exceptions, or engine crossings generally. They are not timing benchmarks.

## Reproduce the current measurement

Run from the repository root in PowerShell:

```powershell
$previousTiering = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    dotnet run --project diagnostics/J12/CallbackAllocations/CallbackAllocations.csproj `
        -c Release-VM -- current test-results/j12-allocations.json
    if ($LASTEXITCODE -ne 0) { throw 'Callback allocation probe failed.' }
} finally {
    $env:DOTNET_TieredCompilation = $previousTiering
}
```

`before.json` was captured from the J11 working tree before replacing the non-empty
`new JsValue[arguments.Length]` in the trampoline. Reports retain source/provider/package hashes
and runtime information. The probe is deliberately outside the ordinary test suite: runtime and
pool behavior may change exact byte totals. Behavioral conformance covers argument counts 0, 1,
8, 9 and 32, receiver identity, explicit undefined versus Missing, new.target, nested callbacks,
guest and host exceptions, and subsequent reuse.
