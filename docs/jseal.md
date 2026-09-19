# JSEAL — the JavaScript Engine Abstraction Layer

JSEAL is the seam between the HTML bridge and a JavaScript engine. The bridge binds a document
against JSEAL's contracts; a **provider** implements those contracts over one engine; and which
provider serves a page is a registration, not a compile-time fact about the bridge.

This document says what the contracts are, why they have the shape they do, how to add an engine, and
— the part most worth reading before planning work — exactly how much of the bridge has actually
moved and what is still in the way.

## The problem it exists to solve

The browser already had an engine abstraction before this one: `IScriptEngine`, in
`src/Broiler.HtmlBridge.Scripting`. It is a good interface and it is at the wrong altitude.

`IScriptEngine` abstracts *running a list of scripts*. That is enough for the document-free entry
points, and `VmScriptEngine` really does serve those on the Broiler.VM JavaScript profile. It is not
enough for a page, because the moment a document is involved the engine reappears in the signature:

| Where | What leaks |
|---|---|
| [`IDomBridgeRuntime.cs:50,52`](../src/Broiler.HtmlBridge.Core/Dom/IDomBridgeRuntime.cs) | `Attach(JSContext, …)` — the "engine-neutral" core assembly takes a Broiler.JS type |
| [`InteractiveSession.cs`](../src/Broiler.HtmlBridge.Scripting/InteractiveSession.cs) | `internal InteractiveSession(JSContext …)` — the only session type could not be built from outside Broiler.JS. It takes an `IDisposable` engine lifetime now and no longer names the engine; the constructor is still `internal`, and `Broiler.HtmlBridge.Scripting.Vm` is not among that assembly's friends |
| `src/Broiler.HtmlBridge.Dom` | 891 `Broiler.JavaScript` references across 250 files when this was written — the DOM named Broiler.JS's object types directly. See [where the migration actually stands](#where-the-migration-actually-stands) for today's count |

