# J02-J03, J19: isolated package consumer and handoff gate

From the repository root, with PowerShell 7+, the .NET 10 SDK and Node.js 24 (for the gate rules):

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

J19 adds a third consumer, `Both`, with exactly the two provider PackageReferences. It runs every
per-provider check for each provider in one process, then registers both in `JsEngineRegistry` and
keeps a realm of each engine live at once to check that their globals stay separate. It is the
only consumer in which both engine families are resolved and loaded together.

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
I10 adds a check for every provider: the module contract types (`IJsModules`, `IJsModuleMap`,
`JsModuleException`) are public in the packaged contracts, no packaged realm implements
`IJsModules`, and no provider declares either module flag before I13.

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

The negative controls run for the `Both` consumer as well.

Reports and successful restore graphs are retained under `test-results/package-consumer/<run-id>`.
Use `-OutputDirectory <new-directory>` to choose a location; an existing directory is refused.
Temporary workspaces are removed on success or failure unless `-KeepWorkspace` is supplied.
CI runs the script on Windows and Linux and uploads the diagnostics even when a step fails.

## J19: version gate

`eng/package-gate.mjs` holds the version-compatibility rules as pure functions; its fixtures are in
`eng/package-gate.test.mjs` (`node --test eng/package-gate.test.mjs`, also run by CI). The script
calls it twice: `gate-source` after staging the feed, before any consumer is built, and `gate`
after all three consumers. Each writes `<name>.request.json` and `<name>.json` to the output
directory; `gate.json` lists every resolved package identity of the two provider builds and the
three consumers, and every staged archive. A rule violation fails the run:

- **Families are single-version.** Within every graph and across all graphs, every `Broiler.VM.*`
  package resolves at one version and every `Broiler.JavaScript.*` package at one version. Each
  family version must equal the pins the build used, and the pins of one family must agree.
- **Provider packages match.** `Broiler.JSeal.Vm` must declare the gated `Broiler.VM` version for
  each engine dependency, `Broiler.JSeal.BroilerJs` the gated `Broiler.JavaScript` version, and
  neither may depend on the other family. A provider resolved without its family is refused.
- **No floating or opportunistic versions.** Pins and candidates must be exact versions; `*`,
  ranges and build metadata are refused. Versions follow NuGet's grammar and order: one to four
  numeric parts (`8.0` is `8.0.0`, a zero fourth part is dropped), prerelease labels compared
  case-insensitively and numeric identifiers numerically. Declared dependency ranges may take any
  NuGet form (`8.0`, `[8.0, )`, `[1.0, 2.0)`, `[1.0]`) but need an inclusive lower bound; floating
  ranges and a dependency with no version are refused. In each graph a dependency must resolve
  exactly at the **highest** lower bound declared for it across that graph, the project's own
  references included, which is what NuGet's unification picks, and inside every declared range.
  A higher resolved version is a floating or opportunistic upgrade and fails; so does a lower one.
- **Missing dependencies fail before release.** Every dependency declared by a staged archive must
  itself be staged inside its range, at a version that is a lower bound some staged archive declares
  for it (its own, or a higher one that unification would pick); every dependency in a graph must be
  resolved.
  Consumer graphs may contain only packages. In CI this gate runs in the validation job, which the
  publish workflow requires before `verify-feed.ps1` and any push.

## J19: evaluating a candidate upstream set

A candidate is a local directory of upstream `.nupkg` archives plus one exact version per family:

```powershell
pwsh -NoProfile -File eng/test-package-consumer.ps1 -CandidateFeed <feed-directory> `
    -CandidateVersion Broiler.VM=0.1.0-preview.4
# Both families at once:
#   -CandidateVersion 'Broiler.VM=0.1.0-preview.4,Broiler.JavaScript=0.1.0-preview.2'
```

Only whole families can be candidates, and a candidate must differ from the current pin: a locally
built archive must never share an identity with a published one. The report's `lane` is
`candidate`; the default run's is `pinned`.

Give a locally packed family a version that can never be published by accident, such as
`0.1.0-preview.4-local.jseal.1`. Mind its order: `4-local` is an alphanumeric identifier, so NuGet
sorts that version above every numeric `preview.N`, `preview.5` included. The gate resolves only
exact versions from the candidate directory into a throwaway cache, so the order does not matter
here; a form such as `0.1.0-preview.4.local.1` sorts between `preview.4` and `preview.5` if it does.
Pack it from a separate worktree of the upstream repository, never from a shared checkout, with that
repository's own `eng/pack.ps1`. Broiler.VM's `pack.ps1 -Version` accepts only `X.Y.Z-preview.N`,
so the J19 validation run set the local version through an untracked `Directory.Build.rsp` in the VM
worktree (`-p:Version=...` and `-p:PackageVersion=...`), which `pack.ps1` reads back and verifies
like any other version. Nothing is pushed.

The checkout is not modified. The script copies its tracked and untracked (not ignored) files into
the temporary workspace, rewrites that copy's `Directory.Packages.props` for the candidate families
only, and adds the candidate directory to that copy's NuGet configuration. Package-source mapping
sends the candidate families' `Broiler.VM.*` / `Broiler.JavaScript.*` patterns, and the exact id of
every archive in the candidate directory, **only** to the candidate directory. Every other package
the checkout's shipping projects already resolved (the `obj/project.assets.json` of `Broiler.JSeal`
and both providers, which carry the whole `Broiler.*` engine closure) is staged from its
package folder, checked against the recorded SHA-512, into a local `pinned-archives` source, and
mapped there by exact id; so everything outside the candidate families restores byte-identical to
the pinned build, without fetching any of them again. Any other
package id, such as the test project's xunit packages, uses the normal sources. The candidate directory must
therefore contain every package it introduces, at the versions they declare, including a new version
of a package the checkout already resolved: that id is served only from `pinned-archives`, and the
restore fails naming it. Nothing falls back to a published version. The copy restores into a new
package cache in the workspace, so unpublished identities never reach the global NuGet cache. The
checkout's shipping projects must have been restored (built or tested once, in `Release-VM` so the
VM provider is included) before a candidate run.

From that copy the script packs, stages and runs all three consumers exactly as in the default
lane, requires every candidate-family package in every graph to be byte-identical (SHA-512) to the
archive in the candidate directory, and then runs `dotnet test Broiler.JSeal.slnx` for `Release`
and `Release-VM` (`-TestTimeoutSeconds`, default 1800). Nothing is published, the packs stay in the
deleted workspace, and there is no fallback to newer versions.

The eventual pin update remains an explicit, separate change: once the candidate packages are
available from the normal feed, edit `Directory.Packages.props` to the candidate versions, then
run both test configurations, the default (pinned) lane of this script and `eng/pack.ps1`.

## Current-source comparisons are a separate lane (documented, not implemented)

**This lane is a documented procedure, not a script.** Nothing in this directory builds or runs a
current Broiler.VM or Broiler.JS source tree, and no report records a current-source comparison.
The nearest existing tool is the [retained review probes](../../diagnostics/J00/README.md), which run
fixtures through current-source engine CLIs; they are run by hand, compare engines rather than JSEAL
providers, and are not wired to this gate.

The intended split is fixed even though the lane is not automated: the gate evaluates package
archives only, and a current-source comparison is a diagnostic that can explain a candidate's
behavior but is never an input to the gate and cannot pass or fail it. The gate refuses project
references in consumer graphs and reads no sibling checkout, so a source build cannot stand in for a
package. To compare against current source today, pack that source as a local candidate (see above)
and run the candidate lane, which is what the J19 validation run did for Broiler.VM.

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
