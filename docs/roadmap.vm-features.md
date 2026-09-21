# Broiler.VM feature additions

[Roadmap index](roadmap.md). All slices are **not started**. These are proposed additions to the VM
JavaScript profile, not prerequisites for fixing JSeal. Modern library work broadly refines JSP-7
and JSW-6; binary work also belongs under JSW-2; Unicode matcher work under JSW-4. BigInt requires
a deliberate manifest widening and an owner in the VM roadmap. Do not reuse JSP-2's completed
literal-refusal objective as proof that BigInt itself is implemented.

Current-source comparisons confirmed the main missing families listed here. Detailed edge cases
in acceptance criteria are requirements to investigate/test, not claims that each was measured in
the review. Select families according to real consumers; the list does not commit to every feature
before the next JSeal release. Preserve metering, cancellation, portability, and explicit refusals.

## F01 — Implement a reusable Float16 conversion primitive

- **Owner / prerequisites:** VM binary/numeric internals; audit existing Math.f16round first.
- **Work:** Establish one bit-accurate binary16 encode/decode operation and reuse compatible existing
  rounding logic. Specify rounding, overflow, subnormals, signed zero, infinities, and NaN behavior.
- **Accept:** Exhaustively decode the finite 16-bit representation space and verify round-trip
  expectations, then test rounding boundaries from Number inputs. Existing Math.f16round behavior
  remains correct. Avoid a new conversion implementation if the existing one already suffices.
- **Exclude:** New globals, view classes, and a parallel numeric representation.

## F02 — Add DataView Float16 accessors

- **Owner / prerequisites:** VM binary built-ins; F01.
- **Work:** Add getFloat16/setFloat16 through the existing DataView bounds/coercion path, handling
  offset conversion, value conversion, byte order, and detached buffers in the specified order.
- **Accept:** Both endian modes, unaligned offsets, edge bit patterns, invalid receivers, bounds,
  detachment, and observable coercion are covered by focused tests and the relevant Test262 subset.
- **Exclude:** Float16Array and resizable-buffer semantics not yet supported.

## F03 — Add Float16Array using the typed-array infrastructure

- **Owner / prerequisites:** VM binary built-ins; F01, V10-V11 for correct species-dependent methods.
- **Work:** Add the element kind, constructor, prototype, BYTES_PER_ELEMENT, buffer views, indexed
  reads/writes, and existing generic methods. Audit serialization/verification assumptions about
  element-kind ranges. Update the feature inventory only when the integrated type is usable.
- **Accept:** Constructors from length, buffer, iterable, and array-like input; conversion/rounding;
  iteration, species, detachment, and host buffer round trips pass. Existing typed arrays do not regress.
- **Exclude:** BigInt content types and duplicate per-method implementations.

## F04 — Model resizable ArrayBuffer state

- **Owner / prerequisites:** VM binary internals; design agreement with the host-copy/transfer
  contracts in I01-I04 and I16. Their implementations are not prerequisites for this internal model.
- **Work:** Define current/max length, resizability, storage replacement, attachment state, ownership,
  and budget accounting. Implement resize as an internal operation first. Preserve the fixed-buffer
  fast path and audit all code retaining spans or raw storage references.
- **Accept:** Grow/shrink/no-op behavior preserves required bytes and zero-fills newly exposed bytes;
  invalid limits and allocation failure leave the old state valid. No native host operation retains
  an invalidated view. Keep public resizing gated until F05-F06 are ready.
- **Exclude:** Growable shared buffers and exposing options that the implementation ignores.

## F05 — Implement fixed-length and length-tracking views

- **Owner / prerequisites:** VM binary internals; F04.
- **Work:** Represent the difference between an omitted view length and an explicit length. Update
  DataView and typed-array bounds against current buffer state; handle temporary out-of-bounds
  states and recovery after growth according to the language rules.
- **Accept:** Shrink/grow sequences cover both view forms, byte offsets, lengths, indexing, DataView
  reads/writes, and detached storage. Recovered views observe the correct data rather than stale
  arrays. View construction and buffer resizing account for their allocations.
- **Exclude:** Assuming every view's length can be cached at construction.

## F06 — Revalidate binary algorithms and publish resizing

- **Owner / prerequisites:** VM binary built-ins; F04-F05, V10-V12.
- **Work:** Audit typed-array methods, iterators, species construction, buffer slice/transfer, and
  native host copying for resizing during observable calls. Add constructor options, resizable,
  maxByteLength, and resize only with the required integrated semantics.
