# Host and cross-repository integration slices

[Roadmap index](roadmap.md). I09 and I11 are **complete**. The VM halves of I01, I03, I05, I07,
I11 and I12, and I14-I17, are released in Broiler.VM `0.1.0-preview.4`, pinned since 2026-09-23.
JSeal has adopted I11, I12's VM routing and I18's VM clone against that release; I02, I04, I06 and
I08 are still prepared only against a local candidate. I10, I12 and I18 are **partial**; I13 is not
started. These slices concern the host surface
and JSeal contracts; missing adapter behavior does not imply the VM lacks the underlying language
feature. Native host API additions map primarily to VM JSP-10; modules to JSW-8; job integration
to JSW-7. Structured clone needs explicit owning milestones in the VM ledger when scheduled.

For every upstream API/consumer pair: first merge and verify the VM API, then make the compatible
package available through the normal release process, then explicitly update JSeal's pins and
adopt the API using J19's consumer gate. Validate candidates with a local feed. Do not combine an
unavailable API reference with an unrelated adapter fix.

## I01 — Add a native VM ArrayBuffer read operation

- **Status:** VM half implemented in the Broiler.VM working tree; local validation only, no package
  yet. `JsHostRealm.TryReadArrayBuffer` copies into a new host-owned `byte[]` or a caller span, brand
  checks the realm's own buffer type without reading any guest-writable property, and answers
  `JsHostBufferStatus` (`Copied` including empty, `NotAnArrayBuffer`, `Detached`,
  `DestinationTooSmall`). It charges one host call and then one fuel unit per byte before copying;
  exhaustion or cancellation ends with nothing written. Contract in VM JSD-0024 section 12. The CLI
  `--host-surface` lane covers it, but no automated suite runs that lane yet. Evidence:
  VM `docs/evidence/jseal-i01-i03/README.md`.
- **Owner / prerequisites:** VM host API; define compatibility and metering contract first.
- **Work:** Add a bulk byte-copy operation that performs a real ArrayBuffer brand check and can
  distinguish unsupported values, empty buffers, and detached buffers according to a documented
  contract. Copy into host-owned memory or a caller-provided bounded span; do not expose the VM's
  mutable internal storage or execute user-overridable JS hooks.
- **Accept:** Exact bytes for empty, short, and multi-chunk inputs; wrong-brand and detached cases;
  cancellation and size/budget failure without partial success. Native read is independent of
  Uint8Array, constructor, species, join, and global mutations.
- **Exclude:** SharedArrayBuffer and retaining a borrowed span after a host turn.

## I02 — Adopt native buffer reads in JSeal

- **Status:** Blocked on a Broiler.VM release. Adoption prepared and validated but not merged. The
  adoption lives in `jseal-vm-next3-over-normal.patch` (placeholder pin VM `0.1.0-preview.4`); it
  passed both configurations and the J19 candidate lane against local candidate
  `0.1.0-preview.4.local.7`, built from the merged wave-10 VM tree and never published. It merges
  with the VM release and pin update. Re-measured on local.7, every host-call and fuel figure equals
  the local.5 run. `TryGetArrayBufferBytes` uses `JsHostRealm.TryReadArrayBuffer`; the guest
  construct/join path and captures are removed. Measured on a 1 MiB read inside a step: 17.1 MB /
  265 host calls -> 1.05 MB / 1. The new acceptance cases (replaced intrinsics, wrong brands,
  detachment by transfer, snapshot ownership) merged now against the pinned packages and exposed a
  Broiler.JS provider defect: its read handed out the engine's own storage. That fix is merged.
- **Owner / prerequisites:** JSeal VM provider; I01 package available, J19.
- **Work:** Replace typed-array subarray/join and decimal parsing with the native read operation.
  Remove capture fields and helper constants only when no remaining path uses them. Preserve the
  public TryGetArrayBufferBytes contract, including its ownership and detached-buffer behavior.
- **Accept:** Existing binary tests plus wrong-brand, detached, and monkey-patched-intrinsic cases
  pass. Compare allocations and host crossings against the old implementation on identical sizes.
- **Exclude:** Changing the public method to return VM-owned memory.

## I03 — Add native VM ArrayBuffer construction from bytes

