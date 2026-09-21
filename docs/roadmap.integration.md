# Host and cross-repository integration slices

[Roadmap index](roadmap.md). All slices are **not started**. These slices concern the host surface
and JSeal contracts; missing adapter behavior does not imply the VM lacks the underlying language
feature. Native host API additions map primarily to VM JSP-10; modules to JSW-8; job integration
to JSW-7. Structured clone needs explicit owning milestones in the VM ledger when scheduled.

For every upstream API/consumer pair: first merge and verify the VM API, then make the compatible
package available through the normal release process, then explicitly update JSeal's pins and
adopt the API using J19's consumer gate. Validate candidates with a local feed. Do not combine an
unavailable API reference with an unrelated adapter fix.

## I01 — Add a native VM ArrayBuffer read operation

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

- **Owner / prerequisites:** JSeal VM provider; I01 package available, J19.
- **Work:** Replace typed-array subarray/join and decimal parsing with the native read operation.
  Remove capture fields and helper constants only when no remaining path uses them. Preserve the
  public TryGetArrayBufferBytes contract, including its ownership and detached-buffer behavior.
- **Accept:** Existing binary tests plus wrong-brand, detached, and monkey-patched-intrinsic cases
  pass. Compare allocations and host crossings against the old implementation on identical sizes.
- **Exclude:** Changing the public method to return VM-owned memory.

## I03 — Add native VM ArrayBuffer construction from bytes

- **Owner / prerequisites:** VM host API; align ownership/error rules with I01.
- **Work:** Add one metered operation that allocates a buffer and copies caller bytes into it.
  Validate size/budget limits before publishing the result. Make the lifetime of the caller's span
  explicit and keep the construction independent of guest globals and typed-array species.
- **Accept:** Later caller mutation cannot change the buffer. Allocation failure has no usable
  partial result, empty buffers work, and bounded large copies honor cancellation/accounting.
- **Exclude:** Zero-copy external storage, shared memory, and arbitrary lifetime pinning.

## I04 — Adopt native buffer construction in JSeal

- **Owner / prerequisites:** JSeal VM provider; I03 package available, J19; coordinate with I02.
- **Work:** Remove the guest ArrayBuffer/Uint8Array construction and per-byte JsHostValue chunks from
  NewArrayBuffer. Delete the now-unused binary intrinsic capture machinery after both paths migrate.
- **Accept:** Input immutability and round-trip tests pass; hostile changes to globals/prototypes do
  not change the operation. Retain before/after allocation and crossing counts. BinaryData is still
  advertised only when the actual native operations are present.
- **Exclude:** General-purpose benchmark infrastructure beyond the repeatable measurements needed.

## I05 — Add a VM host exotic-deletion hook

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

- **Owner / prerequisites:** JSeal VM provider; I05 package available, J19.
- **Work:** Implement the native optional deletion hook in VmExoticObject. Remove Deleting, the
  delete trap, and now-unused Proxy/Reflect captures. Preserve identity and method/property behavior.
- **Accept:** Existing exotic-deletion tests pass for handlers with and without deletion support.
  A guest replacing Proxy or Reflect.deleteProperty has no effect. Named/index/symbol boundaries
  remain identical to the public JSeal contract.
- **Exclude:** Altering ordinary Proxy language semantics in VM.

## I07 — Add native VM promise capability creation

- **Owner / prerequisites:** VM host API; define settlement lifetime and thread-entry rules.
- **Work:** Expose creation of a genuine realm promise plus resolving/rejecting handles through the
  host surface. Reuse the engine's own promise state machine and queued reaction machinery. Ensure
  settlement reenters a valid metered turn and cannot execute guest code on an arbitrary CLR thread.
- **Accept:** Resolve/reject are first-settlement-wins; thenables follow the engine's promise rules;
  reactions execute through the documented job queue; late/disposed/wrong-realm operations have
  explicit outcomes. Guest Promise replacement does not affect host capability creation.
- **Exclude:** A parallel promise implementation in the adapter or immediate reaction execution.

## I08 — Adopt native promise creation in JSeal

- **Owner / prerequisites:** JSeal VM provider; I07 package available, J11, J19.
- **Work:** Replace the captured Promise constructor and temporary executor function with the native
  promise operation. Keep the existing public resolve/reject delegates and their documented lifetime.
- **Accept:** Fulfillment, rejection, thenable assimilation, repeated settlement, restricted-eval
  realms, and disposal behavior pass on both providers. Remove only truly obsolete bridge state.
- **Exclude:** Expanding JSeal's concurrency guarantees implicitly through these delegates.

## I09 — Design an engine-neutral module contract

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

- **Owner / prerequisites:** JSeal Broiler.JS provider; I09 and required compatible engine packages.
- **Work:** Adapt the engine's existing module graph/loader to the chosen contract. Start with a
  deterministic in-memory graph and source labels. Preserve module identity and live bindings;
  do not reimplement linking in JSeal. Keep new capability exposure gated pending I13.
- **Accept:** Two-module evaluation, a missing import, namespace identity, one evaluation per module,
  and live binding updates work through JSeal alone. Guest errors use the shared exception boundary.
- **Exclude:** Browser fetching, CommonJS interoperability, and module capability claims based merely
  on the presence of an assembly.

## I11 — Implement the VM module adapter over existing graph support

- **Owner / prerequisites:** JSeal VM provider, with small VM host additions if required; I09, J19.
- **Work:** Map the same in-memory module contract to VM compilation/linking. Preserve script/module
  parse goals and module identity in artifact requests. Add an upstream host operation first if the
  required in-realm graph entry point is not public; do not compile module payloads as classic scripts.
- **Accept:** Run I10's provider-neutral cases unchanged against VM. Error labels identify the failing
  module, repeated imports reuse the graph identity, and missing modules fail through the specified
  channel. Keep capability exposure gated pending I13.
- **Exclude:** Claiming VM needs a new module language implementation when only host access is missing.

## I12 — Route dynamic import through the module contract

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

- **Owner / prerequisites:** VM; I14, and a documented supported-type matrix.
- **Work:** Add brand-specific clone handling in separate small PRs: Date/RegExp; Map/Set; then the
  required Error forms and binary views. Reconstruct values with destination-realm intrinsics.
  Define how BigInt and resizable buffers enter the matrix when their own features land.
- **Accept:** Each listed brand has value/state tests, graph-identity tests, and explicit unsupported
  cases. Map/Set cycles and repeated keys/values are preserved; destination prototypes belong to the
  destination realm. A brand is never silently flattened into an ordinary object.
- **Exclude:** DOM objects, host resources, and claiming support for every structured-clone brand.

## I16 — Implement ArrayBuffer transfer with validation before detachment

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

- **Owner / prerequisites:** VM host API; I14-I16.
- **Work:** Expose detach/adopt operations with a precise carrier lifetime, compatibility/version
  rule, and thread-safety contract. Materialize destination objects on the destination realm's thread.
  Keep guest objects out of cross-thread carrier state and reject foreign-engine carriers.
- **Accept:** Source and destination can execute on distinct host threads using their supported turn
  model; source disposal does not corrupt a valid carrier; adoption reconstructs realm-local
  prototypes and identities. Define and test repeated adoption or single-use behavior explicitly.
- **Exclude:** Sharing live JsHostRef objects or creating a browser Worker event loop in VM.

## I18 — Expose VM cloning through JSeal and enable the earned capability

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