- **Accept:** Focused tests resize during callbacks, coercion, and species construction; iteration
  and out-of-bounds behavior match each algorithm. Constructor options are honored or explicitly
  refused. Run the pinned resizable-buffer subset before feature publication.
- **Exclude:** Rewriting all binary methods in one patch; split the audit by method family if needed.

## F07 — Establish the Unicode data source and build boundary

- **Owner / prerequisites:** VM compiler/runtime data; none.
- **Work:** Choose and record the Unicode version, provenance/license, reproducible data generation,
  supported tables, portability/AOT impact, and package-size cost. Inventory which existing platform
  APIs are permitted and sufficiently deterministic. Define the consumers for normalization and
  regex properties before choosing storage structures.
- **Accept:** Checked-in or reproducibly generated inputs have a pinned version and validation;
  generation is deterministic; runtime builds require no network download. The design names exactly
  which F08/F09 behaviors the tables support.
- **Exclude:** Assuming external data is already available or bundling an unreviewed dependency.

## F08 — Implement non-ASCII normalization

- **Owner / prerequisites:** VM string built-ins; F07.
- **Work:** Implement NFC/NFD/NFKC/NFKD using the chosen data/backend, retaining argument validation
  and a cheap ASCII path where appropriate. Bound intermediate storage and work on expansion-heavy
  inputs. Keep string handling faithful to the required Unicode/code-unit behavior.
- **Accept:** Official normalization vectors and focused JS coercion/error tests pass, including
  canonical combining order, compatibility forms, supplementary characters, and ill-formed UTF-16
  cases required by the pinned language contract. `'e\u0301'.normalize('NFC')` equals `'\u00e9'`.
- **Exclude:** Intl collation/case mapping and silently returning an unnormalized string.

## F09 — Implement Unicode property escapes for supported regex modes

- **Owner / prerequisites:** VM regex parser/matcher; F07.
- **Work:** Add property/property-value resolution and matching for the targeted Unicode-mode
  grammar. Validate accepted aliases and invalid property names. Distinguish `u` requirements from
  any separate `v`-mode or string-property work; do not advertise the latter by implication.
- **Accept:** Positive/negative property classes, supplementary code points, invalid names,
  complements, and escaping are covered. `new RegExp('\\p{Letter}', 'u').test('a')` succeeds.
  Run the appropriate pinned regex tests under a timeout and with work limits intact.
- **Exclude:** Full regex parity and adding unsupported grammar only to reject it inconsistently later.

## F10 — Add RegExp.escape

- **Owner / prerequisites:** VM regex built-ins; none; Unicode database work is not a blanket dependency.
- **Work:** Implement the pinned specification's escaping rules for leading characters, regex syntax,
  punctuators, whitespace, lone surrogates, and ordinary characters. Reuse deterministic formatting
  helpers without relying on an unrelated platform regex-escape function.
- **Accept:** Generated text round-trips as literal matching text, including when embedded next to
  escape sequences. Receiver/argument validation and focused Test262 cases pass.
- **Exclude:** Unicode property matching and a general regex parser rewrite.

## F11 — Establish the Iterator global and prototype hierarchy

- **Owner / prerequisites:** VM iterator built-ins; audit existing per-kind iterator prototypes.
- **Work:** Define the shared iterator prototype, distinguish array/string/map/set iterator
  prototypes where required, and add the correct Iterator construction/from behavior. Preserve
  existing iteration and closing protocols. State supported members explicitly while helpers land.
- **Accept:** Prototype identities and inheritance match the contract; Iterator.from accepts the
  required iterator/iterable shapes; invalid inputs fail. Modifying one kind-specific prototype
  does not accidentally modify all iterator kinds.
- **Exclude:** Replacing specialized iterators with one undifferentiated object.

## F12 — Add lazy Iterator map and filter

- **Owner / prerequisites:** VM iterator built-ins; F11.
- **Work:** Implement shared helper state for lazy stepping, callback/index handling, completion,
  return, and abrupt completion. Add map/filter over that state without eagerly materializing input.
- **Accept:** No source step before consumption; correct callback count/index; reentry handling;
  iterator closing and exception precedence on callback failure; return propagation; finite
  consumption of an unbounded iterator under the runtime's limits.
- **Exclude:** Async iterator helpers and array-backed simulations of laziness.

## F13 — Add lazy Iterator take and drop

- **Owner / prerequisites:** VM iterator built-ins; F12's helper state.
- **Work:** Add limit conversion and bounded skipping/consumption, including zero and infinity where
  specified. Preserve close behavior when take exhausts its limit and avoid unnecessary next calls.