- **Status:** VM half implemented with I01; local validation only, no package yet.
  `JsHostRealm.NewArrayBuffer(ReadOnlySpan<byte>)` charges the call, the bytes and the live-bytes
  retention before allocating, builds on the intrinsic `ArrayBuffer.prototype`, and copies the
  caller's bytes; later caller mutation cannot change it, and failures answer no value.
- **Owner / prerequisites:** VM host API; align ownership/error rules with I01.
- **Work:** Add one metered operation that allocates a buffer and copies caller bytes into it.
  Validate size/budget limits before publishing the result. Make the lifetime of the caller's span
  explicit and keep the construction independent of guest globals and typed-array species.
- **Accept:** Later caller mutation cannot change the buffer. Allocation failure has no usable
  partial result, empty buffers work, and bounded large copies honor cancellation/accounting.
- **Exclude:** Zero-copy external storage, shared memory, and arbitrary lifetime pinning.

## I04 — Adopt native buffer construction in JSeal

- **Status:** Blocked on a Broiler.VM release. Adoption prepared and validated but not merged. The
  adoption lives in `jseal-vm-next3-over-normal.patch` (placeholder pin VM `0.1.0-preview.4`); it
  passed both configurations and the J19 candidate lane against local candidate
  `0.1.0-preview.4.local.7`, built from the merged wave-10 VM tree and never published. It merges
  with the VM release and pin update. Re-measured on local.7, every host-call and fuel figure equals
  the local.5 run. `NewArrayBuffer` uses `JsHostRealm.NewArrayBuffer`; all binary intrinsic captures
  are removed and BinaryData is decided by performing the native operation once. Measured on a 1 MiB
  construct inside a step: 79.0 MB / 266 host calls -> 1.05 MB / 1. On Broiler.JS a mint after the
  page replaced the `ArrayBuffer` global takes the page's prototype, recorded as a provider gap in
  the test.
- **Owner / prerequisites:** JSeal VM provider; I03 package available, J19; coordinate with I02.
- **Work:** Remove the guest ArrayBuffer/Uint8Array construction and per-byte JsHostValue chunks from
  NewArrayBuffer. Delete the now-unused binary intrinsic capture machinery after both paths migrate.
- **Accept:** Input immutability and round-trip tests pass; hostile changes to globals/prototypes do
  not change the operation. Retain before/after allocation and crossing counts. BinaryData is still
  advertised only when the actual native operations are present.
- **Exclude:** General-purpose benchmark infrastructure beyond the repeatable measurements needed.

## I05 — Add a VM host exotic-deletion hook

- **Status:** VM half implemented in the Broiler.VM working tree; local validation only, no package
  yet. Optional `IJsHostExoticDeletion.TryDeleteNamed` (existing `IJsHostExotic` implementers are
  unaffected) is offered every non-index string key once per deletion, on every route, before the
  ordinary deletion, whose result `delete` returns - matching JSeal's `IJsExoticDelete` contract.
  Index and symbol keys never reach it; handler exceptions translate like host function bodies. No
  internal Proxy. Contract in VM JSD-0024 section 13.1. Evidence: VM
  `docs/evidence/jseal-i05-i07/README.md`.
- **Owner / prerequisites:** VM host API; review compatibility with existing IJsHostExotic implementers.
- **Work:** Add an optional deletion interface or compatible extension rather than break all exotic
  handlers. Specify named strings versus array-index strings and symbols, and the relationship
  between handler completion and ordinary deletion. Follow JSeal's existing deletion contract unless
  a documented incompatibility requires a separately reviewed contract change.
- **Accept:** The handler receives eligible names once; ordinary deletion still runs; integer indices
  and symbols are not misrouted; declined and claimed names behave correctly. Cover non-configurable
  ordinary properties and handler exceptions.
- **Exclude:** Introducing a Proxy internally to simulate the hook again.

## I06 — Remove JSeal's deletion Proxy workaround

- **Status:** Blocked on a Broiler.VM release. Adoption prepared and validated but not merged. The
  adoption lives in `jseal-vm-next3-over-normal.patch` (placeholder pin VM `0.1.0-preview.4`); it
  passed both configurations and the J19 candidate lane against local candidate
  `0.1.0-preview.4.local.7`, built from the merged wave-10 VM tree and never published. It merges
  with the VM release and pin update. A `VmDeletingExoticObject` implements `IJsHostExoticDeletion`
  for handlers that declare `IJsExoticDelete`; the Deleting Proxy, its trap and the Proxy/Reflect
  captures are removed. New boundary and replaced-intrinsic cases pass on both providers and merged
  against the pinned packages.