So `RenderingPipeline` can be handed any `IScriptEngine` it likes and a page still runs on Broiler.JS,
because the only method it calls — `ExecuteInteractive` — has to attach a bridge, and
`IDomBridgeRuntime.Attach` takes a `JSContext`.
Broiler.Browser's
[`docs/vm-javascript-profile.md`](https://github.com/Broiler-Platform/Broiler.Browser/blob/main/docs/vm-javascript-profile.md)
states this plainly and lists it as the reason no page load runs on the VM. That document stayed
with the browser when this component was extracted, because what it describes is an *embedder*
choosing an engine; the seam it names is here.

**A realm is the right altitude.** A realm is what a document has. JSEAL's central type is therefore
`IJsRealm`, and the end state of the migration is `Attach(IJsRealm, …)`.

## Layering

```
                    ┌─────────────────────────────────────────┐
                    │  Broiler.Browser.Core / RenderingPipeline│
                    └───────────────────┬─────────────────────┘
                                        │
                    ┌───────────────────▼─────────────────────┐
                    │  Broiler.HtmlBridge.Scripting            │   owns engine selection;
                    │    ScriptEngine, InteractiveSession      │   registers the providers
                    │    JsEngineHosting                       │   this build linked
                    └───────────────────┬─────────────────────┘
                                        │
                    ┌───────────────────▼─────────────────────┐
                    │  Broiler.HtmlBridge.Dom                  │   the DOM bindings —
                    │    DomBridge + ~130 feature bindings     │   the "base component"
                    └───────────────────┬─────────────────────┘
                                        │  binds the document against
                    ┌───────────────────▼─────────────────────┐
                    │  Broiler.JSeal                 ◄─ JSEAL │   ZERO ProjectReferences
                    │    JsValue JsCall IJsRealm              │   ZERO PackageReferences
                    │    JsCapabilities JsEngineRegistry      │
                    └───────────────────┬─────────────────────┘
                                        │  implemented by
              ┌─────────────────────────┴─────────────────────────┐
              │                                                   │
  ┌───────────▼──────────────┐                    ┌───────────────▼──────────────┐
  │ Broiler.JSeal.BroilerJs  │                    │ Broiler.JSeal.Vm             │
  │   over Broiler.JavaScript│                    │   over Broiler.VM's profile  │
  │   the default engine     │                    │   -VM builds only, see below │
  └──────────────────────────┘                    └──────────────────────────────┘
```

**`Broiler.JSeal` has no `ProjectReference` and no `PackageReference`, and that is the
whole neutrality claim.** Everywhere else in the platform engine neutrality is asserted by
grepping for a namespace. Here it is asserted by the compiler: an assembly that references nothing
cannot name a `Broiler.JavaScript` type, a `Broiler.VM` type, or anything either drags in. Adding a
reference is a diff a reviewer sees, and `scripts/check-engine-neutrality.sh` fails CI if the project
file ever declares either kind of reference. It inspects those two elements and nothing else; the file
already carries a `PropertyGroup` and `InternalsVisibleTo` items.

The corollary is that JSEAL cannot reach `Broiler.HtmlBridge.Core` either — no `ContentSecurityPolicy`,
no `MicroTaskQueue`, no `RenderLogger`. That costs less than it looks: what JSEAL needs from those is
a *capability*, not a dependency.

## The value model

`JsValue` is a `readonly struct` of three fields — a kind, a `double`, and an `object?` reference.
24 bytes.

**Why not an interface hierarchy.** The obvious design is `IJsValue` with `IJsObject`/`IJsFunction`
beneath it, implemented by provider types deriving from the engine's own. For objects that is exactly
what happens. It cannot work for primitives: Broiler.JS's `JSNumber` is `sealed`, so a provider cannot
derive from it, and an interface would force a carrier allocation per number on a path that already
runs per property read.

**Why this layout in particular.** Tag + double + reference is what Broiler.VM's own `JsValue`
([`JsValue.cs:83`](../Broiler.VM/src/Broiler.VM.Profile.JavaScript/JsValue.cs), `internal readonly
struct`) chose, for the reason it records: the collector is the CLR's, so a value that must sometimes
hold a managed reference cannot be a NaN-boxed word. Matching it means the Broiler.VM provider
re-tags rather than converts. (This said "a future Broiler.VM provider".)

**`Missing` is kind zero.** Broiler.JS's `Arguments` indexer returns a CLR `null` — not `undefined` —
past the end, and the bridge's argument reads (598 on 2026-09-08) depend on telling those apart
before coercing:
`scrollTo()` and `scrollTo(undefined)` are different calls. Broiler.VM's `JsType.Empty` is zero for
the same reason.

**The reference is the engine's own value, not a wrapper.** Under the Broiler.JS provider an object
handle carries the `JSObject` the engine already has. That is what keeps wrapper identity working:
`el === el`, and the seven `ConditionalWeakTable<object, …>` the bridge keys on
`JsValue.ObjectIdentity`, one of them `JsObjectRegistry`'s reverse map, all keep asking the question
they always asked. (This said "across a half-migrated bridge"; the bridge holds handles throughout.)
A provider whose engine hands out pointers or stack slots is expected to canonicalise handles itself.

**`==` is `===`; `Equals` is reflexive.** The two deliberately differ on NaN alone — the same split
`System.Double` makes, and for the same reason: the bridge stores JS values in `List<>`s (event
listeners, collection contents) and the BCL reaches `Equals`/`GetHashCode` when it searches them, so a
value that is not equal to itself could not be found in the collection it was put into.

**`JsValue.ToString()` never enters the engine.** This is not a small point. On Broiler.JS,
`JSValue.ToString()` on an object *runs the object's JavaScript `toString`* — so what reads like a
debug rendering is the observable ECMAScript coercion, and can execute page script, throw, or
re-enter the realm. The bridge has 343 such calls. JSEAL's renders `[object]` and the real coercion is
`IJsValues.ToJsString`, which is honest about entering the engine.

**`AsBoolean` decides every kind but BigInt, and `IJsValues.ToBoolean` is the member for that one.**
The handle's switch sends every kind above `String` to `true`, which is right for a symbol and for
every object and wrong for `0n`: `JsValueKind.BigInt` is 7 and `Object` is 8, so "every object is
truthy" never covered it. A BigInt handle is an opaque provider reference, so the contract assembly
has nothing to test. `ToBoolean` sits beside `ToJsString` and `ToNumber` for a different reason from
theirs — it runs no script, and is there because only a provider may look inside the value. Under
Broiler.JS a BigInt-kind handle may also carry that engine's decimal (`0m`), and the provider asks the
engine about both. Broiler.VM's profile has no BigInt at all, so its provider answers from the handle
and refuses a BigInt handle as foreign. The same opacity is why `==` compares two BigInt handles by
reference rather than by value: BigInt is the one kind for which "`==` is `===`" above does not hold.

## The realm

`IJsRealm` aggregates seven narrow contracts, the way `IScriptEngine` was split in this repository's
Phase 8. Every binding depends on the aggregate, so nothing at a call site gets longer; the split is
for the provider implementing them one at a time, and for the reviewer asking what an engine must be
able to do.

| Contract | What it covers |
|---|---|
| `IJsValues` | creating objects, arrays, methods, constructors, exotics; the two coercions that can run user code, and `ToBoolean`, which runs none but is the only truthiness test a BigInt can be given |
| `IJsMembers` | `DefineValue` / `DefineAccessor` / `DefineIndex`, reads and writes, own keys, prototype link |
| `IJsCalls` | `Invoke`, `Construct`, and raising an `Error` or a `DOMException` from host code |
| `IJsJobs` | the microtask queue, and promises the host can settle |
| `IJsSource` | evaluating **host** script, the page's **classic** scripts and **dynamic** source (`eval`, `new Function`) — separately, because the page's policy exempts the first and governs the other two by different directives |
| `IJsClone` | structured clone: `Clone` in one realm, and `Detach`/`Adopt` across two |
| `IJsExotic` | host-completed property lookup, for the six DOM objects whose members are not a fixed list |

Four shapes in there are load-bearing.

**The realm is on the call, not ambient.** `JsCall` carries its `IJsRealm`. A
`[ThreadStatic] Js.Current` would read better at every one of the ~900 callback sites and would be
wrong here: three threads run one page's JavaScript. `ScriptEngine` installs a synchronization context
before building a document's realm precisely because promise and generator continuations were resuming
on the thread pool; `BrowserEventLoop`'s queues are all `ConcurrentDictionary` for the same reason;
a Worker builds a second realm on a thread of its own. An ambient realm turns each of those into a null
reference at run time that no compiler can see — in code being migrated file by file, which is where a
missing realm is exactly the mistake a reviewer cannot spot.

*This hazard does not go away underneath.* Broiler.JS resolves realm intrinsics from a thread-static:
`new JSObject()` reads the current context's `Object.prototype`, and with no context current it mints
an object with a null prototype and **no error**. The provider pays for that with a scope on every
entry point, so the contract does not have to expose it.

**`NewMethod` and `NewConstructor` are different methods.** WebIDL says only interface objects are
constructors: `el.setAttribute.prototype` is `undefined` and `new el.setAttribute()` throws. On
Broiler.JS that is one boolean — `createPrototype: false` — which also makes the function
non-constructable, because `JSConstructorOperations.IsConstructor` tests
`prototype != null || IsConstructable`. The same boolean is a load-bearing memory fix: an element
wrapper's members were each minting an unreachable prototype object plus its `constructor`
back-reference, and dropping them was the difference between a WPT test fitting the memory budget and
being aborted (see [`DomFunction.cs`](../src/Broiler.HtmlBridge.Dom/DomBridge/DomFunction.cs)). Sixteen
interface objects a page may legitimately `new` use `NewConstructor`; everything else uses `NewMethod`.

**Ordinary properties beat exotic handlers.** `IJsExotic` is consulted only when the object's own
property storage found nothing. This is what WebIDL's named-property semantics require and what all
six existing subclasses do — each calls the base lookup first. Getting it backwards is silently wrong
rather than loudly wrong: a collection that happens to contain an element named `item` would start
shadowing its own `item()` method.

**A message crossing to a Worker is cloned twice, and the contract says which realm does which.**
`IJsClone` is three operations because a browser does three different things. Same-document messaging
(`window.postMessage`, a `MessagePort`) clones once, in one realm, and delivers the copy: `Clone`. A
message crossing to a Worker is cloned on the *sending* thread into a graph no script can reach
(`Detach`, which answers a `JsDetachedValue`), and again on the *receiving* thread into the receiving
realm (`Adopt`). Cloning once and handing the result over would put one realm's object graph in
another thread's hands; cloning once on the receiver, from the sender's live value, is worse, because
the sending script keeps running and can mutate that graph while the receiver walks it.

`JsDetachedValue` is a third kind of thing on purpose, and it is what the earlier attempts at this
were missing. A `JsValue` handle is only meaningful to the realm that minted it, and a clone's result
may be a *primitive*, for which a handle carries no engine instance at all — so a worker's inbox
cannot be a queue of handles. The carrier belongs to no realm, carries the engine that made it so
`Adopt` can refuse a graph from another engine, and exposes nothing: `Providers.JsProviderClone` is
the only way in or out, the way `Providers.JsProviderValue` is for a handle.

Putting the clone *on the realm* rather than in a free function taking two realms is the load-bearing
part. Broiler.JS's `structuredClone` mints its objects against the current context, which is
thread-static; a clone taken between two JSEAL calls would mint into whichever realm the thread last
touched — silently, and on exactly the path where the two realms are supposed to stop touching. A
provider enters its realm for the duration of a contract call, so making the clone a contract call is
what brings it inside that bracket.

The transfer list is split the same way it is owned. `ClassifyTransferable` answers in the
specification's vocabulary — is this a transferable object, and is it already detached — so the host
can walk the list itself and classify only what it does not recognise. That matters because a
`MessagePort` is transferable and is a thing the *bridge* owns; no engine knows what one is. Building
the `{ transfer: [...] }` options the clone actually reads stays in the provider, because that is the
clone's own signature.

**Source is split by the Content-Security-Policy decision that governs it, not by who wrote it.**
`IJsSource` has three members. `EvaluateHostScript` runs JavaScript the bridge authors — two
embedded `.js` assets totalling 1,891 lines, plus in-source evaluations that install polyfills,
probe for a global, or re-link a prototype. That source ships with this repository and is not
subject to the page's policy. `EvaluateClassicScript` runs a classic script the page carries — a
script element another script inserted, a sub-document's scripts, an event-handler attribute's
wrapper, a worker's top-level script and each `importScripts` body — and expects its caller to have
taken the `script-src` decision already (`script-src-attr`, for a handler), which every caller but
the worker path does today. The top-level document's own scripts never reach it: `ScriptEngine`
evaluates them on the context it built.
`EvaluateDynamicSource` is what `eval` and `new Function` ask for on the page's behalf;
`'unsafe-eval'` governs it, through `JsCapabilities.GuestEval`, and it is exactly what a CSP may
forbid. Nothing in the bridge calls it, though: the page's own `eval`, `new Function` and
`ShadowRealm.prototype.evaluate` never reach a host member, and each provider refuses them inside a
realm built without `GuestEval` (Broiler.VM has no `ShadowRealm`). A dedicated worker's realm is
built with the constructing page realm's `GuestEval` decision, so what `'unsafe-eval'` refuses the page
it refuses the worker. `ScriptEngine`'s document-free
`Execute(scripts)` and `ExecuteDetailed(scripts)` build no bridge, so they adopt the context they build
themselves, through the same `DomBridgeHostUtils.AdoptRealm` and the same policy mapping, from the policy a host
sets on `ScriptEngine.Csp`; a script there, and work it leaves to run after the call returns, meets the
same refusal: neither entry point disposes that realm, and disposing the context does not unsubscribe the
refusal, so it stays on the context for as long as anything can still run there. Work a document's script
leaves to run after the document's bridge is torn down is not guaranteed the refusal. A dynamic
`import()` is none of
the three members: `IJsSource` runs no modules. Where a page has
module roots and the engine binds imports, `BridgeModuleContext` checks each module it fetches with
`AllowsExternalScript` (`script-src-elem`, then `script-src`, then `default-src`); on the plain
context every other page runs on, `import()` rejects. Separating the members is what
lets an engine declare each on its own: `HostScriptSource` can be met by compiling the bridge's own
JavaScript when the engine is built, `ClassicScriptSource` needs a compiler over text nobody saw
then, and `GuestEval` is a permission a realm may be built without.

## Capabilities

`JsCapabilities` is declared, not discovered. The bridge currently *discovers* one — `EngineModuleSupport`
runs a real ES module and checks the binding, on a worker thread behind a five-second timeout, because
on an engine without the fix the probe **hangs** rather than failing. That is what discovery costs when
a contract could have said so.

A realm's capabilities may be **narrower** than its provider's but never wider: a page whose CSP forbids
evaluation gets a realm without `GuestEval` from an engine that has it.

Two of the flags are still declared without a contract behind them, and this is the honest state of
each:

* **`WorkerRealms`** is complete. Its two halves — "a second realm can be created on another thread"
  and "values moved between the two by structured clone" — are `IJsEngineProvider.CreateRealm` and
  `IJsClone`, and `JsealConformanceTests.ASecondRealmRunsOnASecondThread` exercises both by sending a
  message to a realm on a second thread and receiving a reply.
* **`Modules` and `DynamicImport`** are not. `IJsSource` runs a *script*, which is a different thing
  from instantiating and evaluating a module in a module map, so a provider can declare either and a
  host written against JSEAL alone has no way to use it. The suite records them in
  `NotExpressibleThroughTheContracts` rather than pretending otherwise. The contract that would close
  it is written out in the remarks on
  [`BridgeModuleContext`](../src/Broiler.HtmlBridge.Dom/BridgeModuleContext.cs): a host-implemented
  `IJsModuleLoader` (resolve a specifier against a referrer; load a source for a key) and a
  provider-implemented `IJsModules` (evaluate a module). It has two real implementers already —
  Broiler.JS's `JSModuleContext` overrides and Broiler.VM's `VmModuleMap`, which is *already* that
  shape because the VM embedding contract gives resolution and transport to the host outright. What
  blocks it is mechanical and named there: `ScriptEngine` constructs `BridgeModuleContext` and then
  uses it as a `JSContext`, and the Broiler.JS provider cannot reference
  `Broiler.JavaScript.Modules` without raising an `engineProjectRefs` budget the ratchet enforces even
  for a provider.

One flag decides whether an engine can host a DOM at all:

> **`ReentrantHostCalls`** — a host function may call back into JavaScript while the engine is inside a
> host call. An event listener, a promise reaction and a `toString` coercion are all exactly that. An
> engine without it can run a page's script; it cannot dispatch a `click`.

## Choosing an engine

`JsEngineRegistry` is a process-wide registry keyed by provider name. `BROILER_JS_ENGINE=<name>`
overrides the default for a run.

**What is still a build-time decision, and should be:** whether an engine's assemblies are *linked at
all*. That remains a `ProjectReference` under a configuration condition, which is what keeps `Debug`
free of Broiler.VM and keeps meaningful the duplicate-assembly pass Broiler.Browser runs over its
own graph (`scripts/check-component-graph.sh`, in that repository). The build decides which
providers are present; the registry decides among the ones that are.

Two things want more than one engine in a process, and a `#if` cannot give either: a conformance suite
that runs the same assertions against every registered provider, and a bisect asking whether a page
renders differently on the other engine — a question about a *run*, not a *build*.

## Adding an engine

1. A new project `src/Broiler.HtmlBridge.Jseal.<Engine>` referencing `Broiler.HtmlBridge.Jseal` and the
   engine.
2. `IJsRealm` over the engine's realm. Split it by capability, one file per contract — the Broiler.JS
   provider does, and the shape is worth copying.
3. `IJsEngineProvider` with a stable lower-case hyphenated `Name`, an honest `Capabilities`, and a
   `[ModuleInitializer]` that registers it.
4. Optionally `IJsRealmAdoption`, if the engine's realms can be host-constructed and then wrapped.
5. Add it to `eng/jseal-budget.json` — a provider is the one kind of project whose engine references
   are *supposed* to be high.
6. Run the conformance suite. It is data-driven over `JsEngineRegistry.All`, so a new provider adds no
   test code.

The trap to know about before starting: **the provider is where ambient engine state is paid for.**
Broiler.JS's is the thread-static current context described above. A provider that skipped it would
work in every test that happened to run right after an evaluation.

The second half of that trap is the reverse mistake, and this repository made it: a provider must not
install its *own* scheduling over a host's. `BroilerJsRealm` makes its job pump the thread's
synchronization context while a contract call runs, which is right for a realm it built — the
`JSContext` was constructed with that pump and `DrainJobs` empties it — and wrong for one it merely
adopted, because the host built that context with its own scheduling and drains that queue. Installing
the pump over an adopted context diverted every promise reaction created inside a JSEAL call into a
queue nothing in the process emptied: no error, no reaction, no way to see it from the outside. An
adopted realm now leaves the thread's context alone.

## Where the migration actually stands

`eng/jseal-budget.json` is the ratchet. Per project it records engine references, engine project
references, and `.Eval(` sites (`guestEvalSites`, whatever they run);
`scripts/check-engine-neutrality.sh` fails when a count *rises* — for a provider, only its engine
project references — and reports when one *falls* so the budget is lowered in the same commit. A CI
job runs it. This is what lets the remaining files migrate incrementally instead of in one
cliff-edge merge, and what stops in-flight feature work re-adding coupling behind the migration's
back.

`Broiler.HtmlBridge.Dom/Runtime/JsInterop.cs` is the seam between the migrated and unmigrated halves,
and it is scaffolding meant to be deleted. It is a cast, not a conversion — a JSEAL object handle
already carries the engine's `JSObject` — and every use of it is one place the migration has not
reached.

**Where it stands.** `Broiler.HtmlBridge.Dom`'s engine references have gone from **891 to 12**, and
its eval sites from 55 to 1. Every DOM feature binding, every registration hub, the node wrapper
factories, the event system, all six exotic objects, the worker/messaging surface, every forwarding
parameter and every engine argument frame are migrated. `eng/jseal-budget.json` carries the live
number; this paragraph will go stale and the budget file will not.

**Two contract gaps this document recorded are closed, and both were closed by adding to JSEAL rather
than by working around it in the bridge.**

`IJsExoticDelete` is the deletion half of a host-completed lookup. `Storage` was the only one of the
six lookup-completing objects whose behaviour includes taking something away, and converting it
without a delete hook would have left the ordinary property deleted and the item still in the store —
a wrong answer rather than a missing feature, which is why it was reported rather than worked around.
It is a second interface rather than a sixth member of `IJsExotic` because the two providers do
opposite things about it: Broiler.JS overrides a virtual its exotic object already overrides the
neighbours of and pays nothing, and Broiler.VM has no delete hook on its host-object surface at all
and puts a deleting handler behind the realm's own `Proxy`. A proxy costs a trap lookup on every
operation, so the declaration has to be askable at mint time — otherwise every live collection's
indexed reads would pay for a feature only `Storage` uses.

`IJsValues.NewArrayBuffer` and `IJsValues.TryGetArrayBufferBytes` are the binary-data members, and
`JsCapabilities.BinaryData` is the capability. `BlobBinding` specified them: three operations, not
one — mint, test, read — because `new Blob([buf])` has to tell a buffer from an object it must
stringify and no JS-visible property answers that. The test and the read landed as one member because
they are one question to an engine: a brand check is what produces the bytes.

**Both were served without a Broiler.VM change, by the same move.** The provider reaches an intrinsic
the guest already has — `Proxy` and `Reflect.deleteProperty` for the one, `ArrayBuffer`, `Uint8Array`
and `ArrayBuffer.prototype`'s own `byteLength` getter for the other — captured at realm creation for
the reason `Promise` is captured there, because every one of them is a writable global or a writable
prototype member. That is now three times this route has answered a gap that looked like it needed a
new host-surface member.

**The remaining 12 sit in seven files**, none holding more than three:

| What | Why it stays |
|---|---|
| `DomBridge/Lifecycle.cs`, `RegisterDocument(JSContext)` in `DomBridge/Registration/Registration.cs` | The floor. One *adopts* a context; the other swaps the code cache, a Broiler.JS optimisation with no JSEAL vocabulary |
| `IDomBridgeRuntime.Attach(JSContext, …)`, implemented in `DomBridge.cs` | Declared in `Broiler.HtmlBridge.Core`, and consumed by `Broiler.Cli`/`Broiler.Wpt`/`Broiler.DevConsole`, which are not in this checkout |
| `BridgeModuleContext`, and the sub-document module roots in `DomBridge/SubDocuments.cs` | It derives from the engine's module context to inject specifier resolution and CSP-gated fetch, and a frame's module roots run on that context. JSEAL has no module-graph contract; the one it would need is written out in `BridgeModuleContext`'s remarks |
| `DomBridge/ComputedStyle.cs` | Assigning `document.adoptedStyleSheets` copies the array in engine terms: the realm can mint an array but cannot read one back with the engine's hole treatment. A gap in the contract rather than an unmigrated caller |
| `Runtime/JsInterop.cs` | The cast between a JSEAL handle and the engine's own object. Two files cross it: `ComputedStyle.cs` above, and `Features/StyleSheetBinding.cs`, whose `RetireIndex` names no engine type and so is not in the count |

Two are contract gaps — the module graph, with a written specification waiting, and reading an array
back with the engine's hole treatment; the rest is the floor, a signature consumed outside this
checkout, and that cast. One more gap sits behind the cast and outside the count: `IJsMembers` has
no indexed delete, so `StyleSheetBinding.RetireIndex` reaches the engine's element list through it.

**What used to be listed here as "the one real cost of the value design" was a mistake, and it is
worth recording rather than quietly dropping.** Six doc comments, the table this section used to carry and the budget file all
said a `ConditionalWeakTable` could not be keyed from a handle, because such a table needs a
reference key and `JsValue` is a struct. The struct was never the key: the reference it *carries* is,
and a provider is already required to make that canonical per guest object because handle equality is
defined by it. `JsValue.ObjectIdentity` says so out loud, neither provider needed a line of change to
satisfy it, and five of the bridge's per-object tables crossed on it without changing an answer. One
of the six comments had already cost something real — `NavigatorSurfacesBinding` dropped weakness on
that reasoning and leaked an entry per `permissions.query()` for the life of a document.

The belief survived four attempts to act on it. What broke it was asking what the *provider* promises
rather than what the *struct* can be.

**Another gap is recorded and not yet closed.** `IJsExotic` routes an integer-index key to the indexed
hooks, has no indexed *write* hook, and gives a handler no way to declare that it has no indexed
properties at all. `Storage` has named property getters and setters and no indexed ones, so
`localStorage[8]` is a name — and both engines treat it as an index.
`WebStorageTests.ADigitOnlyKeyIsANamedPropertyLikeAnyOther` carries the case, skipped, with the
contract named rather than the module.

**The conformance suite has earned its place three times, and it is what both contract additions were
measured against.** Every one of its tests is a theory over every registered provider and none of them
names an engine type, so a contract member is not landed until both providers answer it identically —
which is how the delete hook's index-versus-name filter and the binary members' `SharedArrayBuffer`
exclusion were found, both being cases where one engine would have disagreed with the other in
silence. It found `JsCall.NewTarget` always
reporting `Missing` inside a host constructor; an exotic object's supported names being filtered out
of `Object.keys` and object spread, so `Object.keys(form.elements)` saw no named controls; and
`DefineIndex` not growing an Array's length, which made an index written through the contract
invisible to every array generic. A fourth defect — an adopted realm installing its own job pump as
the thread's synchronization context, stranding the host's promise reactions — was caught by the
contract's own remarks during the worker migration. All four are fixed, and all four would have
reached a page.

## Broiler.VM: the second engine

There is a `Broiler.HtmlBridge.Jseal.Vm`, and it declares `JsCapabilities.Document`. **This section
twice said that was impossible, and was twice wrong in the same shape** — a true statement about one
mechanism, read as a statement about every mechanism. Both are kept below rather than deleted,
because the shape is available to anyone reasoning about a second engine from its contracts rather
than from a probe, and the second one survived a whole section written to correct the first.

**The argument that was wrong.** A DOM accessor returns an object; a Broiler.VM value capability
answers a `long` or a `VmOpaqueRef`; an opaque reference is by construction not dereferenceable;
therefore no registration any composition could make would deliver a DOM object to a guest. Every
step of that is true, and the conclusion does not follow. It assumes the object has to travel
through the **capability channel**, and it does not: a host object in Broiler.VM's JavaScript
profile is an ordinary object in the realm, its methods are ordinary native functions, and calling
one is the interpreter's own call path — the same one the standard library takes when
`Array.prototype.map` invokes the function it was handed. Nothing about it reaches the VM core, so
no core member has to be able to carry it.

**The reentrancy argument was wrong in a second way, and a probe settled it.** This section used to
say the VM core refuses a re-entrant call for the duration of a host frame. It does, but it refuses
on the *declaration*: the binding hands the descriptor's own mode to the runtime, which raises the
in-capability depth only for `NonReentrant`, and the source-level contract says the refusal applies
"where the capability declared `NonReentrant`". Nothing refused a re-entrant **value** capability;
nothing had ever declared one. A test in that repository now declares one and watches a nested call
complete, with a control that flips the declaration and gets the refusal back.

**What is true, and is the part worth carrying forward,** is that the wall is somewhere else than
this document said. A host callback re-entering *the instance that is executing* is refused by a
different gate that never reads the declaration, and lifting that gate is not a small change: the
VM's per-operation scope is not nestable and has no restore, its load mediator's fan-out bounds
reset for a nested operation and never restore, a nested meter bills its interval twice, and
cancellation does not reach the inner operation. The seam described below needs none of that,
because nothing nests.

### What Broiler.VM now provides

`Broiler.VM.Profile.JavaScript` publishes a host surface: a realm object an embedder holds, a value
type carrying a stable opaque identity per guest object, minting for objects, arrays, methods,
constructors and exotic objects, property definition including accessors, reads and writes, and a
call back into the guest. A composition supplies its embedder when it builds the profile
descriptor, and **registration is still the permission**: a realm reaches an embedder only where the
composition also registered an optional capability whose handler is never invoked, so a build that
linked an embedder has no host object in its realms unless the composition that ran it said so. The
decision record is `JSD-0024` in that repository; the programme around it is its hosting roadmap.

Three properties of that seam matter to a provider written against it. Every crossing charges a host
call and fuel proportional to what it carries, so an embedder cannot buy unmetered work. The realm
is valid only inside a step, on the guest's own thread, and refuses by name outside it. And an abort
— a spent allowance, a cancellation — reaching host code is latched: catching it clears nothing, and
the operation still ends the way the core was told it would. **A provider that wraps guest calls in
`catch (Exception)`, which is ordinary defensive style, cannot turn a spent allowance into a
completed page load.**

### The provider

`src/Broiler.HtmlBridge.Jseal.Vm` exists and **passes the conformance suite in full**. It registers
as `broiler-vm`, and a build that links it selects it with `BROILER_JS_ENGINE=broiler-vm`. It is
linked under the `-VM` configurations only, through the same conditional reference
`Broiler.HtmlBridge.Scripting.Vm` already carries — so the default build has one engine and the
`-VM` build has two, which is what the suite's own totals show.

**What it declares, and what it does not.**

| Flag | Declared | Why |
|---|---|---|
| `HostScriptSource` | yes | Through the realm's own indirect `eval`, which is the only thing that evaluates *into* an existing realm rather than making a second one |
| `ClassicScriptSource` | yes | The same route, under a single-use permit that the one compile it authorises spends, so a realm built without `GuestEval` still runs the page's script elements |
| `GuestEval` | yes, unless the realm was built without it | A realm built with `AllowGuestEval: false` keeps host script and classic scripts and refuses the page's `eval` and `new Function` outside a host script, which the provider enforces by marking host script and arming a single-use permit per classic script |
| `ExoticObjects` | yes | The profile's exotic object consults ordinary storage first, which is the order WebIDL requires |
| `GlobalIsVariableScope` | yes | Measured, not assumed: a top-level `var` becomes an own property of the global |
| `ReentrantHostCalls` | yes | A host method calling a guest listener is the interpreter's own call path and meets no lifecycle gate |
| `Promises` | yes | The realm's own `Promise` is read off the global, an executor is minted with `NewMethod`, and `Construct` runs it — so the pair a host settles is the pair the language made |
| `WorkerRealms` | **no** | One realm per instance, and no agent model to clone between. `IJsClone`'s members exist and refuse |
| `Modules`, `DynamicImport` | **no** | JSEAL has no module-graph contract to implement against yet |

**So `JsCapabilities.Document` is declarable, and the bit that used to be missing was `Promises`.**

**The argument that kept it missing is the second instructive mistake in this section.** `NewPromise`
refused, and the reason it gave was that the only way to fake a settleable promise is to *evaluate* a
snippet capturing the resolvers — so a page whose Content-Security-Policy forbids evaluation would be
handed a `fetch` promise built out of the capability it had just refused. That objection is correct
about the route it names. It is not correct about the engine, because evaluation is not the only way
to reach a constructor: `GetProperty`, `NewMethod` and `Construct` are three ordinary crossings of
the host surface and none of them compiles a character. The profile's own hosting roadmap said as
much in the clause bounding what its promise stage would add — *"an embedder can already build one
out of `Construct`"* — and this repository did not read it.

`JsealConformanceTests.APromiseIsStillAvailableInARealmThatForbidsGuestEvaluation` is that claim
turned into an assertion: a realm built with `AllowGuestEval: false` refuses dynamic source and
still hands back a promise that settles. It runs against both engines.

**What declaring `Document` does and does not say.** It says a host may build a document-bearing page
in a realm this provider made. It does not say the browser does — `IDomBridgeRuntime.Attach` still
takes a `JSContext`, so the realm a page load adopts is still Broiler.JS's. That gap is the
migration's, and is the `IDomBridgeRuntime.Attach` row of the table above.

### What writing it found

Six defects, each caught by a contract the provider had to satisfy rather than by review.

- **The step-jobs path did not open the host window.** A drain worked and a step did not, so a
  promise reaction reaching a host method was refused — and the refusal named the realm rather than
  the path that had failed to open it.
- **A turn took no artifact-load mediator**, so an embedder's own script met "this composition
  registered no artifact provider" in a composition that had registered one.
- **A host constructor was handed no receiver.** The profile's built-in constructors make and return
  their own object; an embedder describing an interface expects the language's behaviour, which is
  an object created from `new.target.prototype` and passed as `this`.
- **A member the realm installed reached the exotic handler as an assignment**, which made an
  embedder unable to install a member whose name its own handler claimed — an `item()` method on a
  collection containing something called `item` is the ordinary case, not a corner.
- **Property attributes did not travel**, so a non-enumerable member was enumerable.
- **Top-level declarations reached the global in the wrong order.** `GlobalDeclarationInstantiation`
  creates every function binding before every var binding, and the lowering emitted vars first — so
  a host recovering a frame's declarations by diffing the global's own names read them in an order
  no other engine produces.

The first five are the seam's; the last is the profile's lowering, and it is the one that would have
reached a page.

**The measurement that answers "is this working" has moved again, and this is now the honest one.**
It was never "the bridge no longer names Broiler.JS" — that is relocation. Then it was "how many DOM
operations are expressible without a host→guest re-entry and without a host capability returning an
object", and both of those constraints are gone. Then it was "how much of a page loads on a realm
that declares everything except `Promises`", and that qualifier is gone too. What it is now:
**`Attach` takes a `JSContext`, so no page has ever loaded on this provider's realm — the measurement
is what happens the first time one does.** Until `Attach` takes an `IJsRealm`, every claim in this
section is a claim about a conformance suite and not about a page, and the section says so rather
than letting a reader infer a browser from a green test run.

**The upstream ask that is left.** One row remains genuinely blocked, and it is not about the DOM:
the VM's capability channel still cannot answer a guest with bytes, so a global the *guest* reaches
without an embedder — a shell-shaped `read` — can exist and refuse and nothing more. That row is
filed in that profile's amendment register and is unaffected by anything here.