- **Accept:** Negative/NaN/zero/fractional/infinite limit cases match the pinned algorithm; take closes
  at the correct point; drop remains metered for large skips; helper chains preserve laziness.
- **Exclude:** An unbounded host-side loop that bypasses VM fuel/cancellation.

## F14 — Add lazy Iterator flatMap

- **Owner / prerequisites:** VM iterator built-ins; F12.
- **Work:** Extend helper state to the outer and current inner iterator. Apply the specified mapper
  result validation and close both iterators in the proper order under failure or early return.
- **Accept:** Empty and nested iterators, mapper errors, inner-step errors, early return, and close
  exceptions have deterministic state and correct precedence. Repeated completion stays completed.
- **Exclude:** Recursive eager flattening or arbitrary-depth Array.flat semantics.

## F15 — Add terminal iterator helpers

- **Owner / prerequisites:** VM iterator built-ins; F11-F12.
- **Work:** Add toArray, forEach, reduce, some, every, and find using common iterator-step/close
  primitives. Keep callback and short-circuit semantics explicit; split the method checklist into
  additional slices if the common implementation does not keep the patch small.
- **Accept:** Empty inputs, missing reduce initial value, callback index, short-circuit closure,
  thrown callbacks, and large bounded inputs pass. toArray observes array allocation limits.
- **Exclude:** Array methods that merely happen to share names and async helpers.

## F16 — Add Array.fromAsync

- **Owner / prerequisites:** VM array/promise built-ins; existing async iteration/job semantics audited.
- **Work:** Follow the correct preference for async iterator, sync iterator, and array-like input;
  await values and mapping results in order; honor the constructor receiver where required. Use the
  existing promise and job machinery, not blocking CLR waits.
- **Accept:** Async/sync/array-like cases, rejection, mapper ordering, early close, and constructor
  customization pass with an explicitly drained job queue. No job or iterator is lost on rejection.
- **Exclude:** Depending on the new Iterator helpers where the language does not require them.

## F17 — Add JSON.rawJSON and JSON.isRawJSON

- **Owner / prerequisites:** VM JSON built-ins; none.
- **Work:** Introduce a branded raw-JSON value with the exact permitted input validation and integrate
  it into stringify. Implement isRawJSON as a brand operation. Keep normal parsing/stringification
  behavior and replacer ordering intact.
- **Accept:** Valid raw primitives serialize as raw text; malformed or disallowed input is rejected;
  a lookalike object cannot forge the brand. Cover replacers, nesting, and immutable raw payload.
- **Exclude:** JSON reviver source-text context, which was not independently verified in this review.

## F18 — Add disposal symbols and SuppressedError

- **Owner / prerequisites:** VM symbol/error built-ins; none.
- **Work:** Add the required well-known symbols and SuppressedError constructor/prototype/descriptors.
  Reuse ordinary error creation and preserve error/suppressed payload identity.
- **Accept:** Symbol identity is stable within the intended realm/agent model; descriptors and
  constructor behavior match the pinned specification; arbitrary payloads survive unchanged.
- **Exclude:** Claiming resource-management syntax or stack APIs are implemented by these primitives.

## F19 — Implement DisposableStack

- **Owner / prerequisites:** VM built-ins; F18.
- **Work:** Implement synchronous use/adopt/defer/move/dispose operations and disposed state. Capture
  disposal methods at the specified time and unwind in reverse order, combining errors correctly.
- **Accept:** LIFO order, move ownership, disposal twice, registration after disposal, throwing
  disposers, and suppressed-error chains pass. Unwinding remains metered and reentry does not double
  execute an entry. Publish the type only with the complete method set.
- **Exclude:** Async disposal and parser changes.

## F20 — Implement AsyncDisposableStack

- **Owner / prerequisites:** VM built-ins; F18-F19's shared ownership rules and existing promises/jobs.
- **Work:** Implement asynchronous disposal with the correct async/sync method fallback, serialized
  awaiting, move semantics, and error aggregation. Keep all guest calls within normal VM turns.
- **Accept:** Mixed sync/async entries dispose in order; rejected disposers form the right error chain;
  repeated calls observe the specified completion; no blocking waits or CLR-finalizer guest execution.
- **Exclude:** `await using` parsing/lowering, which is F22.

## F21 — Add synchronous using declarations

- **Owner / prerequisites:** VM compiler/runtime; F18-F19 primitives.
- **Work:** Add grammar/static semantics and lowering to resource scopes with cleanup on normal exit,
  return, throw, break, and continue. Capture completion values so cleanup failure combines with
  an existing abrupt completion correctly. Handle only the explicitly supported parse goals first.