- **Owner / prerequisites:** JSeal VM provider; I05 package available, J19.
- **Work:** Implement the native optional deletion hook in VmExoticObject. Remove Deleting, the
  delete trap, and now-unused Proxy/Reflect captures. Preserve identity and method/property behavior.
- **Accept:** Existing exotic-deletion tests pass for handlers with and without deletion support.
  A guest replacing Proxy or Reflect.deleteProperty has no effect. Named/index/symbol boundaries
  remain identical to the public JSeal contract.
- **Exclude:** Altering ordinary Proxy language semantics in VM.

## I07 — Add native VM promise capability creation

- **Status:** VM half implemented with I05; local validation only, no package yet.
  `JsHostRealm.NewPromiseCapability`/`ResolvePromise`/`RejectPromise` create a genuine realm promise
  on the intrinsic prototype and settle it through the engine's own resolve procedure; reactions run
  only from the job queue; first settlement wins (`AlreadyResolved` afterwards). Settlement is valid
  only inside a step on the guest thread (`RealmNotCurrent` otherwise, including after release),
  foreign values are refused, and `JsHostValue.Missing` is an `ArgumentException` that leaves the
  capability open. Contract in VM JSD-0024 section 13.2.
- **Owner / prerequisites:** VM host API; define settlement lifetime and thread-entry rules.
- **Work:** Expose creation of a genuine realm promise plus resolving/rejecting handles through the
  host surface. Reuse the engine's own promise state machine and queued reaction machinery. Ensure
  settlement reenters a valid metered turn and cannot execute guest code on an arbitrary CLR thread.
- **Accept:** Resolve/reject are first-settlement-wins; thenables follow the engine's promise rules;
  reactions execute through the documented job queue; late/disposed/wrong-realm operations have
  explicit outcomes. Guest Promise replacement does not affect host capability creation.
- **Exclude:** A parallel promise implementation in the adapter or immediate reaction execution.

## I08 — Adopt native promise creation in JSeal

