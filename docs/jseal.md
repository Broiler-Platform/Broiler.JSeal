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
AsBoolean is a cheap handle inspection; use realm ToBoolean when BigInt is possible. Equality uses
object identity and strict equality for non-BigInt primitives; NaN is unequal under `==` but Equals
is reflexive for .NET collections.

**BigInt (B06).** A BigInt handle is opaque: JsValue cannot read its integer, so two rules follow,
and both providers implement them the same way.

- *Truthiness* is `IJsValues.ToBoolean`: `0n` (however computed) is false, every other BigInt is
  true, and a BigInt object is an object and true. `AsBoolean` answers true for every BigInt handle
  and stays that way.
- *Equality* is `IJsValues.IsStrictlyEqual`, ECMAScript `===` without running page script: two
  BigInts are equal when their integers are, and a BigInt never equals a Number or a String. For
  every other kind it answers what `==` answers, except that `JsValue.Missing`, which is no
  JavaScript value, is `undefined` there (as both providers make it in `Clone`):
  `IsStrictlyEqual(Missing, Undefined)` is true although the handles differ. JsValue's `==`, `Equals` and `GetHashCode` compare
  BigInt handles **by reference** and are not changed: a true answer means the same value, a false
  one means nothing, because Broiler.JS hands back the engine's own value and Broiler.VM mints a new
  box per crossing. A host keying a dictionary on a BigInt's value keys on `ToJsString`'s answer.

`IsStrictlyEqual` is a new `IJsValues` member with a default body, so an out-of-tree provider still
compiles; that body answers `==` wherever `==` is `===` and throws `NotSupportedException` for two
distinct BigInt handles. A BigInt handle another engine minted is refused with `JsEngineException` by
every member that would unwrap it (Broiler.JS used to raise an `InvalidCastException`). Values cross
exactly both ways: as a property, as a callback argument or result, through `Invoke`, and through a
same-realm `Clone`. Broiler.VM has BigInt from `0.1.0-preview.4`, the pinned release, which carries
its B05-B08 work (host value kind `JsHostValueKind.BigInt`); the VM provider boxes the exact integer
in the handle, answers `ToBoolean` and `IsStrictlyEqual` from it, and refuses another engine's
BigInt handle. [JsealConformanceTests.BigInt.cs](../Broiler.JSeal.Tests/JsealConformanceTests.BigInt.cs)
holds the shared cases, driven by a stated per-engine table of the BigInt value, `BigInt64Array`/
`BigUint64Array` and the DataView BigInt accessors.

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

**Invoke and Construct refuse a handle that is not a function.** Both providers check the handle's
classification before the engine sees the call and throw `JsEngineException` whose `Thrown` is the
realm's `TypeError`, as ECMAScript's Call does. A noncallable Proxy's `apply` or `construct` trap
never runs; Broiler.JS previously ran it, and the VM previously answered with a host-surface refusal.
The check comes before the VM's foreign-realm check, so a non-function handle from another VM realm
also gets the `TypeError`; a foreign function handle is still refused as foreign. This is the host
API only: guest code calling a noncallable Proxy on the pinned Broiler.JS engine still runs its
`apply` trap, an engine defect that JSeal does not cover.

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

The restriction was audited route by route on the pinned packages, and the
[guest-evaluation route tests](../Broiler.JSeal.Tests/JsealConformanceTests.GuestEvalRoutes.cs) keep
every route refused: each function kind's constructor reached through a prototype, `call`, `apply`,
`bind` or `Reflect`; ShadowRealm `evaluate` reached indirectly, subclassed or nested; promise jobs and
async continuations; string timer callbacks; interop globals and `import('clr')`; and structured
clone into a restricted worker realm. No route compiles. Broiler.JS raises its evaluation hook on the
realm that constructed a ShadowRealm, so `evaluate` is refused there. Code already running inside a
ShadowRealm asks the child context, which nothing subscribes to (VM decision JSD-0030 follow-up
SR-6), and SR-6 remains open upstream. That code is unreachable only while `evaluate` is refused
and `importValue` is unimplemented. A test pins the pinned package's synchronous "not implemented"
`TypeError` from `importValue`, so any package that changes it fails and forces SR-6 to be
reviewed; the test cannot tell a fixed `importValue` from an unfixed one, and has to be replaced by
one that loads an `eval`-calling module once `importValue` loads modules. Broiler.VM has no
ShadowRealm.