- **Accept:** Nested scopes, loops, destructuring restrictions, initializer failure, and abrupt exits
  pass focused parser/runtime tests. Source locations remain meaningful. Artifact/verifier support
  is updated if lowering introduces new instructions.
- **Exclude:** Parsing `using` while lowering it as an ordinary variable declaration.

## F22 — Add await using declarations

- **Owner / prerequisites:** VM compiler/runtime; F20-F21 and existing async suspension machinery.
- **Work:** Extend resource scopes to suspend/resume during asynchronous disposal, with correct
  parse-goal restrictions and completion propagation. Include module contexts only where the
  engine's existing module implementation provides the required async evaluation semantics.
- **Accept:** Cleanup completes before the async function/module settles; nested asynchronous
  cleanup preserves order and exceptions; cancellation/budget failures follow a documented VM
  policy without running unmetered cleanup. Invalid contexts are rejected during compilation.
- **Exclude:** A JSeal module capability claim before I13.

## B01 — Design and add an internal BigInt value representation

- **Owner / prerequisites:** VM value/runtime/compiler owners; explicit manifest and compatibility design.
- **Work:** Choose an allowed representation compatible with the profile's portability/AOT rules,
  value layout, budgets, and artifact versions. Add internal storage/type discrimination and define
  allocation and operation-cost accounting. Audit all exhaustive value-kind switches.
- **Accept:** Design review covers serialization boundaries, host values, arithmetic limits,
  comparisons, formatting, and type mixing. Internal values retain exact integers beyond Number
  precision. Existing artifacts are handled by an explicit version policy.
- **Exclude:** Public BigInt admission, silent Number conversion, and assuming a library is permitted.

## B02 — Parse and lower exact BigInt literals behind the feature gate

- **Owner / prerequisites:** VM compiler/verifier; B01.
- **Work:** Parse supported literal radices directly into an exact representation; validate separators
  and invalid forms. Introduce the required constant/artifact representation and verification bounds.
  Preserve the existing named refusal for manifests that do not admit BigInt.
- **Accept:** Values above 2^53 survive compile/verify/execute exactly in the gated path; malformed
  literals fail at source positions; oversized constants are bounded; older manifests still refuse.
- **Exclude:** Publishing an incomplete BigInt global or permitting malformed artifacts.

## B03 — Implement BigInt arithmetic

- **Owner / prerequisites:** VM numeric runtime; B01-B02.
- **Work:** Implement addition/subtraction, multiplication, division/remainder, and exponentiation
  with explicit mixed Number/BigInt rejection and work charging tied to operand/result size. Split
  multiplication/exponentiation into further slices if their resource model cannot be reviewed
  together. Keep the public feature gated until the complete numeric surface is ready.
- **Accept:** Exact signed arithmetic; truncating division/remainder rules; divide-by-zero and invalid
  exponent errors; large-result refusal; bounded cancellation; no lossy double intermediates.
- **Exclude:** Unmetered arbitrarily large operations and implicit mixed-type conversion.

## B04 — Implement BigInt bitwise and shift operations

- **Owner / prerequisites:** VM numeric runtime; B01-B03.
- **Work:** Implement bitwise operations, shifts, unary operations, and asIntN/asUintN with the
  specified signed behavior and operation-size limits. Preserve unsupported unsigned right shift
  and unary-plus behavior as the required errors.
- **Accept:** Negative values, sign extension, zero/large widths, shift direction, invalid Number
  mixing, and bounded oversized operations pass. Audit compiler constant folding for consistency.
- **Exclude:** Reusing fixed-width Number bitwise conversions.

## B05 — Complete BigInt conversion, comparison, and public admission

- **Owner / prerequisites:** VM built-ins/runtime; B01-B04.
- **Work:** Add BigInt conversion/global/prototype behavior, string parsing/formatting, truthiness,
  equality, relational comparisons, and JSON rejection. Check the complete language-operation
  inventory, then enable the feature only for an explicit supported manifest.
- **Accept:** 0n is false, equal mathematical BigInts compare equal, mixed comparisons retain exactness,
  Number conversions are only allowed by the right operations, Symbol/error cases are correct, and
  the selected pinned BigInt tests pass. Update absent-feature records and format compatibility docs.
- **Exclude:** Describing a named refusal as BigInt support or enabling the global before operators work.

## B06 — Carry BigInt through the VM host API and JSeal

- **Owner / prerequisites:** VM host API, then JSeal adapter; B05, J19 package handoff.
- **Work:** Add the VM host value kind and exact conversion/identity rules, then teach VmMarshal and
  ToBoolean to handle it. Design a realm-level equality operation if consumers need mathematical
  BigInt equality; document the existing JsValue operator limitation rather than silently changing
  dictionary/hash semantics. Audit future clone behavior for BigInt payloads.