- **Status:** Blocked on a Broiler.VM release. Adoption prepared and validated but not merged. The
  adoption lives in `jseal-vm-next3-over-normal.patch` (placeholder pin VM `0.1.0-preview.4`); it
  passed both configurations and the J19 candidate lane against local candidate
  `0.1.0-preview.4.local.7`, built from the merged wave-10 VM tree and never published. It merges
  with the VM release and pin update. `NewPromise` uses
  `NewPromiseCapability`/`ResolvePromise`/`RejectPromise`. New first-settlement, thenable and
  disposal cases merged against the pinned packages and exposed a VM provider defect (settling with
  `Missing` passed the engine's hole marker, so no reaction ran); that fix is merged.
- **Owner / prerequisites:** JSeal VM provider; I07 package available, J11, J19.
- **Work:** Replace the captured Promise constructor and temporary executor function with the native
  promise operation. Keep the existing public resolve/reject delegates and their documented lifetime.
- **Accept:** Fulfillment, rejection, thenable assimilation, repeated settlement, restricted-eval
  realms, and disposal behavior pass on both providers. Remove only truly obsolete bridge state.
- **Exclude:** Expanding JSeal's concurrency guarantees implicitly through these delegates.

## I09 — Design an engine-neutral module contract

- **Status:** Design complete in [the module contract design](jseal.modules.md); no code or
  capability changed. It proposes optional `IJsModules`/`IJsModuleHost`/`IJsModuleMap`/`IJsModule`
  contracts: host-assigned canonical keys with synchronous resolution, asynchronous cancellable
  loading applied through realm tasks, engine-owned linking, evaluation settled only through the job
  queue with cached errors, dynamic import through the same map, and module policy separate from
  GuestEval. It walks through a two-module graph, a cycle, a rejected load, top-level await,
  repeated import and disposal, and maps Modules/DynamicImport to I10-I13 witnesses. Probes found
  that the pinned Broiler.JS package lacks live bindings, the namespace exotic object, cyclic TDZ and
  top-level-await ordering, and that the VM has no public in-realm graph entry; these become
  upstream follow-ups. Owner decisions are pending on ModuleSupport compatibility and the VM host
  additions.
- **Owner / prerequisites:** JSeal contracts with JS/VM API review; J18.
- **Work:** Specify a separate module-facing contract for canonical module identity, host resolution
  and loading, graph lifetime, linking, evaluation completion, namespace access, and dynamic import.
  Define source labels, job-pump integration, resolver failures, and module policy separately from
  classic source and unsafe-eval permission. Prefer an optional interface over enlarging every
  provider's minimum implementation by accident.
- **Accept:** Walk a two-module graph, cycle with live bindings, rejected load, top-level await,
  repeated import, and disposal through the API design. No engine, filesystem, network, or DOM type
  appears in contracts. Establish which features each capability promises.
- **Exclude:** Hard-coded URL/network resolution and a production implementation in the design PR.

## I10 — Implement the JS module adapter over the existing engine

- **Status:** Partial in JSeal; the upstream engine work exists. The I09 contract types are in
  `Broiler.JSeal/Modules`, and the Broiler.JS adapter is implemented behind the internal
  `EnableModuleContract` gate with no published Modules/DynamicImport capability. On the pinned
  Broiler.JS `0.1.0-preview.1` package the adapter refuses at link time the graphs whose answers
  that engine would get wrong (reassigned or nested-var exports, cycles, import(), CommonJS names,
  direct eval, globally shared top-level names) and records namespace writes and a few parser
  deviations as gaps. **Upstream:** the Broiler.JS working tree now implements module semantics per
  the specification - live immutable import bindings, the module namespace exotic object, link-time
  SyntaxErrors for missing or ambiguous imports, cyclic TDZ, spec top-level-await ordering with
  cached evaluation errors, strict module scope without CommonJS names, the parser fixes, and every
  specifier through Resolve - with its full test suite passing apart from two pre-existing
  time-zone-dependent tests and module Test262 788 -> 1095 of 1667 with no regressions. It is not
  packaged; once a Broiler.JS release is pinned the adapter's refusals can be removed and the
  provider-neutral cases run unrefused.
- **Owner / prerequisites:** JSeal Broiler.JS provider; I09 and required compatible engine packages.
- **Work:** Adapt the engine's existing module graph/loader to the chosen contract. Start with a
  deterministic in-memory graph and source labels. Preserve module identity and live bindings;
  do not reimplement linking in JSeal. Keep new capability exposure gated pending I13.
- **Accept:** Two-module evaluation, a missing import, namespace identity, one evaluation per module,
  and live binding updates work through JSeal alone. Guest errors use the shared exception boundary.
- **Exclude:** Browser fetching, CommonJS interoperability, and module capability claims based merely
  on the presence of an assembly.

## I11 — Implement the VM module adapter over existing graph support

- **Status:** Complete on 2026-09-23, against the released Broiler.VM `0.1.0-preview.4`: the
  adapter below was re-created from the prepared patch against that package, and **all 47
  provider-neutral module cases pass on broiler-vm with no gap row**, alongside
  `VmModuleAdapterTests` for the gate, `MaxModules`, host re-entry and a realm without the
  contract. It stays behind the internal `VmEngineProvider.EnableModuleContract` gate, and no
  capability is declared, pending I13. The history follows. Upstream, the Broiler.VM working tree
  had `JsHostRealm.LoadModule`/`EvaluateModule` (JSD-0024 section 15), an
  evaluation walk that follows the pinned algorithm including concurrent async siblings and
  [[AsyncEvaluationOrder]] settlement, and `JsHostRealm.TryGetModuleState` ([[Status]], the
  identical [[EvaluationError]], [[HasTLA]] and [[CycleRoot]], section 20.1). In JSeal,
  `VmModuleMap` implements the I09 contract for the VM provider behind the internal
  `EnableModuleContract` gate, and now **reads module state instead of inferring it**: the
  timing-based walk-completion marker, the search of a module's text for `await`, the predicted
  evaluation walk and the three `Status` refusals are gone (the file falls from 1375 to 968 lines).
  Two rules stay JSeal's own and are documented in [the module contract design](jseal.modules.md): a
  cycle member whose [[CycleRoot]] is still evaluating-async reports `EvaluatingAsync`, and one
  whose root ended with an evaluation error reports `Errored` with that value; a module the language
  has finished reports `EvaluatingAsync` until the evaluation that finished it has delivered its
  settlement, which is what keeps the contract's "Status reaches Evaluated only after that promise
  fulfils" true before a host drains. The adoption lives in `jseal-vm-next3-over-normal.patch`
  (placeholder pin VM `0.1.0-preview.4`); it passed both configurations and the J19 candidate lane
  against local candidate `0.1.0-preview.4.local.7`, built from the merged wave-10 VM tree and never
  published. It merges with the VM release and pin update. On the candidate **all 47 of I10's
  provider-neutral module cases pass on VM with no engine-gap row left** (42 of 46 on the previous
  candidate); the cases the refusals covered failed first against the previous map. An adversarial
  review found one regression in the rewrite - a cycle member whose root ended in an error answered
  `Evaluated` - which was reproduced as a case and fixed. Broiler.JS behaviour is unchanged.
  Evidence: VM `docs/evidence/jseal-vm-host-modules/README.md`, `jseal-vm-module-host/README.md`,
  `jseal-vm-module-gaps/README.md`.
- **Owner / prerequisites:** JSeal VM provider, with small VM host additions if required; I09, J19.
- **Work:** Map the same in-memory module contract to VM compilation/linking. Preserve script/module
  parse goals and module identity in artifact requests. Add an upstream host operation first if the
  required in-realm graph entry point is not public; do not compile module payloads as classic scripts.
- **Accept:** Run I10's provider-neutral cases unchanged against VM. Error labels identify the failing
  module, repeated imports reuse the graph identity, and missing modules fail through the specified
  channel. Keep capability exposure gated pending I13.
- **Exclude:** Claiming VM needs a new module language implementation when only host access is missing.

## I12 — Route dynamic import through the module contract

- **Status:** Implemented for the VM against the released `0.1.0-preview.4` (2026-09-23): the
  eleven shared dynamic-import cases pass on broiler-vm. The Broiler.JS adapter still refuses a
  module that calls `import()` on `0.1.0-preview.3`, whose engine resolves the specifier through a
  synchronous `Resolve` at run time that the adapter cannot answer from the host; those rows stay
  gaps. The history follows. The VM seam is `IJsHostModuleLoader` with deferred `CompleteModuleRequest`/`FailModuleRequest`, and the
  adoption routes `import()` through the map with the calling module's key or the calling script's
  label. Since the VM carries GetActiveScriptOrModule()'s referrer into eval code, `Function` bodies
  and code a job runs (JSD-0024 section 20.3), **the adapter needed no change and its recorded
  refusal is gone**: eleven shared dynamic-import cases pass on VM with no gap. The adoption lives
  in `jseal-vm-next3-over-normal.patch` (placeholder pin VM `0.1.0-preview.4`); it passed both
  configurations and the J19 candidate lane against local candidate `0.1.0-preview.4.local.7`, built
  from the merged wave-10 VM tree and never published. It merges with the VM release and pin update.
  The referrer of code a promise job runs (`p.then(eval)`) is deliberately not pinned by a contract
  case: HostEnqueuePromiseJob asks an implementation to make the enqueuing script or module active
  again, which Broiler.VM does and Node does not, while the pinned Broiler.JS engine carries no
  referrer across a job at all; the question is recorded for I13. The Broiler.JS side stays refused
  on the pinned package (all eleven cases are gap rows).
- **Owner / prerequisites:** Both JSeal providers and any upstream host seams; I10-I11.
- **Work:** Route import() through host resolution using the calling module's identity and the
  defined job queue. Cache module identity consistently with static imports. Define failure and
  cancellation behavior without conflating module loading with guest eval.
- **Accept:** Dynamic import resolves asynchronously to the same namespace as static import;
  concurrent requests share loading/evaluation as specified; resolution/evaluation failures reject.
  Cover restricted-eval realms, nested imports, and disposed realms.
- **Exclude:** Allowing arbitrary network access or blocking a host thread waiting for its own jobs.

## I13 — Verify module cycles, async completion, and capability publication

- **Owner / prerequisites:** JSeal integration; I10-I12 and J18.
- **Work:** Complete shared tests for cycles, live bindings, top-level await, rejection propagation,
  resolver reentry, and teardown. Fix issues in their owning engine/adapter slices rather than
  suppressing comparisons. Publish Modules/DynamicImport capabilities only for the contract behavior
  actually supported, and update coverage reporting and package-consumer tests.
- **Accept:** Both providers meet the declared module contract under bounded tests; absence is explicit
  on unsupported configurations; package-only consumers can exercise the API. No false success from
  an unobserved evaluation task or undrained promise.
- **Exclude:** A whole-Test262 module conformance claim from a handful of host tests.

## I14 — Build a VM clone graph for primitives, objects, and arrays

- **Status:** Implemented in the Broiler.VM working tree as an internal, unadvertised carrier; local
  validation only. Proposed VM record JSD-0032 fixes the clone format, carrier lifetime and cost
  model: detached data with no realm handles, valid after its source runtime is disposed, adoptable
  more than once, charged per record, key, value and byte, and bounded (2^22 entries, 2^28 bytes).
  Serialization follows HTML StructuredSerializeInternal order with a memory map: cycles, shared
  references, holes and sparse lengths survive; getters run and their throws propagate; functions,
  symbols, proxies, detached buffers, views over detached buffers and unlisted brands are refused.
  Slice-compiler check rows (JIT and Native AOT) cover these cases, a 200,000-node chain and adoption
  after disposal. Accepted limitation: carrier memory is not reported to LiveBytes (I17). No public
  API. Evidence: VM `docs/evidence/jseal-i14-i15/README.md`.
- **Owner / prerequisites:** VM; explicit clone-format/lifetime decision and cost model.
- **Work:** Design a detached carrier containing clone data, not live source-realm handles. Implement
  primitive, ordinary object, and sparse array cloning with a visited-object map, preserving cycles
  and repeated references. Define getter/error behavior and reject uncloneable values. Keep the
  transport private until its supported type set is complete for the intended public contract.
- **Accept:** Cycles terminate, shared references stay shared within the clone, sparse holes survive,
  and mutation does not cross the clone boundary. Functions/symbols and unsupported brands fail
  explicitly; validation and allocation are bounded.
- **Exclude:** A JSON round-trip clone, guest prototype sharing, or a WorkerRealms claim.

## I15 — Extend clone support to the required built-in brands

- **Status:** Implemented with I14; internal only. The JSD-0032 matrix covers Boolean/Number/String
  wrappers, Date, RegExp (source and flags), Map and Set (order, cycles, repeated members), Error
  forms per the HTML rules, fixed ArrayBuffers, typed arrays and DataViews (views over one buffer
  share one destination buffer); destination prototypes belong to the destination realm and no brand
  is flattened. BigInt and resizable buffers have documented future rows. Delivered as one patch
  with I14 rather than one per brand group.
- **Owner / prerequisites:** VM; I14, and a documented supported-type matrix.
- **Work:** Add brand-specific clone handling in separate small PRs: Date/RegExp; Map/Set; then the
  required Error forms and binary views. Reconstruct values with destination-realm intrinsics.
  Define how BigInt and resizable buffers enter the matrix when their own features land.
- **Accept:** Each listed brand has value/state tests, graph-identity tests, and explicit unsupported
  cases. Map/Set cycles and repeated keys/values are preserved; destination prototypes belong to the
  destination realm. A brand is never silently flattened into an ordinary object.
- **Exclude:** DOM objects, host resources, and claiming support for every structured-clone brand.

## I16 — Implement ArrayBuffer transfer with validation before detachment

- **Status:** Implemented in the Broiler.VM working tree as an internal extension of the JSD-0032
  carrier; local validation only. Transfer lists are validated completely (ArrayBuffer brand,
  duplicates, and after the walk, attachment and the byte bound) before any detachment, so a
  refusal, guest throw or abort leaves every source usable and success detaches every listed
  buffer; aliases and views share one destination buffer; a carrier holding moved bytes can be
  adopted once. Resizable buffers are refused as not transferable (added when F04-F06 merged).
  Evidence: VM `docs/evidence/jseal-i16/README.md`.
- **Owner / prerequisites:** VM; I14-I15's binary handling. Agree on the ownership contract with
  I01-I04. Fixed-buffer transfer does not wait for F04-F06; explicitly refuse unsupported resizable
  transfers until that integration is implemented.
- **Work:** Validate the complete transfer list and clone graph before committing ownership changes.
  Reject duplicates, wrong brands, already-detached buffers, and unsupported transfers. Preserve
  aliasing among views and ensure failure does not prematurely detach source storage.
- **Accept:** Successful transfer preserves bytes and detaches the source; every view observes the
  prescribed detached state. Failed cloning/validation leaves the source usable. Multiple references
  to one buffer share one destination buffer. Document resizable-buffer behavior explicitly.
- **Exclude:** Shared-memory transfer and transferring arbitrary host objects.

## I17 — Support detached clone adoption on a second realm/thread

- **Status:** Implemented in the Broiler.VM working tree as public host API; local validation only.
  `JsHostRealm.DetachClone(value, transfer)` returns an opaque `JsHostCloneCarrier` (profile, format
  version, single-use flag, charged bytes) and `AdoptClone` rebuilds the graph in the receiving
  realm; both are step-bound, thread-checked, charged crossings. A carrier holds data only, may cross
  threads, survives its source's disposal, is repeatable unless it holds transferred bytes (then
  single-use, claimed atomically), and anything else is refused as `ForeignCarrier`. The sender pays
  LiveBytes for the carrier before any buffer detaches. Host-surface rows run each runtime on its own
  thread (realm-local prototypes, adoption after disposal, a two-thread race on a single-use
  carrier, budget deltas). Accepted limitation: a carrier held after its sender is disposed is
  outside every budget. JSD-0024 section 17 and JSD-0032 section 5b. No WorkerRealms claim (I18).
  Evidence: VM `docs/evidence/jseal-i17/README.md`.
