# JSEAL — the JavaScript Engine Abstraction Layer

JSEAL provides .NET 10 contracts for hosts that create JavaScript objects, install callbacks,
execute scripts and drive promise jobs. This repository contains the contracts, two providers,
tests and packaging tools. HTML/DOM integration belongs to consuming repositories; passing this
suite does not demonstrate a browser page load.

## Projects and dependency boundaries

| Project | Responsibility |
| --- | --- |
| [Broiler.JSeal](../Broiler.JSeal/Broiler.JSeal.csproj) | Engine-neutral values, realm interfaces, capabilities and registry |
| [Broiler.JSeal.BroilerJs](../Broiler.JSeal.BroilerJs/Broiler.JSeal.BroilerJs.csproj) | Broiler.JS implementation and optional adoption of a host-created JSContext |
| [Broiler.JSeal.Vm](../Broiler.JSeal.Vm/Broiler.JSeal.Vm.csproj) | Broiler.VM JavaScript profile implementation |
| [Broiler.JSeal.Tests](../Broiler.JSeal.Tests/Broiler.JSeal.Tests.csproj) | Shared conformance, registry and provider-specific ownership tests |

Both providers are in [Broiler.JSeal.slnx](../Broiler.JSeal.slnx), including ordinary Release builds.
The **test project's VM reference and registration** are conditional: Release tests JS;
Release-VM tests both providers. A consuming application's references and registration determine
which providers it can select.

The contracts project declares no ProjectReference or PackageReference. It uses .NET framework
types, but cannot directly name engine types with its current references. Provider code uses
`Broiler.JSeal.Providers` to store opaque values; host bindings should use JsValue and IJsRealm.

There is **no dedicated neutrality-ratchet script or CI check for added references** in this
checkout. Compilation enforces configured references; it would not reject a future edit adding
an engine dependency. Reference review remains necessary. The [CI workflow](../.github/workflows/ci.yml)
builds and tests both configurations on Windows and Linux, runs isolated package consumers on both,
tests preview-version selection, and packs/verifies packages on Windows. It does not inspect
downstream DOM binding neutrality.

## Values and callbacks

[JsValue](../Broiler.JSeal/Values/JsValue.cs) is a readonly struct containing a kind tag, numeric
field and opaque reference. Its measured x64 size is 24 bytes, not a serialization guarantee.
JS object handles retain engine objects; VM handles retain canonical boxed host values keyed by
stable VM object identity. ObjectIdentity can key host weak tables without boxing the whole handle.

Missing is kind zero, distinct from explicit undefined. JsValue.ToString is a diagnostic rendering
that never executes guest code; realm ToJsString and ToNumber perform guest coercions.
AsBoolean is a cheap handle inspection; use realm ToBoolean when BigInt is possible. The VM provider
currently has no BigInt support. Equality uses object identity and strict equality for non-BigInt
primitives; NaN is unequal under `==` but Equals is reflexive for .NET collections. BigInt handles
compare by reference, a known limitation rather than mathematical BigInt equality.

[JsCall](../Broiler.JSeal/Values/JsCall.cs) carries the realm, receiver, arguments and NewTarget.
Its argument span is borrowed for the callback duration; copy values that must survive the callback.
An out-of-range argument is Missing. NewMethod creates a non-constructable function; NewConstructor
supplies constructor behavior and a prototype. NewTarget is Missing for ordinary calls and identifies
the construction target otherwise.

## Realm operations and behavior

[IJsRealm](../Broiler.JSeal/Realm/IJsRealm.cs) aggregates six operation interfaces plus IDisposable.
[IJsExotic](../Broiler.JSeal/Realm/IJsExotic.cs) is a separate host handler passed to NewExotic.

| Interface | Operations |
| --- | --- |
| IJsValues | Create objects, arrays, functions, exotics and buffers; coerce values and read buffer bytes |
| IJsMembers | Define/read/write members and indices, inspect keys and manage prototypes |
| IJsCalls | Invoke and construct; produce exceptions for host callbacks to throw |
| IJsJobs | Enqueue/drain jobs and create promises with retained resolving actions |
| IJsSource | Evaluate host, classic and dynamic source under separate authorization rules |
| IJsClone | Classify transferables, clone locally, detach/adopt a cloned graph |

Declarations are in [IJsRealmCapabilities.cs](../Broiler.JSeal/Realm/IJsRealmCapabilities.cs)
and [IJsClone.cs](../Broiler.JSeal/Realm/IJsClone.cs). Error and DomError return an exception for
the callback to throw; callers must not assume an engine-specific CLR type from those factories.
Unsupported capabilities use JsCapabilityUnavailableException.

**`IsFunction` reports callability, including callable proxies.** The Broiler.JS provider uses the
engine's `IsFunction` predicate before classifying general objects. It retains the proxy's own
identity, without reading guest properties or unwrapping its target. Revocation leaves a callable
proxy classified as a function; invoking it throws a guest `TypeError`.

**`IsArray` means an actual Array exotic object.** It is not the ECMAScript `Array.isArray` operation,
which follows proxy targets and can throw after revocation. Array proxies remain Object handles;
J05 does not widen this contract or introduce guest operations during classification.