- **Accept:** Exact values cross callbacks and properties; 0n truthiness is correct; foreign handles
  are rejected consistently; equal values can be compared through the agreed contract. Separate
  upstream API and consumer adoption PRs if required by the package boundary.
- **Exclude:** Boxing BigInt as Number or claiming all reference-based JsValue equality is JavaScript ===.

## B07 — Add BigInt64Array and BigUint64Array

- **Owner / prerequisites:** VM binary built-ins; B05, V10-V11; integrate F05-F06 if already landed.
- **Work:** Add signed/unsigned 64-bit element conversions, constructors, views, indexing, iteration,
  generic typed-array methods, and content-type checks across typed-array operations.
- **Accept:** Modulo truncation, signedness, Number/BigInt mixing errors, copying, species, detachment,
  and shared-buffer aliasing within one ordinary buffer are covered. Existing Number arrays remain
  behaviorally unchanged.
- **Exclude:** SharedArrayBuffer or treating BigInt arrays as Float64 arrays.

## B08 — Add DataView BigInt accessors

- **Owner / prerequisites:** VM binary built-ins; B05 and the shared binary bounds/coercion helpers.
- **Work:** Add getBigInt64/getBigUint64/setBigInt64/setBigUint64 with endian conversion, bounds,
  detached/out-of-bounds handling, and observable argument conversion order.
- **Accept:** Both endian modes, unaligned offsets, signed boundaries, overflow truncation, wrong
  value types, and detachment/resizing during coercion pass the relevant tests.
- **Exclude:** Requiring typed-array constructors when DataView can use the shared primitives directly.

## D01 — Decide the Intl scope and data strategy

- **Owner / prerequisites:** VM roadmap/API/data owners; no implementation dependency for urgent work.
- **Work:** Produce a concrete proposal for Intl scope, locale/time-zone data, versioning, portability,
  package size, and supported fallbacks. Break an accepted implementation into separate Collator,
  NumberFormat, DateTimeFormat, and later-formatting slices with their own acceptance datasets.
- **Accept:** The decision says either deferred with an explicit consumer limitation, or scheduled
  with bounded first APIs and data dependencies. Existing locale-named methods' fallback behavior is
  accurately documented. No empty Intl object is presented as implementation.
- **Exclude:** Committing to a full internationalization subsystem through one roadmap checkbox.

## D02 — Decide whether shared memory belongs in the VM profile

- **Owner / prerequisites:** VM architecture/agent model owners; no prerequisite for ordinary buffers.
- **Work:** Assess SharedArrayBuffer and Atomics against agent/thread ownership, shared accounting,
  synchronization, wait/wake, cancellation, and embedding policy. Write a concrete design and staged
  follow-up only if the deliberate exclusion is changed.
- **Accept:** The decision retains the exclusion explicitly or defines a supported agent model and
  separately scoped implementation gates. Data races and wait lifetime are addressed before exposing
  APIs. Existing feature inventories remain honest.
- **Exclude:** Adding the globals solely to match a typeof comparison.

## D03 — Decide a host-drained FinalizationRegistry cleanup model

- **Owner / prerequisites:** VM GC/job ownership; audit existing weak-reference semantics.
- **Work:** Document the current intentionally inert registry and design optional cleanup at a
  metered host drain point, with target liveness, held values, cancellation, exceptions, and disposal
  considered. Use deterministic test seams for eligibility; actual GC timing is not a test oracle.
- **Accept:** A proposal identifies where callbacks are queued/run and what remains implementation
  dependent, or records continued deferral. Any later implementation has a separate slice and cannot
  execute guest code directly from CLR finalizers.
- **Exclude:** Promising prompt or guaranteed garbage collection or calling callbacks from a finalizer.

## D04 — Decide the ShadowRealm support boundary

- **Owner / prerequisites:** VM realm/module owners; coordinate with J04 and I09-I13 as applicable.
- **Work:** Pin the intended proposal/specification version and define realm isolation, callable
  wrapping, allowed cross-realm values, inherited compilation policy, lifetime, and optional module
  support. Distinguish this API from browser workers and structured-clone transport.
- **Accept:** A decision includes evaluation and policy-isolation examples plus bounded implementation
  follow-ups, or explicitly defers the feature. A restricted parent cannot regain dynamic compilation
  simply by constructing a child realm.
- **Exclude:** Reusing one global object and presenting it as realm isolation.