- **Owner / prerequisites:** VM host API; I14-I16.
- **Work:** Expose detach/adopt operations with a precise carrier lifetime, compatibility/version
  rule, and thread-safety contract. Materialize destination objects on the destination realm's thread.
  Keep guest objects out of cross-thread carrier state and reject foreign-engine carriers.
- **Accept:** Source and destination can execute on distinct host threads using their supported turn
  model; source disposal does not corrupt a valid carrier; adoption reconstructs realm-local
  prototypes and identities. Define and test repeated adoption or single-use behavior explicitly.
- **Exclude:** Sharing live JsHostRef objects or creating a browser Worker event loop in VM.

## I18 — Expose VM cloning through JSeal and enable the earned capability

- **Status:** Partial; WorkerRealms remains. A public `StructuredClone` capability flag (a new bit;
  existing values unchanged) separates same-realm cloning from WorkerRealms, as J18 decided; it is
  declared for broiler-js, whose pinned engine's deviations from HTML StructuredSerialize are
  recorded as gaps in [the guide](jseal.md), and **for broiler-vm since 2026-09-23**, against the
  released `0.1.0-preview.4`: every shared clone case passes on it, and a realm declares the flag only
  after it cloned a value at handover. The history follows. The prepared VM adoption implements `Clone`, `Detach`,
  `Adopt` and `ClassifyTransferable` over `DetachClone`/`AdoptClone` (JSD-0024 section 17, carrier
  layout 2 with BigInt). The adoption lives in `jseal-vm-next3-over-normal.patch` (placeholder pin
  VM `0.1.0-preview.4`); it passed both configurations and the J19 candidate lane against local
  candidate `0.1.0-preview.4.local.7`, built from the merged wave-10 VM tree and never published. It
  merges with the VM release and pin update. `Clone(Missing)` is undefined on both providers and a
  Missing transfer entry is the engine's DataCloneError. WorkerRealms is **not** declared for
  broiler-vm: its transfer carriers are single-use, which contradicts `IJsClone.Adopt`'s promise of
  repeatable adoption; second-thread transport is exercised only through an internal test gate.
- **Owner / prerequisites:** JSeal VM provider; I17 package available, J18-J19.
- **Work:** Implement Clone, Detach, Adopt, and ClassifyTransferable with engine-native operations.
  Adopt any separately designed same-realm clone capability without changing existing enum values.
  Advertise WorkerRealms only after second-thread creation and transport satisfy its full contract.
- **Accept:** Shared provider tests cover cycles, supported brands, uncloneable values, transfer
  failure atomicity, source detachment, foreign-engine rejection, and destination-realm ownership.
  Unsupported builds still fail explicitly. Package consumers exercise both same-realm cloning and
  second-thread transport.
- **Exclude:** Enabling WorkerRealms just because basic object copying works, or promising a full
  browser Worker implementation from the realm capability alone.