The policy belongs to the realm whose `eval`, `Function` or ShadowRealm is called, as ECMAScript's
HostEnsureCanCompileStrings specifies. A restricted realm's own functions compile nothing even when
another realm's API invokes them. A host that hands a permissive realm's `eval`, `Function` or
ShadowRealm into a restricted realm has granted it compilation on Broiler.JS, which does not check
which realm minted a handle. Broiler.VM refuses such foreign handles at the crossing.

### Source identity

Each evaluation selects one diagnostic identity with `JsRealmOptions.SourceLabelFor`: a non-blank
label, then a non-blank `DocumentUrl`, then `anonymous`. `DocumentUrl` has no other meaning: it is
not used for module resolution, caching, origin checks or permission. Labels never select a member,
strictness or permission. No source method executes module graphs; module graphs have their own
optional contract (see [Modules](#modules)), whose source labels come from the host's loads.

| Metadata on `JsEngineException` | Broiler.JS | Broiler.VM |
| --- | --- | --- |
| `SourceLabel` for guest throws and syntax errors leaving a source member | Yes | Yes |
| `SourceLine` for a syntax error in the supplied text | Parser line when trusted; none at end of input, for an unterminated token, or for text with a lone CR, U+2028 or U+2029 | Front-end line; none for text with a backquote and a backslash-continued line |
| `SourceColumn` | Not reported | Front-end UTF-16 column, with the line |
| `ScriptStackTrace` and guest `error.stack` | Engine text with `label:line,column` frames | None |

Only the label is always present. A reported syntax-error line is the ECMAScript line in the
supplied text; where a pinned engine is known to miscount, the provider reports no line rather than
a wrong one. Lines refer to the supplied text under `ForceStrictMode`. JS prepends the directive
on line 1, so its engine-reported first-line columns shift by 13 characters. VM uses a compiler flag. The pinned VM eval request carries only
source bytes and always runs entry `main`, so the label cannot reach VM compilation or guest
frames. A VM source-name, diagnostic and stack API is an upstream follow-up. The
[source identity tests](../Broiler.JSeal.Tests/JsealConformanceTests.SourceIdentity.cs) keep
guaranteed metadata separate from provider-specific stack details.

## Capabilities and limits

**Provider ability and realm permission are separate.** A provider's `Capabilities` states what its
engine can do before any realm exists. A realm's `Capabilities` is that ability narrowed, and is
never wider. Two things narrow it. The host's permissions: today the only one is
`AllowGuestEval = false`, which removes GuestEval and nothing else. And the provider's own check of
the realm it built: the [VM provider](../Broiler.JSeal.Vm/VmEngineProvider.cs) withholds BinaryData
when its bridge captured no binary intrinsics, and HostScriptSource, ClassicScriptSource and GuestEval
when it captured no `eval`, rather than declare what that realm cannot do. With the pinned VM packages
neither check fires, and a default-options realm has exactly its provider's capabilities; the suite
asserts this, so a realm that did hit one of those checks fails instead of quietly narrowing. Calling a contract member without
its capability throws JsCapabilityUnavailableException naming the missing flag. That exception does
not derive from JsEngineException.

Document combines HostScriptSource, ClassicScriptSource, Promises, ExoticObjects,
GlobalIsVariableScope, ReentrantHostCalls and BinaryData. It claims those seven flags, each witnessed
separately, and nothing more. It does not imply complete ECMAScript support or integration into an
external browser. Flag values are public contract and are pinned by a test; a new flag takes a new bit.

| Capability | Broiler.JS | Broiler.VM |
| --- | --- | --- |
| HostScriptSource, ClassicScriptSource | Yes | Yes; through `JsHostRealm.EvaluateScript` |
| GuestEval | Unless disabled by options | Unless disabled by options; needs eval |
| Promises, ExoticObjects | Yes | Yes |
| GlobalIsVariableScope, ReentrantHostCalls | Yes | Yes |
| BinaryData | Yes; SharedArrayBuffer excluded from byte reads | Yes; creation checks binary intrinsics |
| StructuredClone | Yes | Yes (I18); creation checks one clone at handover |
| WorkerRealms | Yes, including structured clone transfer | No; a transfer carrier is single-use (I18) |
| Modules, DynamicImport | Only when ModuleSupport returns true | No |

The [JS provider](../Broiler.JSeal.BroilerJs/BroilerJsEngineProvider.cs) treats a missing, false or
throwing ModuleSupport callback as unsupported. Its Modules package also supplies ordinary arguments
objects; that dependency is not proof of module integration. A true callback describes the host's own
module integration, not the JSEAL module contract below.

### Modules

The optional module contract designed by I09 exists since I10: `IJsModules`, `IJsModuleHost`,
`IJsModuleMap`, `IJsModule` and their value types, in the contracts assembly with no new references.
No provider advertises Modules or DynamicImport for the contract yet (the host-asserted ModuleSupport
callback above is separate), and no realm from a registered provider or from adoption implements
`IJsModules`. The Broiler.JS adapter is reached only through an internal, test-only provider option
until I13. It runs static graphs through the engine's own module loader, compiles module code strict,
and refuses, at link time and with the reason, the graphs it can see the pinned engine would run
wrongly, including every module that contains `import()`; the VM adapter is I11. [The module contract design](jseal.modules.md#i10-implementation-status) records what
the adapter does, each refusal, and the remaining gaps.

### Capability coverage

[JsealConformanceTests.Capabilities.cs](../Broiler.JSeal.Tests/JsealConformanceTests.Capabilities.cs)
accounts for every single flag, for every registered provider:

- **Witness.** Each flag has one named witness theory. Its rows come from `EnginesDeclaring`, so it runs
  for each provider that declares the flag, plus any flag the witness needs to observe the result.
  It does not run for other providers. A witness asserts that its realm has the capability and does not
  return early without it. Other capability-dependent theories choose their rows in the same way.
- **Refusal.** Each flag a provider does not declare that gates a contract member gets its own row of
  `AnUndeclaredCapabilityIsAnExplicitRefusalNotAPass`. That row asserts the realm lacks the flag and
  calls the gated member, which must throw JsCapabilityUnavailableException, the contract's documented
  rule and nothing more. Test reports therefore list each refusal by provider and flag. A refusal is
  never counted as a witness. Only the WorkerRealms refusal (broiler-vm) runs
  today; the other specified refusals are checked only for existence until a provider without the
  flag is registered.
- **Absence only.** GlobalIsVariableScope and ReentrantHostCalls gate no member, and the contract does
  not say what an engine without them does. Their rows,
  `AnUndeclaredCapabilityWithNoGatedMemberIsOnlyAbsent`, assert only that the realm does not declare
  them. No registered provider produces one.
- **Gap.** Modules and DynamicImport have no witness yet. The module API is designed in I09
  ([the module contract design](jseal.modules.md)) and exists since I10; its provider-neutral cases
  run through the providers' internal gate, and I13 turns them into these witnesses. Until then, a
  provider declaring either flag fails the suite. A provider that does not declare them gets a row of
  `AnUndeclaredCapabilityWithNoContractIsARecordedGap`, never a refusal row, and the two missing
  witnesses appear in every run as skipped tests (`TheModulesWitnessIsNotExercisedUntilI13`,
  `TheDynamicImportWitnessIsNotExercisedUntilI13`).

A kind with no row in a build (for example refusals in Release, where broiler-js declares every flag
but the gaps) reports one placeholder row, `(no registered provider lacks one)`, which asserts that
the kind really is empty; xUnit 2 fails a theory with no data.

Meta-tests resolve witness names by reflection and check that each witness takes rows from its own
flag, requiring at most HostScriptSource besides it, so a provider that declares a flag without some
unrelated one is still witnessed. They also check that every provider/flag pair is witnessed,
refused, absent-only or a recorded gap, exactly once. A removed
witness, a renamed witness or a provider dropped from witness rows fails the suite. In Release-VM,
the suite checks registered identities rather than a count: both broiler-js and broiler-vm run the
Document witnesses and StructuredClone; broiler-js witnesses WorkerRealms; broiler-vm reports one
refusal, WorkerRealms. Release registers only broiler-js. These checks inventory capability claims. They do not
prove complete engine semantics.

**Same-realm structured clone (StructuredClone, I18).** J18 decided that same-realm cloning needs its
own flag on a new bit before any provider has only one half, and I18 adopts it: `StructuredClone`
(`1 << 11`) gates `Clone` and makes `ClassifyTransferable` meaningful; WorkerRealms gates `Detach`
and `Adopt`. Every provider that declares WorkerRealms also declares StructuredClone. Existing values
are not renumbered, and a host that still checks WorkerRealms before `Clone` stays correct, only
narrower. The shared cases in
[JsealConformanceTests.StructuredClone.cs](../Broiler.JSeal.Tests/JsealConformanceTests.StructuredClone.cs)
cover cycles, the brands the engines support, uncloneable values, holes, accessors, primitive
wrappers and array properties, transfer failure atomicity, source detachment and destination-realm
ownership. The VM provider declares StructuredClone since its `0.1.0-preview.4` pin, over the
profile's own carrier (`JsHostRealm.DetachClone`/`AdoptClone`), and passes every shared case with no
gap. It does not declare WorkerRealms: the profile makes a carrier holding transferred bytes
single-use, which contradicts `IJsClone.Adopt`'s promise of repeatable adoption, so its `Detach` and
`Adopt` run only behind an internal test gate.

The flag says that `Clone` works and that a refused value fails with `JsEngineException`; it does not
certify that a provider's engine answers every value as HTML's StructuredSerialize does. The known
deviations of the declaring provider, each pinned by a recorded gap in the shared cases so that a fix
fails the suite until the gap is removed:

| Value | HTML | Broiler.JS 0.1.0-preview.3 |
| --- | --- | --- |
| Symbol, WeakMap, WeakSet, Proxy, Promise | DataCloneError | Symbol returned as is; the others copied as plain objects |
| RangeError and the other native error types | Name kept | Rebuilt as `Error` |
| Array with holes, or with non-index properties | Length and properties kept | Holes compacted (the copy is shorter), properties dropped |
| Accessor property | Value read through the getter | Dropped |
| Boolean, Number and String objects | Brand and value kept | Empty plain object |
| BigInt object | Brand and value kept | Plain object (a BigInt primitive is kept) |

A host that must give pages HTML's exact answers on Broiler.JS checks these values itself before
calling `Clone`. The Broiler.JS rows are an upstream clone fix taken through a pin update.

The [VM provider](../Broiler.JSeal.Vm/VmEngineProvider.cs) owns one runtime, verified bootstrap artifact
and instance per realm. It reuses the active execution step for callbacks and opens a host turn for
external calls. Promise creation uses the captured intrinsic constructor without source evaluation.
Binary operations and some exotic behavior also use captured intrinsics. Clone refuses
StructuredClone, Detach and Adopt refuse WorkerRealms, and ClassifyTransferable returns
NotTransferable on a live VM realm. These mechanisms
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
