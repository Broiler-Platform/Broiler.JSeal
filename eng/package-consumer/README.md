# J02-J03: isolated package consumer

From the repository root, with PowerShell 7+ and the .NET 10 SDK:

```powershell
pwsh -NoProfile -File eng/test-package-consumer.ps1
```

The script uses the existing `eng/pack.ps1` to build and validate all three shipping packages.
`-Version X.Y.Z-preview.N` optionally selects the same local version for all three. It does not
publish packages. Normal provider builds/restores use this checkout's NuGet configuration and
credentials, as an ordinary build does.

The consumer template is outside the solution and contains **one PackageReference**, to the
selected provider. It acquires `Broiler.JSeal` transitively. It has no project references and names
no engine-specific types or assembly preload APIs. Each provider is built and run separately in
a new directory under the operating system's temporary folder, outside this checkout.

## What is isolated

1. The three new JSeal packages go into a private local feed. External dependencies come from the
   exact `.nupkg` archives resolved in the provider projects' assets files, with SHA-512 checked
   against those files. This preparation may use the normal NuGet package cache; it never copies
   DLLs from development outputs or reads test-project dependencies.
2. Each consumer restores from **only** that local feed into its own empty package cache and
   intermediate directory. Parent MSBuild configuration, central versions, NuGet fallback folders,
   the SDK's implicit `library-packs` source, HTTP cache reuse and remote sources are excluded.
   The resolved graph must contain only staged package identities/hashes and exactly the intended
   feed/cache paths. NuGet audit is disabled for this offline restore; the normal pack/build restore
   keeps its existing audit behavior.
3. Each consumer is a fresh `dotnet Consumer.dll` process. Startup hooks, additional deps and shared
   stores are cleared for consumer commands. Loaded Broiler assemblies must reside in its output
   directory. The other provider/engine and test assemblies must be absent.

This verifies standalone packaging, not an entirely offline clean build of the engine dependencies.
The .NET SDK and framework remain installed prerequisites. No sibling engine checkout is used.

## Checks and failure controls

Each provider must create a realm, evaluate `6 * 7`, return a parenthesized non-ASCII string,
invoke a native host callback with the correct realm/arguments, and dispose idempotently. Disposed
realms must reject queued work. The expected assembly floor is explicit in `Program.cs`:

| Consumer | Required assemblies (in addition to `Broiler.JSeal`) |
|---|---|
| BroilerJs | `Broiler.JSeal.BroilerJs`, `Broiler.JavaScript.Runtime`, `BuiltIns`, `Globals`, `Modules`, `Storage` |
| Vm | `Broiler.JSeal.Vm`, `Broiler.VM.Runtime`, `Broiler.VM.Profile.JavaScript`, `Broiler.VM.Profile.JavaScript.Compiler` |

J03 adds ordinary, zero-argument and strict function `arguments` checks for both providers. The JS
consumer also evaluates `(function(x){ return eval('arguments[0] + x'); })(7)` and requires 14;
VM function-scope direct eval remains V13-V15's separate work. These run through the sole provider
PackageReference, with no engine-specific preload. The JS consumer verifies that installing Modules
does not enable `Modules` or `DynamicImport` capabilities without the existing host opt-in.

The report records the full shipped and loaded sets, assembly versions/hashes, resolved package
identities/hashes, source revision/working-tree state, harness input hashes and command outputs.

Two negative controls run for each provider after the successful smoke test:

- An empty feed with a second fresh package cache must fail restore with a missing-package error,
  despite the successful restore's cache still being present.
- A copy of the executable output without `Broiler.JSeal.dll` must fail to launch with that assembly
  missing, despite the package cache and repository build outputs still being present.

Timeouts and unexpected exit codes fail the script. The report distinguishes expected negative
control exits from failures of the smoke test. Build/restore steps default to 300 seconds and
execution steps to 30 seconds; use `-BuildTimeoutSeconds` / `-RunTimeoutSeconds` to override.

Reports and successful restore graphs are retained under `test-results/package-consumer/<run-id>`.
Use `-OutputDirectory <new-directory>` to choose a location; an existing directory is refused.
Temporary workspaces are removed on success or failure unless `-KeepWorkspace` is supplied.
CI runs the script on Windows and Linux and uploads the diagnostics even when a step fails.

## Scope and observed follow-up

The JS provider now declares `Broiler.JavaScript.Modules` at `0.1.0-preview.1`, matching its other
engine packages. `JSArguments` currently lives in that assembly even for ordinary classic scripts.
The engine's `JSContext` bootstrap calls `JSEngine.EnsureBuiltInsAssemblyLoaded`, which loads the
assembly and runs its module initializer. `ModulesAssemblyInitializer` registers `JSArguments` with
`JSArgumentsBuilder`; the builder also has a lazy loading fallback. No provider-side initialization
shim or consumer preload is required. The successful standalone run records Modules among the
assemblies actually loaded from the consumer output.

The regression was observed failing with `JSArgumentsBuilder is not initialized` before the package
dependency was added, then passing with the dependency. Both providers' isolation controls still
pass. This dependency repairs arguments creation; it makes no claim about module evaluation support.

**Separate Broiler.JS architectural follow-up:** evaluate moving `JSArguments` and its initialization
out of the Modules package into a layer shared by ordinary scripts. Its current BuiltIns, Engine,
Runtime and LinqExpressions dependencies need an upstream dependency-graph review before choosing
the destination. That extraction is not part of J03's package repair.

An exploratory string check also found a separate behavior in the pinned VM `0.1.0-preview.3`:
`EvaluateClassicScript("'hello'")` returns Undefined, while `EvaluateClassicScript("('hello')")`
returns the string. The bare string is a directive-prologue case; its completion value needs an
upstream semantic regression/fix. The package smoke uses the parenthesized form to check primitive
marshaling and UTF-8 independently of that semantic defect. J02 does not fix it.