**Guest failures cross member and coercion boundaries as `JsEngineException`.** Its `Thrown` value
preserves numbers, explicit `undefined`, and object identity. The Broiler.JS provider translates
only its engine's guest exception while the realm is current, including getter/setter calls, Proxy
traps and enumeration traversal. Property definitions and prototype writes use the engine's virtual
operations so traps run. Definition descriptors have no prototype, preventing guest properties on
`Object.prototype` from supplying descriptor fields. String coercion uses `StringValue`, which
honors `Symbol.toPrimitive` and the string hint; number coercion uses `DoubleValue`.

**Ordinary named properties take precedence over `IJsExotic` handler values.** This includes own
and inherited properties whose value is `undefined`, getters returning `undefined`, and accessors
with only a setter. Presence is checked before reading an ordinary getter, which runs once with
the original receiver. Removing the ordinary property exposes the handler on subsequent reads.
The Broiler.JS provider checks ordinary presence before reading; the VM adapter declines handler
lookups for names found on the prototype chain, complementing the VM's own-property check.

**`IJsExotic.IndexedLength` describes a dense range.** Every index below this exclusive bound must
be supplied by `TryGetIndex`; returning true with `undefined` represents a present entry. The range
must be consistent during an operation, but can grow, shrink or replace values between operations.
A hole below the bound is a host contract error: adapters raise `InvalidOperationException` when
validation encounters it. To withdraw entries, shrink the bound. Handler entries are enumerable,
configurable and read-only. Ordinary own indexed properties take precedence, and are preserved
when the collection grows over them or shrinks past them. The JS provider tracks materialized slot
ownership; an explicit ordinary definition ends handler ownership. Deleting that ordinary override
exposes the current handler entry if its index is still within the range.

**A failed job stops that drain and leaves later jobs queued.** The failed job has been removed;
the next drain resumes with its successors, including jobs it enqueued before throwing. A guest
throw is exposed as `JsEngineException`; an ordinary exception from a queued host `Action` propagates
unchanged. The previous realm and synchronization context are restored on either path. Promise
reaction and thenable failures remain rejections where JavaScript requires them. Captured
resolve/reject actions enter their promise's owning realm, enqueue reactions on its pump without
draining, and refuse calls after disposal, including when the promise was already settled.

**Realm disposal is idempotent and runs no queued work.** Only `EngineName` and `Capabilities`
remain readable through the realm. All other operations throw `ObjectDisposedException` after
disposal, including global access, pending-job queries, primitive coercion, buffer reads and transfer
classification. Disposal takes precedence over capability refusals for valid arguments; invalid
argument-validation order is unspecified. Retained resolve/reject actions also throw, without
reading a thenable or scheduling reactions. Handle-only `JsValue` inspections remain available.
Hosts must serialize disposal and settlement with other realm operations, and dispose only after
an active operation has returned. An adopted wrapper discards its own jobs and removes only its own
policy subscriptions. Its original host still owns the context and its engine-scheduled job queue.

**VM callback arguments use an inline buffer through eight values, then a pooled array.** Each
invocation owns its buffer through the host body's return, including nested callbacks. The pooled
path clears references in `finally` after success, guest throws, host failures or conversion failures.
This removes the adapter's per-callback argument array; the VM host API and outbound JSeal calls
still allocate their own arrays. [J12 measurements](../diagnostics/J12/README.md) separate these costs
and record the warmed adapter reduction of 48–792 bytes per call at the measured non-zero arities.

## Source authorization

The host classifies source and authorizes each classic script before calling JSEAL. JSEAL does
not parse a browser's Content-Security-Policy.

| Entry point | Provider behavior |
| --- | --- |
| EvaluateHostScript | Trusted host-authored source; ForceStrictMode applies here |
| EvaluateClassicScript | Host has authorized the script; supplied strictness is preserved |
| EvaluateDynamicSource | Requires GuestEval; supplied strictness is preserved |

AllowGuestEval=false removes GuestEval and blocks guest eval and Function compilation, as well as
the dynamic-source API. Host/classic evaluation remains available and does not lend its authorization
to guest callbacks. JS enforces this through its evaluation hook. VM invokes a captured eval intrinsic
with a single-use permit for host/classic compilation. The source provider consumes it before parsing,
including failed parses, and nested API calls restore the prior state. Guest indirect eval and Function
use their own source's strictness on both providers. The
[source authorization tests](../Broiler.JSeal.Tests/JsealConformanceTests.SourceAuthorization.cs)
pin these boundaries.

Source identity remains incomplete: JS passes label to Eval; VM currently uses a fixed compiler unit
name and the label only in a missing-eval diagnostic. Neither provider consumes DocumentUrl.
J17 in the [JSeal roadmap](roadmap.jseal.md) tracks that decision. No source method executes module graphs.

## Capabilities and limits

A realm may narrow its provider's capabilities. Document combines HostScriptSource,
ClassicScriptSource, Promises, ExoticObjects, GlobalIsVariableScope, ReentrantHostCalls and BinaryData.
It does not imply complete ECMAScript support or integration into an external browser.

