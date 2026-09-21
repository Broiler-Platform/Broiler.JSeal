# J00: retained review baseline

This is an **opt-in diagnostic bundle**, outside `Broiler.JSeal.slnx`, the xUnit projects, normal
CI, and package publication. It retains minimal reproductions from the 2026-09-19 review without
changing production behavior. J01 owns improvements to VM's general differential runner; J02 owns
isolated package-consumer testing. This fixed bundle implements neither of those slices.

## Reproduce

Requirements: .NET 10 SDK, PowerShell 7+, Git, and access to the package feeds already configured
by each repository. Restore authentication is the same as for a normal build; do not put credentials
in commands or evidence. The complete run also needs Broiler.JS and Broiler.VM source checkouts.

From the JSeal repository root, with both engine repositories in sibling directories:

```powershell
pwsh -NoProfile -File diagnostics/J00/run.ps1
```

For another directory layout:

```powershell
pwsh -NoProfile -File diagnostics/J00/run.ps1 `
  -BroilerJsRoot /path/to/Broiler.JS `
  -BroilerVmRoot /path/to/Broiler.VM
```

Run just one part when the sibling source checkouts or their build prerequisites are unavailable:

```powershell
pwsh -NoProfile -File diagnostics/J00/run.ps1 -Mode Providers
pwsh -NoProfile -File diagnostics/J00/run.ps1 -Mode Sources
```

`Providers` runs the JSeal source adapters against the engine NuGet versions in
[`Directory.Packages.props`](../../Directory.Packages.props), in a separate process for each provider.
It is **not** an isolated consumer of packaged JSeal: the adapters are project references. This is
enough to exercise the original engine dependency finding; the completed J02/J03
[package-consumer check](../../eng/package-consumer/README.md) tests the packaged composition.
The registry fixture uses another process so clearing its registry/environment variable cannot
interfere with the ordinary test suite or the user's environment.

`Sources` rebuilds both CLI projects from their supplied source checkouts and runs the **same**
[`language.js`](language.js) under explicit script goals. It does not replace JSeal's pinned engine
packages with those source builds. JSeal configuration is `Release-VM`; both source hosts use
`Release`. Builds restore dependencies for the selected configuration.

The collector defaults to a new `test-results/J00/<id>/` directory, which Git ignores. Use
`-OutputDirectory <new-directory>` to choose another location; an existing directory is refused.
`-ProbeTimeoutSeconds` defaults to 30; `-BuildTimeoutSeconds` to 300. Timeout terminates the child
process tree and records failure. The entire run is finite and does not fetch source checkouts,
modify package pins, publish packages, or change engine source.

## Reading the result

The run writes `observations.json` and per-command stdout/stderr logs. The JSON retains command
arguments, working directories, exit codes, timeouts, and command output; root paths are replaced
with `<JSeal>`, `<Broiler.JS>`, and `<Broiler.VM>`. `stdoutLog`/`stderrLog` name the raw files emitted
by a fresh run; the retained JSON is self-contained even without those separate files.

Identity includes full repository commits and dirty-file lists, SDK/OS/architecture/PowerShell,
resolved engine package versions and NuGet SHA-512 values, fixture/input SHA-256 values, and
built assembly versions/SHA-256 values. Hashes are evidence of the actual inputs/outputs, not a
claim that binaries will be identical across SDKs, paths, or platforms. The collector's input
hash records the file bytes at capture time; a Git line-ending conversion can change that hash.

Each observation records `expected`, `actual`, and an outcome:

- `matches-target`: this individual probe agrees with its stated desired behavior or availability.
- `differs-from-target`: a recorded difference; it may be a defect, unsupported feature, or deliberate
  exclusion. Consult the owning slice. Availability alone never proves full implementation.
- `completed=false`, a `failure`, or a nonzero collector exit: infrastructure/capture failure. Do not
  interpret a missing result as unsupported functionality or a successfully reproduced defect.

Exit code 0 means all requested fixtures completed and their observations were collected. It does
**not** mean the engines conform or the known defects are fixed. Ordinary differences do not fail
the collector. Changed observations after a fix must be reviewed; this script neither accepts
baseline updates automatically nor compares against a stored wrong answer as its correctness oracle.

The collector checks a completion marker, exact fixture count, unique case IDs, and string values.
There are 10 observations per provider, one registry observation, and 39 per source host: **99**
in the full capture. Those are inventory counts, not a conformance score. An intentional fixture
change must update its completion/count contract explicitly.

## Retained identities and commands

The checked-in [capture](baseline-2026-09-19.json) records this baseline:

| Repository | Commit |
|---|---|
| JSeal | `d64ebec877e9cd017f1ddd0e9e8f1b52c592d24f` plus the diagnostic/roadmap working-tree additions listed in the capture |
| Broiler.JS | `73f071d1f1e1d5404018bc5337d6dafc74011676` |
| Broiler.VM | `405fe2d234bbae69b1ea70dac603bbfe24978760` |

It was captured on Windows X64 with SDK `10.0.401`. It does not establish Linux reproduction.
The direct JSeal JS dependencies are `0.1.0-preview.1`; VM dependencies are `0.1.0-preview.3`.
The complete resolved package list and exact commands are in the capture. Current checkouts can
produce different results; to reproduce the historical engine behavior, use these commits in
separate checkouts and supply their roots. The collector deliberately does not reset repositories.

Equivalent manual commands, useful when investigating one fixture:

```powershell
# Run from JSeal; each invocation is a fresh process.
dotnet build diagnostics/J00/ProviderProbes/ProviderProbes.csproj -c Release-VM
dotnet diagnostics/J00/ProviderProbes/bin/Release-VM/net10.0/ProviderProbes.dll broiler-js
dotnet diagnostics/J00/ProviderProbes/bin/Release-VM/net10.0/ProviderProbes.dll broiler-vm
dotnet diagnostics/J00/ProviderProbes/bin/Release-VM/net10.0/ProviderProbes.dll registry
```

