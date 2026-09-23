# JSEAL module contract (design)

Design slice I09 in the [integration roadmap](roadmap.integration.md#i09--design-an-engine-neutral-module-contract),
written 2026-09-21. It began as a proposal. **The contract types exist since I10**, and no provider
gains a capability because of this document; see [I10 implementation status](#i10-implementation-status)
for what was built and where it departs from the design. The rest belongs to I11-I13 and the upstream
slices listed at the end. The [JSEAL overview](jseal.md) describes the contracts that exist today.

The design adds an **optional** interface that a realm implements only when it can run module
graphs. It leaves `IJsRealm`, `IJsSource` and the minimum provider implementation unchanged. No
engine, filesystem, network, URL or DOM type appears in it: specifiers, keys and labels are
strings, source is text, and values are `JsValue`.

## Current behavior

The observations below were checked on 2026-09-21. JSeal was at `37d9825`. Broiler.JS was tested
from its pinned package `0.1.0-preview.1`. The VM results come from its **current source** at
`484f389`, run through the JavaScript CLI, not from the pinned `0.1.0-preview.3` packages; they
show what the engine can do, not what JSeal can reach today. Node v24.17 gave the reference answers.

### JSeal

- `JsCapabilities.Modules` and `DynamicImport` exist, but their XML documentation says JSEAL has
  no module contract ([JsCapabilities.cs](../Broiler.JSeal/Hosting/JsCapabilities.cs)).
- `IJsSource` has no module entry point ([IJsRealmCapabilities.cs](../Broiler.JSeal/Realm/IJsRealmCapabilities.cs)).
  The capability coverage test exempts both flags as "not expressible"
  ([JsealConformanceTests.cs](../Broiler.JSeal.Tests/JsealConformanceTests.cs), `NotExpressibleThroughTheContracts`).
- `BroilerJsEngineProvider.ModuleSupport` advertises both flags whenever a host callback returns
  true. No JSEAL operation is behind that claim ([BroilerJsEngineProvider.cs](../Broiler.JSeal.BroilerJs/BroilerJsEngineProvider.cs)).
  Created and adopted realms both copy the provider's `Capabilities`, so the callback applies to
  both ([BroilerJsRealm.cs](../Broiler.JSeal.BroilerJs/BroilerJsRealm.cs), both constructors).
- **Adoption.** `BroilerJsEngineProvider` implements `IJsRealmAdoption`. `TryAdopt` wraps any
  `JSContext` the host built, and that includes a `JSModuleContext`. The remark on `TryAdopt` says
  this is the path the bridge's `<script type="module">` support needs. The adopted realm does not
  own the context. The context keeps its own `Resolve`, its own source reads and its pre-registered
  specifiers, and JSEAL sees none of them.
- **Broiler.JS realm:** `BroilerJsRealm` creates a plain `JSContext`, not a `JSModuleContext`
  ([BroilerJsRealm.cs](../Broiler.JSeal.BroilerJs/BroilerJsRealm.cs)). A probe ran `import('./x.mjs')`
  from `EvaluateHostScript`. The promise rejected with `TypeError: ... dynamic import is not
  supported in this host`, both with and without GuestEval.
- **VM realm:** `VmSourceProvider.Answer` never checks `JsFormat.TryReadModuleRequest`, so it treats
  a guest's module request like an `eval` request ([VmSourceProvider.cs](../Broiler.JSeal.Vm/VmSourceProvider.cs)).
  Without GuestEval, the request is refused as guest source. With GuestEval, the code shows the
  payload reaching the classic-script compiler. The probe saw `SyntaxError: the module './x.mjs'
  could not be loaded from '': HostFailure/ProviderRefused` in both cases. The specifier text never
  ran as script. The provider registers no `JavaScriptProfile.ResolveCapability`
  ([VmEngineProvider.cs](../Broiler.JSeal.Vm/VmEngineProvider.cs)).

### The existing consumer: Broiler.HtmlBridge

Broiler.HtmlBridge (source at `f316987`, 2026-09-21) is the only known consumer that runs modules,
and it runs them **around** JSEAL, not through it:

- **Broiler.JS path.** `BridgeModuleContext` (`src/Broiler.HtmlBridge.Dom/BridgeModuleContext.cs`)
  is a `JSModuleContext`. Its overrides resolve specifiers as URLs and read source through the
  bridge's fetch under the page's CSP. `ScriptEngine` constructs it directly and calls
  `RunScriptAsync` on it, and `DomBridgeHostUtils.AdoptRealm` hands it to `TryAdopt` so that DOM
  code sees a JSEAL realm. Its remarks record that JSEAL has no resolver or loader contract.
- **The flags.** `JsEngineHosting.EnsureRegistered` (`src/Broiler.HtmlBridge.Scripting/JsEngineHosting.cs`)
  sets `BroilerJsEngineProvider.ModuleSupport = static () => EngineModuleSupport.Available`. That
  value comes from a one-time probe of the engine. The bridge's own decision to run modules reads
  `EngineModuleSupport.Available` directly (`ScriptEngine.cs`, `SubDocuments.cs`). A search of
  `src/` found no code that reads `JsCapabilities.Modules` or `DynamicImport` from a realm; the
  only mention is in a remark.
- **VM path.** `VmScriptEngine` drives Broiler.VM directly, without a JSEAL realm, through its own
  `VmSourceProvider` and `VmModuleMap` (`src/Broiler.HtmlBridge.Scripting.Vm`). That provider
  recognizes `JsFormat.TryReadModuleRequest`, answers `NotFound` for a module the document did not
  declare and `Refused` for a source the front end rejects. When the CSP forbids `eval` it
  registers no provider at all, so `import()` is refused as well
  (`VmScriptEngineTests.ADynamicImportIsRefusedWhenThePolicyForbidsEvaluation`).

### Broiler.JS (`Broiler.JavaScript.Modules` 0.1.0-preview.1)

`JSModuleContext` derives from `JSContext`. Its only public entry points are file- and folder-shaped
(`RunAsync(folder, file)`, `RunScriptAsync(script, moduleFolder, ...)`). Its extension points are
protected virtual methods: `Resolve`, `LoadModuleAsync`, `GetModuleDirectory`, `GetModuleUrl`,
`ReadModuleSourceAsync` and `CompileModuleAsync`. By default, `Resolve` and `ReadModuleSourceAsync`
use the filesystem. The run methods wrap evaluation in `Task.Run(() => AsyncPump.Run(...))`
(`JSModuleContext.cs` in Broiler.JS). Modules are cached by the string that `Resolve` returns,
but `LoadModuleAsync` first looks up the **raw specifier** in the same cache and only calls
`Resolve` when that lookup misses. Any specifier whose text equals an existing cache entry is
therefore answered without asking `Resolve`. That covers the pre-registered `module` and `clr`,
and also any specifier spelled exactly like an earlier canonical key. The constructor also puts
`assert` and `global` on the global object, which a plain `JSContext` does not have.

A probe subclass replaced `Resolve`, `GetModuleDirectory` and `ReadModuleSourceAsync` with an
in-memory map. It ran the same graphs used for the VM:

| Case | Broiler.JS 0.1.0-preview.1 | VM `484f389` CLI | Node |
|---|---|---|---|
| Live binding after exporter `x++` | `1` (snapshot) | `2` | `2` |
| Namespace tag and key order | `[object Object]`, `x,bump` | `[object Module]`, `bump,x` | same as VM |
| Cyclic read of an uninitialised `let` | `undefined` | `ReferenceError` | `ReferenceError` |
| Importer sees a dependency's value after top-level await | `before` | `after` | `after` |
| Static import and `import()` give the same namespace, one evaluation | yes | yes | yes |
| Two dynamic graphs sharing a dependency evaluate it once | not probed | yes | yes |
| Second `import()` of a module whose body threw | resolves | **resolves** | rejects with the same error |
| Missing module | `TypeError` | `TypeError` (dynamic); host diagnostic (static) | error |
| Bare specifier `module` | resolved from the built-in cache without calling `Resolve` | refused by the CLI host | error |

The current Broiler.JS source documents the live-binding gap too: imports are not yet live
bindings (`Broiler.JavaScript.Modules.Tests/ModuleImportImmutabilityTests.cs`). The Broiler.JS
adapter therefore cannot meet the `Modules` promise below without upstream engine work. JSEAL
must not add linking to hide the gap.

### Broiler.VM

The profile implements module records, live-binding slots, namespace objects, cycle states and
top-level-await evaluation promises (`JsModule.cs`: `JsModuleRecord`, `JsModuleInstance`,
`JsModuleNamespace`). All of these types are internal. The profile reads no files and derives no
keys. The composition resolves the whole graph before compilation (`JsModuleUnit`,
`JsResolvedRequest` and `JsCompiler.Compile(scripts, modules)`, all public in 0.1.0-preview.3). A
link step then asks the composition to confirm each resolution through `ResolveCapability`
(signature `(bytes)->unit`, caller thread, non-reentrant). The request carries the referrer, the
specifier and the key, separated by NUL characters. For `import()`, the profile sends a
`JsFormat.ModuleRequest(referrer, specifier)` payload to the source provider. The provider's
`Answer` must return a compiled artifact **synchronously** (`SourceProvider.cs` and `ModuleGraph.cs`
in the CLI composition).

The pinned `JsHostRealm` has no public operation that runs a module graph inside an existing
realm, and none that answers a module request later. JSeal realms are created with
`VmExternalSuspensionMode.Disabled`.

Two unrelated VM defects came up during probing. They are listed as follow-ups:

- A module whose evaluation threw can be imported again successfully.
- A module containing two top-level destructuring declarations (`const [a, b] = ...; const [c, d] = ...;`)
  is refused with `DuplicateLexicalDeclaration` for an empty name. The same code runs as a script.

## Decision

### Shape and discovery

```csharp
namespace Broiler.JSeal;

// Optional. A realm implements it only when Capabilities includes Modules.
public interface IJsModules
{
    // At most one map per realm; a second call throws InvalidOperationException.
    IJsModuleMap OpenModuleMap(IJsModuleHost host, JsModuleOptions options);
}

public interface IJsModuleHost
{
    // Synchronous and free of I/O: maps (referrer, specifier, attributes) to a canonical key.
    JsModuleResolution Resolve(in JsModuleRequest request);

    // Asynchronous and cancellable: supplies source for a key. Called at most once per key per map
    // unless an earlier call failed.
    ValueTask<JsModuleSource> LoadAsync(JsModuleKey key, CancellationToken cancellationToken);

    // Thread-safe. Runs the task later, on the realm's thread, serialized with other realm work and
    // never inside another realm operation. This is the host's event loop.
    void QueueRealmTask(Action task);
}

public interface IJsModuleMap : IDisposable
{
    // Resolves, loads and links the static graph. It does not evaluate.
    ValueTask<IJsModule> LoadAsync(string specifier, JsModuleReferrer referrer, CancellationToken cancellationToken = default);
    // True only for a module that is Linked or later; see "Visibility" below.
    bool TryGetModule(JsModuleKey key, [NotNullWhen(true)] out IJsModule? module);
    bool HasPendingLoads { get; }
}

public interface IJsModule
{
    JsModuleKey Key { get; }               // readable after disposal
    string SourceLabel { get; }            // readable after disposal
    JsModuleStatus Status { get; }         // Linked, Evaluating, EvaluatingAsync, Evaluated, Errored
    JsValue Evaluate();                    // a realm promise; every call returns the same promise
    JsValue GetNamespace();                // the namespace object, identical on every call
    JsValue EvaluationError { get; }       // Missing unless Errored
}
```

Supporting types are small value types:

- `JsModuleKey(string Value)`: non-empty, contains no U+0000, compared by ordinal equality.
- `JsModuleRequest`:
  - `Referrer`
  - `Specifier`
  - `Attributes` (an ordered list of key/value strings)
  - `Kind` (`Static`, `Dynamic` or `HostRoot`)
- `JsModuleReferrer`: one of `Module(key)`, `Script(label)` or `Host(baseKey?)`.
- `JsModuleResolution`: `Resolved(key)` or `Failed(JsModuleFailure, message)`.
- `JsModuleFailure`: `NotFound`, `Refused`, `UnsupportedSpecifier` or `UnsupportedAttributes`.
- `JsModuleSource`: `Text`, an optional `Label`, and `Failed(JsModuleFailure, message)`.
- `JsModuleOptions`: `AllowDynamicImport`, `AllowImportFromScripts` and `MaxModules`.
- `JsModuleException`: carries `Phase`, `Specifier`, `Referrer`, the key if known, the label, and
  `Thrown` (a `JsValue`).

The phases are `Resolve`, `Load`, `Parse` and `Link`. Evaluation errors are guest errors and use
`JsEngineException` instead.

**Visibility.** `JsModuleStatus` has no state for a module that is still loading or that failed to
link, because the host never holds such a module. `TryGetModule` returns false for a key whose
graph has not finished linking, and `map.LoadAsync` is the only way to wait for it. When linking
fails, the records of that graph stay unlinked and invisible. Their loaded sources are kept, so a
later `map.LoadAsync` or `import()` that reaches them links again without calling `LoadAsync` for
those keys. This matches the ECMAScript rule that a failed `Link()` returns modules to `unlinked`.

**Adopted realms never implement `IJsModules`.** A realm returned by `IJsRealmAdoption.TryAdopt`
wraps a context the host built and still owns. For Broiler.JS that context may be a
`JSModuleContext` with its own `Resolve`, its own source reads and pre-registered `module`/`clr`
entries, so JSEAL cannot guarantee rules 1 and 2 for it. The realm therefore does not implement
`IJsModules`, and under the owner decision proposed below it does not advertise `Modules` or
`DynamicImport` either. A host that runs modules in a context it owns keeps doing so through the
engine's own types, as the bridge does today. A host that wants the contract creates the realm with
`CreateRealm` and opens a map. Only a realm whose provider created the engine context implements
`IJsModules`.

**Why an optional interface.** A host checks both the flag and the type:
`realm.Capabilities.HasFlag(Modules) && realm is IJsModules modules`. A realm without module support
implements nothing new. Adding members to `IJsSource`, as a remark in the coverage test once
suggested, would have forced every provider to implement a refusal. It would also have mixed module
graphs with classic evaluation. `IJsRealmAdoption` already sets this precedent for optional provider
features.

### Rules

1. **Identity.** The host's `Resolve` produces the canonical key. JSEAL and providers never
   normalize, derive or compare keys except by ordinal equality. A map holds one module record per
   key. A provider caches each successful resolution per (referrer, specifier, attributes), as
   ECMAScript requires. Failed resolutions and failed loads are **not** cached, so a later import
   calls the host again.
2. **No built-in specifiers.** Every specifier goes through `Resolve`, including bare names. A
   provider must not satisfy a request from an engine-registered cache the host did not approve.
   Broiler.JS pre-registers `module`, and `clr` when CLR integration is enabled. The adapter must
   disable both or send them through the host.
3. **Loading.** The provider calls `LoadAsync` on the realm's thread. It must not touch the realm
   from the completion. It applies the result in a task sent through `QueueRealmTask`, even when
   the `ValueTask` has already completed, so ordering does not depend on how the host completes it.
   Concurrent requests for one key share one load. The cancellation token fires only when the map
   or the realm is disposed. Cancelling the host's own `IJsModuleMap.LoadAsync` token only abandons
   that host's wait. The load still finishes for any other waiter.
4. **Linking** is performed by the engine. Link errors are `SyntaxError` in the guest and
   `JsModuleException(Phase.Link)` for the host. Examples are a missing or ambiguous export and a
   parse failure in a dependency. The provider enforces `MaxModules`. Exceeding it is a
   `Load`-phase failure.
5. **Evaluation and jobs.** `Evaluate` never blocks and never drains jobs. It returns the module's
   evaluation promise, which a later `DrainJobs` settles. `Status` reaches `Evaluated` only after
   that promise fulfils. A host reads the result after draining jobs, not from task completion. An
   errored module keeps its error: every later `Evaluate`, static import or `import()` rejects with
   the same `EvaluationError` value. A module whose evaluation was never reached, because an earlier
   module of the walk threw, stays `Linked` and runs if something evaluates it later. Before the
   drain, a module whose evaluation has started but not settled may report `Evaluating` or
   `EvaluatingAsync` (the Broiler.JS adapter reports `Evaluating` for a module that is not
   awaiting); the contract fixes only that neither reports `Evaluated` before its promise fulfils.
6. **Namespace.** `GetNamespace` requires `Linked` or later. It returns the engine's namespace
   exotic object, which has sorted keys, `@@toStringTag` set to `"Module"`, and no extensibility,
   and reads live bindings. `import()`, static namespace imports and every host call all see the
   same object.
7. **Dynamic import (I12).** `import(specifier, options)` creates a `Dynamic` request. The referrer
   is the calling module's key, or `Script(label)` with the label of the classic or host script. Eval
   code and a function the `Function` constructor made use the module or script that created them
   (`GetActiveScriptOrModule`); a provider that cannot name that one rejects the import with
   `TypeError` rather than send another referrer. The call:
   - returns a realm promise right away;
   - resolves through the same host `Resolve` and the same key cache;
   - loads and links the target graph, then evaluates it;
   - settles through the job queue.

   A realm that has no map, or whose options disable dynamic import, rejects with `TypeError`. An
   import from a script with `AllowImportFromScripts=false` also rejects with `TypeError`.
   Resolution and load failures reject with `TypeError`. Link failures reject with `SyntaxError`.
   Evaluation failures reject with the thrown value.
8. **Policy is separate from source authorization.** Module loading never checks or uses GuestEval.
   `AllowGuestEval=false` does not block `import`, and a module never compiles through the eval
   path or its single-use permit (J04). The host applies its content policy in `Resolve` or
   `LoadAsync` by returning `Refused`, and uses `JsModuleRequest.Kind` to tell static imports from
   dynamic ones. Guest `eval` inside a module is still governed by GuestEval. A host that wants
   the bridge's current VM behavior, where a CSP that forbids `eval` also refuses `import()`,
   returns `Refused` for `Dynamic` requests from its own `Resolve`.
9. **Source labels.** `JsModuleSource.Label` defaults to the key. Parse errors, `JsModuleException`
   and, where the engine supports it, script stacks use the label. A label never affects identity
   or permission. What line and column information is guaranteed is J17's decision.
10. **Attributes.** Attributes are passed to the host. A provider rejects any attribute key it does
    not support. In the first version it supports none, so `with { type: "json" }` fails with
    `UnsupportedAttributes` (see Deferred).
11. **Threading.** `QueueRealmTask` is the only operation that can be called from any thread. All
    other rules follow the [realm lifetime rules](jseal.md#realm-operations-and-behavior). A host
    must not block the realm's thread waiting for a module task or promise, because the load that
    would release it runs on that same thread.
12. **Reentrancy.** `IJsModuleHost.Resolve`, and the synchronous part of `LoadAsync` up to its
    first incomplete await, run on the realm's thread, often while guest code or a link step is on
    the stack. VM declares its `ResolveCapability` non-reentrant and caller-thread for the same
    reason. During those calls the host must not call any operation of the realm, the map or an
    `IJsModule`: no `map.LoadAsync`, `TryGetModule`, `Evaluate`, `GetNamespace`, `DrainJobs`, host
    or classic script, or property access. A provider detects such a call and throws
    `InvalidOperationException` to the host code that made it. It does not recurse into the engine.
    - The request being answered then fails. For the host it is a `JsModuleException` in the
      `Resolve` or `Load` phase, with the `InvalidOperationException` as the inner exception. The
      guest sees `TypeError`.
    - Any other exception thrown by `Resolve` or `LoadAsync`, or a faulted `ValueTask`, is treated
      the same way. A CLR exception never reaches the guest as a value.
    - `QueueRealmTask` may be called from inside `Resolve` or `LoadAsync`. It never runs the task
      inline. Work that needs the realm, such as starting another `map.LoadAsync`, belongs in such
      a task.
    - After the failure, the realm and the map stay usable. The next import calls the host again
      (rule 1).
13. **Disposal.**
    - Disposing the map cancels outstanding loads. Pending dynamic-import promises and pending
      `Evaluate` promises reject with `TypeError` through the job queue, and every later `import()`
      rejects the same way. Pending host `LoadAsync` calls fault with `ObjectDisposedException`.
    - Disposing the realm disposes its map. It runs no queued work and settles nothing, which
      matches the existing rule for retained promise settlers.
    - Tasks the provider has already handed to `QueueRealmTask` do nothing if they run after
      disposal. A late load completion does not throw.
    - After disposal, only `Key` and `SourceLabel` can be read from an `IJsModule`. Every other
      member throws `ObjectDisposedException`.

## Walkthroughs

Each case assumes an in-memory `IJsModuleHost`:

- `Resolve` maps `./name` to the key `mem:/name`.
- `LoadAsync` returns text from a dictionary.
- `QueueRealmTask` adds to a queue. The test runs that queue, alternating with `DrainJobs`, until
  both are empty (`RunUntilIdle`).

**Two-module graph.** `main` imports `{ x, bump }` from `./dep`. Then `ns.x` is read after `bump()`.

```csharp
var map = modules.OpenModuleMap(host, new JsModuleOptions());
var load = map.LoadAsync("./main", JsModuleReferrer.Host(null));
RunUntilIdle();                                  // Resolve x2, LoadAsync x2, link
var main = await load;                           // Status == Linked
var done = main.Evaluate(); RunUntilIdle();      // done fulfilled; Status == Evaluated
var ns = map.TryGetModule(new("mem:/dep"), out var dep) ? dep.GetNamespace() : default;
// realm.GetProperty(ns, "x") answers 2 after bump(); OwnPropertyNames(ns) == ["bump", "x"]
```

**Cycle with live bindings.** `a` imports from `b`, and `b` imports `a`. `b` reads `a` before
`a`'s body runs, so `b`'s body sees a `ReferenceError` (TDZ). A function exported by `b` that
reads `a` later returns `"a1"`. Both modules end `Evaluated`. `LoadAsync` is called once per key,
and the cycle ends because of the key cache. No budget limit is involved.

**Rejected load.** `main` imports `./missing`. The host returns `JsModuleSource.Failed(NotFound)`,
so `map.LoadAsync` throws `JsModuleException(Phase.Load, Key: "mem:/missing")` and no module is
evaluated. From a module, `await import('./missing')` rejects with `TypeError`. Because failures
are not cached, a second `import()` calls `LoadAsync` again, and succeeds if the host now has the
source. If `Resolve` returns `Refused`, the result is `Phase.Resolve` and `LoadAsync` is never
called.

**Top-level await.** `dep` runs `await Promise.resolve(); v = "after"`. Right after `main.Evaluate()`,
the returned promise is pending and `dep.Status` is `EvaluatingAsync`. After `RunUntilIdle()`,
`main` has read `"after"` and both modules are `Evaluated`. A test that checks status without
draining jobs must see a pending state, not success. If `dep` rejects, `main`'s promise rejects
with the same value and both modules are `Errored`.

**Repeated import.** A static namespace import and `import('./dep')`, awaited, return the same
object. So do two concurrent `import('./dep')` calls started in the same turn. `LoadAsync` is
called once and the module body runs once. If `dep` threw during evaluation, both the first and
later `import()` calls reject with the same error object. A provider that resolves the second
call fails this case, as the current VM does.

**Disposal.** A dynamic import is waiting on a host `LoadAsync` that has not completed. Disposing
the map fires the load's token, and the guest promise rejects with `TypeError` on the next drain.
If the host completes the load anyway, its queued task does nothing. Disposing the realm instead
settles nothing and runs no jobs. `main.Key` stays readable and `main.GetNamespace()` throws
`ObjectDisposedException`.

## Capability promises and witnesses

| Flag | Promises | Witness (supplied by) |
|---|---|---|
| `Modules` | The realm is `IJsModules`; one map per realm; host-only resolution; canonical key identity; the engine links; live bindings; a namespace exotic object; cycles with a cross-module TDZ; top-level await settled only through jobs; errors cached; labels in failures; independent of GuestEval; the disposal rules above | The six walkthroughs as shared provider theories (I10 for JS, I11 for VM), mapped in `CapabilityCoverage` and removed from `NotExpressibleThroughTheContracts` (I13) |
| `DynamicImport` | Requires `Modules`; `import()` from modules, and from scripts when allowed, goes through the map; the same namespace as static import; shared concurrent loads; async rejection for every failure; works in realms without GuestEval; disposal rejects or does nothing as above | I12's nested, concurrent, restricted-eval and disposed-realm cases, then I13's resolver-reentry (rule 12) and teardown (rule 13) cases |

A realm without `Modules` must not implement `IJsModules`. A realm with `DynamicImport` but
without `Modules` is invalid, and a test asserts that combination never occurs.

**Gating until I13.** Each provider gets an `internal` option (for example
`BroilerJsEngineProvider.EnableModuleContract` and its VM counterpart), reachable only from the
test assemblies through the existing `InternalsVisibleTo` entries. A provider instance with the
option set creates realms that implement `IJsModules` and report `Modules` (and `DynamicImport`
when supported) in their own `Capabilities`. So the rule above holds in tests as well. The instance
registered by the module initializer never sets the option. I13 removes the option when it
publishes the flags. Whether an absent `DynamicImport` flag means `import()` rejects in every realm
is also asserted then.

The resolver-reentry witness is one shared theory. It uses a host whose `Resolve` (and, in a second
case, the synchronous part of `LoadAsync`) calls each prohibited operation in turn. It asserts four
things: the host sees `InvalidOperationException`, `map.LoadAsync` fails with the matching phase, a
guest `import()` rejects with `TypeError`, and a following import with a well-behaved host
succeeds in the same realm.

## Deferred

These are outside this design:

- JSON and CSS modules, and every import attribute.
- Populating `import.meta`. A per-module `import.meta` object is required. Filling in `url` or any
  other property is left to a later host hook.
- `import.meta.resolve`, source-phase imports and deferred imports.
- Import maps and every URL or network rule, which are host policy.
- Unloading modules from a live map.
- Sharing module records across realms.
- CommonJS interoperability.
- Worker module realms (I18).
- A Test262 module conformance claim.

## Follow-up slices

- **I10: contract types and the Broiler.JS adapter.**
  - Work: add the contract types above, and shared tests gated by the internal option described
    under "Gating until I13". Adapt `JSModuleContext` by subclassing it:
    - Create it with the realm's `JobPump` and `enableClrIntegration: false`.
    - Drive `LoadModuleAsync` instead of the `Task.Run` entry points.
    - **Override `LoadModuleAsync`** so that every specifier, including `module`, `clr` and text
      equal to an existing key, goes to `IJsModuleHost.Resolve` before any cache lookup. The
      override then looks up the returned key, never the raw specifier.
    - Remove `assert` and `global` from the global object after construction, so a module-capable
      realm has the same global names as a classic realm.
    - Only `CreateRealm` returns an `IJsModules` realm. `TryAdopt` never does.
  - Accept: the two-module, missing-import, namespace-identity and one-evaluation cases pass.
    `import 'module'`, `import 'clr'` and a specifier spelled like an existing key each call the
    host's `Resolve`, and fail when the host refuses them. The set of global own-property names is
    identical for a module-capable realm and a classic realm from the same provider. An adopted
    `JSModuleContext` does not implement `IJsModules`.
  - Blocker: the live-binding, namespace, cycle TDZ, top-level-await ordering and errored-module
    cases **require the upstream Broiler.JS slice below** plus a pin update through J19. They stay
    expected failures and are listed as such, not skipped.
- **Upstream Broiler.JS: module semantics and host-controlled resolution.** This is Broiler.JS
  repository work, and JSeal takes it through a J19 pin update.
  - Work: live import bindings, the module namespace exotic object (sorted keys,
    `@@toStringTag` `"Module"`, not extensible), a cross-module TDZ for cycles, top-level-await
    dependency ordering, and caching of evaluation errors. Also let a subclass make every
    specifier reach `Resolve` first, for example with an option that skips the raw-specifier cache
    lookup and the `module`/`clr` pre-registration.
  - Accept: the probe graphs in the table above give Node's answers through a `JSModuleContext`
    subclass with in-memory `Resolve` and `ReadModuleSourceAsync`. With the option set, no
    specifier is satisfied without calling `Resolve`. The `ModuleImportImmutabilityTests` gap note
    is removed.
  - Until then, I10's `LoadModuleAsync` override is the only way to meet rule 2.
- **VM host operation (VM ledger JSW-8/JSP-10), a prerequisite of I11.** *(Done upstream on
  2026-09-22: `JsHostRealm.LoadModule`/`EvaluateModule` and `TryGetModuleState`, VM decision record
  JSD-0024 sections 15 and 20. It is unreleased, so JSeal's adoption of it is prepared rather than
  merged; the I11 and I12 status paragraphs in [the integration plan](roadmap.integration.md) carry
  the current state.)*
  - Work: add a public `JsHostRealm` operation that runs a resolved in-memory graph (the key, text
    and resolution of each `JsModuleUnit`) inside an existing realm and returns the evaluation
    promise and the namespace.
  - Accept: the same keys give the same instances across calls and across `import()`. The
    operation never takes the classic-script path.
- **VM deferred module answers, a prerequisite of VM `DynamicImport`.**
  - Work: let the source provider answer a module request **later**, through a host completion
    applied in a realm turn, instead of only synchronously inside `Answer`.
  - Accept: a load that completes asynchronously settles `import()` through the job queue. A
    disposed realm neither runs guest code nor settles anything.
  - Until this exists, the VM adapter cannot honour an asynchronous `LoadAsync` for `import()`,
    and does not advertise `DynamicImport`.
- **I11: the VM adapter.**
  - Work: register `ResolveCapability` and answer each ruling from the map's cached resolutions.
    Recognize `JsFormat.TryReadModuleRequest` in `VmSourceProvider` before any GuestEval check.
  - Work, failure answers: the VM engine turns a provider `Refused` answer
    (`VmReason.ProviderRefused`, mapped in `VmArtifactLoadMediator`) into a guest `SyntaxError`,
    and `NotFound` or a thrown provider fault into `TypeError` (`JsEngine.cs`, the dynamic-import
    load path). The adapter therefore answers `NotFound` for every host `Resolve` or `LoadAsync`
    failure, including `Refused`, and for a rule-12 violation. It answers `Refused` only when the
    front end rejects the module source. This is the convention the bridge's `VmSourceProvider`
    already uses. The host still sees the precise `JsModuleFailure` through `JsModuleException`.
  - Accept: I10's cases pass unchanged. A realm with `AllowGuestEval=false` can import. A module
    payload never reaches the classic-script compiler. Host `Resolve` and `LoadAsync` failures of
    every kind reach the guest as `TypeError`, and parse or link failures reach it as
    `SyntaxError`.
- **VM fix: errored module re-import.** Accept: a second `import()` of a module whose evaluation
  threw rejects with the identical thrown value, and the body does not run again.
- **I13 additions from this design.** Work: the resolver-reentry theory (rule 12) and the teardown
  cases (rule 13) on both providers, and the removal of the internal gating option. Accept: both
  pass on every provider that reports `Modules`.
- **Broiler.HtmlBridge migration (bridge repository), after I10 and the upstream Broiler.JS
  slice.**
  - Work: move `BridgeModuleContext`'s two overrides into an `IJsModuleHost`. Its `Resolve` uses
    the bridge's URL resolver and its `LoadAsync` uses the CSP-checked fetch. `ScriptEngine` then
    creates its realm with `CreateRealm`, opens a map and evaluates module roots through it,
    instead of constructing a `JSModuleContext` and adopting it.
  - Accept: the bridge's module tests pass through the contract, and the bridge no longer sets
    `ModuleSupport`.
- **VM fix: module top-level destructuring.** Accept: two top-level destructuring `const`
  declarations compile under the module goal, and a real duplicate is still refused and named.

## I10 implementation status

Implemented 2026-09-21 on JSeal `37d9825` plus the merged wave-1 work, against the pinned Broiler.JS
`0.1.0-preview.1` packages. No package pin changed, and no provider advertises Modules or
DynamicImport.

**Contract types.** The types in [Shape and discovery](#shape-and-discovery) are in
[`Broiler.JSeal/Modules`](../Broiler.JSeal/Modules/IJsModules.cs), and the contracts project still has
no references. The design left three details open:

- `JsModuleOptions` defaults to `AllowDynamicImport = true`, `AllowImportFromScripts = true` and
  `MaxModules = 10,000`.
- `JsModuleKey` rejects an empty value or U+0000 when it is constructed.
- `JsModuleException` carries no `JsModuleFailure`. Its message names the failure, and the host
  already knows its own `Resolve` and `LoadAsync` answers.

**Gating.** [Gating until I13](#capability-promises-and-witnesses) proposed that a gated realm report
Modules. It does not. The Broiler.JS adapter cannot keep the whole Modules promise on the pinned
engine, so a realm from `new BroilerJsEngineProvider { EnableModuleContract = true }` implements
`IJsModules` and reports the same flags as any other realm from the provider. The rule "a realm
without Modules must not implement `IJsModules`" therefore holds only outside that internal gate.
I13 decides the flags. Registered and adopted realms never implement `IJsModules`.

**How the Broiler.JS adapter works.** [BroilerJsModuleContext](../Broiler.JSeal.BroilerJs/BroilerJsRealm.Modules.cs)
subclasses `JSModuleContext`:

- It is built with the realm's job pump and `enableClrIntegration: false`. The adapter removes
  `assert` and `global`, so a module realm has the same global names as a classic realm.
- Its `LoadModuleAsync` override answers each request with the module the map already loaded for
  it. No specifier, including `module`, `clr` or text spelled like a key, reaches the engine's cache.

[The map](../Broiler.JSeal.BroilerJs/BroilerJsModuleMap.cs) handles loading:

- It resolves every specifier through the host and applies every load in a `QueueRealmTask` task.
- It compiles each text with the engine's own compiler before anything runs. A parse error is a
  `Parse`-phase `JsModuleException` whose inner exception comes from the shared `JsEngineException`
  boundary, with the module's label.
- It parses each text again with the engine's parser to read the module's static requests. This is
  not linking: the engine binds imported names itself, when evaluation reaches each declaration.
- It enforces `MaxModules` when a new module would be added, before the host is asked to load it, so
  a graph that keeps requesting new modules fails the `Load` phase instead of loading without end.

**Module code is strict.** The pinned engine compiles module text as the sloppy body of a function
and has no strictness option. The map therefore prepends `"use strict";` to the text it hands the
engine, on the first line or on the line after a hashbang, which is how this provider already forces
strictness on host source. An assignment to an undeclared name throws `ReferenceError`, `this` in a
plain function call is `undefined`, and `with` or a legacy octal literal fails the `Parse` phase. Line
numbers are unchanged. Engine-reported columns on the directive's line, in parse messages and in
guest stack traces, are 13 characters too high.

Evaluation is the engine's. The adapter shares each module's evaluation task, so a module body runs
once, a later importer receives the same cached error, and an importer waits for an asynchronous
dependency. Evaluation settles only through `DrainJobs`. A module realm runs its jobs as tasks on a
realm scheduler, because the engine's `new JSPromise(Task)` settles through `ContinueWith` on
`TaskScheduler.Current`. Outside such a task, that continuation would run on the thread pool.

The module parse goal is internal in the pinned package (`CoreScript.AllowTopLevelAwaitScope` and
`ModuleGoalScope`), so the adapter reaches it by reflection. If either member is missing,
`OpenModuleMap` throws `NotSupportedException` instead of parsing with the script goal.

**Refusals.** The adapter refuses a graph at link time when it can see from the text alone that the
pinned engine would run it wrongly. The graph fails with a `Link`-phase `JsModuleException` that
names the reason and is never evaluated. With the refusals disabled, the shared cases gave the wrong
answers shown here:

| Refused | Why | Wrong answer without the refusal |
|---|---|---|
| An exported `let`, `var`, function or class binding the module ever reassigns, or that a direct `eval` could reassign | Imports are copies, not live bindings | `1:1` instead of `1:2` |
| A cyclic static graph | No cross-module TDZ or live bindings | `no TDZ` instead of `ReferenceError` |
| An asynchronous dependency (top-level await anywhere below it) that is not the module's last static request | The engine waits for it before starting the next import | `slow:start,slow:end,quick,main` instead of `slow:start,quick,slow:end,main` |
| A static `import` or `export ... from` after any other statement | The engine evaluates a dependency where its declaration appears | Not probed; follows from the compiler |
| An `export { name }` list before the declaration of `name` (a function declaration excepted) | The list copies the binding when it runs | Not probed; follows from the compiler |
| Any `import()` | I12 routes it through the map | Not applicable |
| An `export { name }` list whose `name` is no module-level declaration the analysis can see | Neither check above could run on it | Not observed; the engine rejects an undeclared name |
| A reference to `module`, `exports`, `require`, `__dirname` or `__fileame`, anywhere, including a same-named parameter | The engine passes module code these CommonJS parameters | `typeof module` is `object`; `module.exports = {...}` replaces the namespace, and `exports.name = ...` extends it |
| Any direct `eval` | It could reach those parameters | As above |
| `this` or `arguments` outside a non-arrow function or class body | Module code runs as a function whose `this` is the engine's module object | `typeof this` and `typeof arguments` are `object` instead of `undefined` |
| A top-level `var` (at any block depth) or function declaration whose name another module in the map also declares that way, or reads without declaring it, or that the global object already has | The engine binds these declarations on the realm's global object | `99:dep,dep` instead of `1:dep,main` for two modules that each declare `count` and `helper` |

The checks are syntactic and conservative. They walk every field of every syntax node by
reflection, because the engine's `AstReduce` skips variable initializers, object literal members,
parameter defaults and switch cases. A `var` nested in a block, loop, `switch` or `try` counts as a
module-level declaration; before the adversarial review it did not, and
`if (true) { var x = 1; } export { x }` with a later `x = 2` gave `1:1` unrefused. Only a
non-computed member property, a non-computed property key that is not shorthand, a label and
`import.meta` or `new.target` are skipped as names that are never references.

The cross-module check compares a module's top-level `var` and function names with the other
modules' and with the names they read without declaring anywhere. A module that declares a name in a
nested scope and also reads the same name free elsewhere is not caught. A later classic script, or a
host write, that creates a global property of the same name is not checked either.

The probe table under [Broiler.JS](#broilerjs-broilerjavascriptmodules-010-preview1) recorded
`before` for an importer after top-level await. Through the adapter, which shares each module's
evaluation task, the importer sees `after`, and the shared case passes. Only a later sibling import
is misordered, and that is refused.

The adapter also fails explicitly in these cases:

- `GetNamespace` before evaluation starts throws `InvalidOperationException`, because the engine
  creates the exports object when evaluation starts.
- `require()` is refused at link time with the other CommonJS names. The engine's own request for
  it rejects with `TypeError`, which the refusal leaves unreachable.
- An import attribute fails the `Resolve` phase as `UnsupportedAttributes`.
- A request that is not one of the module's static requests rejects with `TypeError`.

**Gaps that are reported, not refused.** The shared tests list these as expected failures for
Broiler.JS:

- The namespace is the engine's ordinary exports object. It has no `@@toStringTag`, its keys are in
  source order, and it is extensible and writable: `ns.x = 99` and `ns.extra = 1` succeed in strict
  code, where ECMAScript throws `TypeError`, and later reads of the namespace see the written values.
- A missing or ambiguous export reads `undefined` instead of failing to link.
- The engine's parser rejects `import 'specifier';`, so such a module fails the `Parse` phase.
- A module's top-level `var` and function declarations are properties of the realm's global object,
  visible as `globalThis.name` and to classic scripts. The refusal above covers only the name
  collisions it can see.

These are recorded as refused gaps in the shared cases, so they fail rather than pass:
`ModulesDoNotShareTopLevelVarBindings`, `ModuleCodeHasNoCommonJsBindings` and
`TopLevelThisIsUndefinedAndArgumentsIsUnbound`. `ModuleCodeIsStrict` passes.

Other deviations, not yet covered by a shared case:

- `export function f() {}` followed by `export { f as g }` fails to compile.
- `export default function f() {}` followed by another `export` fails to parse
  (`Unexpected token Identifier: export`).
- An anonymous default export is not named `default`. For `export default function () {}`,
  `export default class {}` and `export default (function () {})`, the function's `name` is the
  importer's local name, such as `g` for `import g from`, instead of `default`.
- An HTML-like comment (`<!--`) is accepted in module text instead of being a `SyntaxError`.
  A text-level refusal would also refuse strings that contain them, so this is reported, not refused.
- `import.meta` has no `url`; populating it is deferred, as designed.

**Tests.** [JsealConformanceTests.Modules.cs](../Broiler.JSeal.Tests/JsealConformanceTests.Modules.cs)
holds the provider-neutral cases for I11 to reuse unchanged:

- Every (engine, case) pair runs either as a passing case or as a recorded gap.
- A gap row requires the case to fail, either by the adapter's refusal or by the contract assertion
  that catches the wrong answer. A gap that starts passing fails the suite.
- Broiler.VM has an engine-level gap until I11.
- The I12 dynamic-import cases in
  [JsealConformanceTests.DynamicImport.cs](../Broiler.JSeal.Tests/JsealConformanceTests.DynamicImport.cs)
  (the same namespace as a static import, shared concurrent loads, every failure kind, a graph
  whose earlier sibling throws, nested imports, `import()` in eval and `Function` code, a
  restricted-eval realm, the map's options with the script's label as referrer, and disposal of the
  map and of the realm) are recorded gaps for Broiler.JS: the adapter refuses a module containing
  `import()`, and a script's `import()` meets the engine's own loader.
- `AnEvaluationErrorStopsTheWalkBeforeALaterSibling` passes on Broiler.JS: a sibling after a
  thrower does not run and stays `Linked`. `AThrowInACycleLeavesAnUnreachedDependencyLinked` is a
  refused gap there, with the other cyclic graphs.

[BroilerJsModuleAdapterTests.cs](../Broiler.JSeal.Tests/BroilerJsModuleAdapterTests.cs) holds the
adapter's own refusal and near-miss cases.

**Upstream Broiler.JS additions from I10.** These join the upstream slice above:

- A public module-goal parse entry point.
- The side-effect import grammar.
- The export-list lookup of an exported function declaration.
- Not exposing the CommonJS parameters, or a module object as `this`, to ES module code.
- Compiling module code strict, so the adapter's `"use strict";` prefix and its column shift can go.
- A module environment for top-level `var` and function declarations instead of the global object.
- `export default function f() {}` followed by further exports, and `default` as the name of an
  anonymous default export.
- A non-writable namespace, which the namespace exotic object item already covers.
- HTML-like comments as a `SyntaxError` in module code.

## Owner decisions pending

- **Proposed, owner decision pending: the future of `BroilerJsEngineProvider.ModuleSupport`.**
  Recommendation: at I13, derive both flags from a working `IJsModules` implementation and keep the
  callback only to narrow them further. A callback that returns true would then no longer be enough
  to advertise either flag, and adopted realms would never advertise them.
  - Impact on Broiler.HtmlBridge: its `JsEngineHosting` sets the callback, and the realms it adopts
    from `BridgeModuleContext` would lose `Modules` and `DynamicImport`. The bridge's own module
    path is not affected, because it gates on `EngineModuleSupport.Available` and a search of its
    `src/` found no reader of the two flags. Its assignment would become a no-op for adopted realms.
  - This changes public behavior. To decide, the owner must say:
    1. whether any consumer other than the bridge is known to read the flags on an adopted realm;
    2. whether to accept the change at I13 with a release note, or to require an `[Obsolete]`
       period on `ModuleSupport` first, during which the callback keeps applying to adopted realms
       only, documented as "host-asserted, not witnessed by JSEAL";
    3. whether the bridge migration slice above is scheduled before that release or after it.
- **Resolved on 2026-09-22: both VM host additions were implemented upstream** (JSD-0024 sections
  15, 16 and 20, in the Broiler.VM working tree, unreleased), so I11 and I12 are no longer blocked
  for want of a VM operation. What they wait for now is a Broiler.VM release and a pin update: the
  adoption is prepared against a local candidate and is not part of this repository's merged state.