| Capability | Broiler.JS | Broiler.VM |
| --- | --- | --- |
| HostScriptSource, ClassicScriptSource | Yes | Yes; creation checks eval availability |
| GuestEval | Unless disabled by options | Unless disabled by options; needs eval |
| Promises, ExoticObjects | Yes | Yes |
| GlobalIsVariableScope, ReentrantHostCalls | Yes | Yes |
| BinaryData | Yes; SharedArrayBuffer excluded from byte reads | Yes; creation checks binary intrinsics |
| WorkerRealms | Yes, including structured clone transfer | No |
| Modules, DynamicImport | Only when ModuleSupport returns true | No |

The [JS provider](../Broiler.JSeal.BroilerJs/BroilerJsEngineProvider.cs) treats a missing, false or
throwing ModuleSupport callback as unsupported. Its Modules package also supplies ordinary arguments
objects; that dependency is not proof of module integration. JSEAL has no module-loader/evaluation
contract. The coverage table in [JsealConformanceTests.cs](../Broiler.JSeal.Tests/JsealConformanceTests.cs)
exempts Modules and DynamicImport as not expressible and maps other declared flags to tests.
This is an inventory check, not proof of complete engine semantics.

The [VM provider](../Broiler.JSeal.Vm/VmEngineProvider.cs) owns one runtime, verified bootstrap artifact
and instance per realm. It reuses the active execution step for callbacks and opens a host turn for
external calls. Promise creation uses the captured intrinsic constructor without source evaluation.
Binary operations and some exotic behavior also use captured intrinsics. Clone, Detach and Adopt
refuse WorkerRealms; ClassifyTransferable returns NotTransferable on a live VM realm. These mechanisms
do not expand the threading contract or provide unhandled-rejection reporting.

## Registration and selection

Module initializers register providers on assembly load. Register explicitly when selection must
be available before incidental assembly loading. A host referencing Broiler.JSeal.BroilerJs can use:

```csharp
using Broiler.JSeal;
using Broiler.JSeal.BroilerJs;

BroilerJsEngineProvider.Register();
using var realm = JsEngineRegistry.Default.CreateRealm(new JsRealmOptions
{
    AllowGuestEval = false
});
var result = realm.EvaluateHostScript("20 + 22", "host:example");
Console.WriteLine(realm.ToNumber(result));
```

To add VM, reference Broiler.JSeal.Vm and call Broiler.JSeal.Vm.VmEngineProvider.Register().
BROILER_JS_ENGINE=broiler-vm selects it only if registered; SetDefault also selects a known provider.

Names are matched case-insensitively. The first registration becomes the default; `SetDefault`
selects another registered name, and replacement preserves that selection. Removing the selected
default chooses the remaining name first in ordinal, case-insensitive order. Removing the last
provider leaves the registry empty, so `Default` throws until another provider is registered.
Removing an unknown name has no effect. `Reset` clears registrations and selection together.

The environment value is read on each `Default` call and trimmed. Unknown or blank values fall
back to the registered default without changing it. One lock protects registration, selection and
snapshot reads. Each operation is coherent; returned providers and `All` snapshots can outlive a
later removal. Registry mutation tests exercise isolated instances of the same internal state,
leaving conformance providers and the process environment untouched.

## Adding a provider

1. Create `Broiler.JSeal.<Engine>` referencing `Broiler.JSeal` and its engine packages.
2. Implement IJsRealm and IJsEngineProvider, preserving identity, Missing, callback lifetime,
   source authorization, exceptions and ownership semantics.
3. Declare only supported capabilities. Implement IJsRealmAdoption only for borrowed contexts
   with separate ownership.
4. Add the project to the solution, test references and explicit discovery registration. Shared
   theories use registered provider names; engine-specific tests belong in separate classes.
5. Extend isolated package consumers and CI composition as needed. Review the contracts dependency
   boundary; there is no local engine-reference budget file to update.

Build/test commands are in the [README](../README.md); package checks are described in the
[consumer harness guide](../eng/package-consumer/README.md). The conformance suite includes
provider-independent facts and provider theories, with JS adoption tested separately.

## Scope, history and follow-up

JSEAL was extracted from HtmlBridge on 2026-09-19. Its central design decision is to expose a realm,
rather than just a script-list execution interface: hosts also create objects, bind callbacks,
preserve identity and drive jobs. Old bridge file counts are not an inventory of this checkout.

The [retained review baseline](../diagnostics/J00/README.md) records dated observations, while the
[JSeal roadmap](roadmap.jseal.md) records fixes and remaining contract work. Native VM host APIs,
modules, structured clone and browser integration belong to the [integration roadmap](roadmap.integration.md).
Verify consuming repositories before making claims about their current signatures or page-load support.

The [human review record](../HUMAN_REVIEW.md) remains **PENDING**; automated checks are not human approval.
For documentation-only validation, run `python diagnostics/J14/check_docs.py` from the repository root.
It checks local Markdown links, heading targets and literal local project examples. It does not fetch
external URLs or validate optional sibling checkouts.