The following commands belong to the optional **external source checkouts**, not JSeal:

```powershell external
# Run each build from its owning repository, then pass an absolute JSeal fixture path.
# Broiler.JS:
dotnet build Broiler.JS/Broiler.JavaScript/Broiler.JavaScript.csproj -c Release
dotnet Broiler.JS/Broiler.JavaScript/bin/Release/net10.0/BroilerJS.dll --script-host /path/to/Broiler.JSeal/diagnostics/J00/language.js
# Broiler.VM:
dotnet build src/compositions/Broiler.VM.Composition.JavaScript.Cli/Broiler.VM.Composition.JavaScript.Cli.csproj -c Release
dotnet src/compositions/Broiler.VM.Composition.JavaScript.Cli/bin/Release/net10.0/Broiler.VM.Composition.JavaScript.Cli.dll --all --quiet /path/to/Broiler.JSeal/diagnostics/J00/language.js
```

Prefer the collector for bounded runs and metadata. The manual commands intentionally have no
timeout wrapper. Source fixtures are script-goal probes; there is no module-goal evidence here.

## Six JSeal findings and regression ownership

| Fixture IDs | Desired behavior | Retained difference | Eventual regression location |
|---|---|---|---|
| `J03.arguments` | Ordinary `arguments[0]` returns 7 | JS package composition throws `JSArgumentsBuilder is not initialized` | J02/J03 package-consumer smoke tests; do not hide the missing dependency by loading Modules in test setup |
| `J04.eval-restricted` and permissive control | Restricted guest indirect eval is catchably refused; permissive control returns 42 | VM returns 42 in both realms when called through host script | `Broiler.JSeal.Tests/JsealConformanceTests.Source.cs` |
| `J05.callable-proxy` | Function tag and invocation result 42 | JS returns Object tag, invocation still returns 42 | `Broiler.JSeal.Tests/JsealConformanceTests.cs` / `.Members.cs` |
| `J06.getter-exception`, `J07.coercion-exception` | JsEngineException carries thrown 42 | JS leaks engine-specific JSException for both | `Broiler.JSeal.Tests/JsealConformanceTests.Members.cs` and coercion coverage |
| `J08.undefined-precedence` | Existing ordinary undefined value wins | JS returns the handler's string | `Broiler.JSeal.Tests/JsealConformanceTests.ExoticObjects.cs` |
| `J10.default-after-removal` | Remaining registered VM provider becomes default | Default unavailable while HasAny is true | `Broiler.JSeal.Tests/JsealRegistryTests.cs`, isolated from concurrent registry consumers |

The named-property fixture does not repeat the ordinary property in `SupportedNames`. It therefore
avoids the invalid duplicate-name fixture from the exploratory review. Sparse index withdrawal is
not treated as a confirmed failure here: J09 must first resolve the dense/sparse contract. Array
Proxy classification and the unsuccessful buffer-species-tampering experiment are also excluded
from the confirmed-defect inventory.

## VM differences and ownership

These are engine-level cases. Put their regression tests in the VM JavaScript profile's owning
test harness and relevant pinned Test262 subset; they should not become JSeal's language-conformance
suite. The existing VM `src/tests/differential/` corpus can retain portable cases through J01.

| Fixture | Current-source JS | Current-source VM | Owning slices |
|---|---|---|---|
| Array species | true | false | V07-V08 |
| Concat spreadability | `a` | `[object Object]` | V09 |
| String RegExp guard | TypeError | false | V04 |
| Numeric coercion order | `ab` | `ba` | V01 |
| New symbol on frozen object | 0 keys added | 1 key added | V02-V03 |
| Mapped arguments | 7 | 1 | V05-V06 |
| Direct eval of caller local | 7 | EvalError | V13-V15 |
| Non-ASCII normalization | true | TypeError | F07-F08 |
| Unicode property escape | true | SyntaxError | F07/F09 |
| Resizable buffer state | `true\|16\|function` | `\|\|undefined` | F04-F06 |

Availability probes retain each name separately:

| Missing from source VM in this capture | Owning slices |
|---|---|
| BigInt, BigInt64Array, BigUint64Array | B01-B08 |
| Float16Array, DataView.prototype.getFloat16 | F01-F03 |
| Iterator | F11-F15 |
| Array.fromAsync | F16 |
| RegExp.escape | F10 |
| JSON.rawJSON / JSON.isRawJSON | F17 |
| Disposal symbols, SuppressedError, DisposableStack, AsyncDisposableStack | F18-F22 |
| Intl | D01: scope/data decision |
| SharedArrayBuffer, Atomics | D02: deliberate exclusion pending an agent-model decision |
| ShadowRealm | D04: scope/isolation decision |

Controls record APIs that already exist in VM: WeakRef, FinalizationRegistry, Object.groupBy,
Map.groupBy, Promise.withResolvers, Promise.try, Math.f16round, Set.prototype.union,
ArrayBuffer.prototype.transfer, and Array.prototype.toLocaleString. Their presence is not proof
of full semantics. In particular, FinalizationRegistry's intentionally inert cleanup is a
source-documented limitation in VM's `JsCollections.cs`, not a GC-timing assertion in this bundle.

The provider inventory separately records Array.fromAsync, Object.groupBy, and Map.groupBy to
show why a source-engine result cannot be assumed for a pinned engine package. All three are
present in the source JS host; all three are absent from the pinned JS provider in this capture.

No full Test262 results, browser/worker/module integration claims, performance claims, or overall
completeness percentages follow from these fixtures.
