# Broiler.VM feature additions

[Roadmap index](roadmap.md). F01-F22, B01-B05, B07 and B08 are **implemented** in the Broiler.VM
working tree (local validation only; `using` and BigInt are admitted by the proposed records
JSD-0034 and JSD-0033, and F07-F09 run on Unicode 17 data archived under JSD-0031), and B06 is
**partial** (its VM half is implemented; JSeal's half is prepared and waits for the VM pin); D01-D04
are proposed decision records. These are proposed additions to the VM
JavaScript profile, not prerequisites for fixing JSeal. Modern library work broadly refines JSP-7
and JSW-6; binary work also belongs under JSW-2; Unicode matcher work under JSW-4. BigInt requires
a deliberate manifest widening and an owner in the VM roadmap. Do not reuse JSP-2's completed
literal-refusal objective as proof that BigInt itself is implemented.

Current-source comparisons confirmed the main missing families listed here. Detailed edge cases
in acceptance criteria are requirements to investigate/test, not claims that each was measured in
the review. Select families according to real consumers; the list does not commit to every feature
before the next JSeal release. Preserve metering, cancellation, portability, and explicit refusals.

## F01 — Implement a reusable Float16 conversion primitive

- **Status:** Implemented in the Broiler.VM working tree; local validation only, not accepted VM
  milestone evidence, and no VM package carries it yet. Math.f16round was audited: the platform
  `(System.Half)double` conversion rounds once, directly from the double, ties to even, verified
  against an integer-arithmetic reference over every binary16 value, midpoint and neighbour, so it
  is reused behind one primitive, `JsFloat16` (Encode/Decode/Round; NaN stored as 0x7E00), which
  Math.f16round, the DataView accessors and Float16Array share. Probe `the-float16-surface.js`
  decodes and re-encodes all 65536 patterns and rounds every boundary; it agrees with Node.
  Evidence: VM `docs/evidence/jseal-f01-f03/README.md`.
- **Owner / prerequisites:** VM binary/numeric internals; audit existing Math.f16round first.
- **Work:** Establish one bit-accurate binary16 encode/decode operation and reuse compatible existing
  rounding logic. Specify rounding, overflow, subnormals, signed zero, infinities, and NaN behavior.
- **Accept:** Exhaustively decode the finite 16-bit representation space and verify round-trip
  expectations, then test rounding boundaries from Number inputs. Existing Math.f16round behavior
  remains correct. Avoid a new conversion implementation if the existing one already suffices.
- **Exclude:** New globals, view classes, and a parallel numeric representation.

## F02 — Add DataView Float16 accessors

- **Status:** Implemented with F01; local validation only. `getFloat16`/`setFloat16` come from the
  existing DataView accessor loop through a new Float16 element kind, in the specified order (brand,
  ToIndex, ToNumber, ToBoolean, detached TypeError, bounds RangeError). Pinned Test262: 14 -> 70 of 88
  variants; the rest need `$262.detachArrayBuffer` (since added by the merged follow-up fixes) or
  resizable buffers.
- **Owner / prerequisites:** VM binary built-ins; F01.
- **Work:** Add getFloat16/setFloat16 through the existing DataView bounds/coercion path, handling
  offset conversion, value conversion, byte order, and detached buffers in the specified order.
- **Accept:** Both endian modes, unaligned offsets, edge bit patterns, invalid receivers, bounds,
  detachment, and observable coercion are covered by focused tests and the relevant Test262 subset.
- **Exclude:** Float16Array and resizable-buffer semantics not yet supported.

## F03 — Add Float16Array using the typed-array infrastructure

- **Status:** Implemented with F01-F02; local validation only. Float16Array uses the existing
  typed-array infrastructure (element kind, constructor, prototype, BYTES_PER_ELEMENT, views, indexed
  access, every shared `%TypedArray%` method including species) with no per-method copies, and is
  listed in the realm's binary globals. Every typed-array constructor now accepts an iterable
  (`@@iterator` before the array-like path; arrays with the intrinsic iterator keep a charged fast
  drain) and measures array-likes with ToLength. A CLI `--host-surface` check covers the host buffer
  round trip. Pinned Test262: TypedArrayConstructors 552 -> 594 and TypedArray 1042 -> 1410 passing,
  no newly failing variant. The merge exposed a subclass-prototype regression in the iterable path,
  fixed before validation.
- **Owner / prerequisites:** VM binary built-ins; F01, V10-V11 for correct species-dependent methods.
- **Work:** Add the element kind, constructor, prototype, BYTES_PER_ELEMENT, buffer views, indexed
  reads/writes, and existing generic methods. Audit serialization/verification assumptions about
  element-kind ranges. Update the feature inventory only when the integrated type is usable.
- **Accept:** Constructors from length, buffer, iterable, and array-like input; conversion/rounding;
  iteration, species, detachment, and host buffer round trips pass. Existing typed arrays do not regress.
- **Exclude:** BigInt content types and duplicate per-method implementations.

## F04 — Model resizable ArrayBuffer state

- **Status:** Implemented in the Broiler.VM working tree with F05-F06; local validation only.
  `JsArrayBuffer` carries a maximum byte length; resize replaces storage through one operation that
  charges fuel and admits growth through LiveBytes before allocating, and every refusal or failure
  leaves the old state valid (an actual out-of-memory was not provoked). The audit covered views,
  built-ins, the host-surface reads (JSD-0024 section 12 amended: current length, no new status)
  and the clone carrier (JSD-0032 amended: resizable buffers refused). Evidence:
  VM `docs/evidence/jseal-f04-f06/README.md`.
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

- **Status:** Implemented with F04; local validation only. Typed arrays and DataViews distinguish
  fixed-length from length-tracking views and evaluate IsTypedArrayOutOfBounds/IsViewOutOfBounds
  against the current buffer, recovering after growth. The DataView constructor measures the buffer
  before converting byteLength, and the typed-array [[PreventExtensions]] (IsTypedArrayFixedLength)
  is implemented, so freezing a view over a resizable buffer throws.
- **Owner / prerequisites:** VM binary internals; F04.
- **Work:** Represent the difference between an omitted view length and an explicit length. Update
  DataView and typed-array bounds against current buffer state; handle temporary out-of-bounds
  states and recovery after growth according to the language rules.
- **Accept:** Shrink/grow sequences cover both view forms, byte offsets, lengths, indexing, DataView
  reads/writes, and detached storage. Recovered views observe the correct data rather than stale
  arrays. View construction and buffer resizing account for their allocations.
- **Exclude:** Assuming every view's length can be cached at construction.

## F06 — Revalidate binary algorithms and publish resizing

- **Status:** Implemented and published in the working tree with F04-F05; local validation only.
  Binary algorithms were revalidated by family (validation, callbacks, coercions, species,
  iteration, buffer slice/transfer) and `ArrayBuffer(len, {maxByteLength})`, `resizable`,
  `maxByteLength`, `resize` and the transfer rules are published. The pinned resizable subset ran,
  but many of its files also need BigInt64Array; with a modified harness those 149 files pass
  diagnostically, and they must be re-run when B07 lands. Internal clone transfer refuses resizable
  buffers (I16 integration).
- **Owner / prerequisites:** VM binary built-ins; F04-F05, V10-V12.
- **Work:** Audit typed-array methods, iterators, species construction, buffer slice/transfer, and
  native host copying for resizing during observable calls. Add constructor options, resizable,
  maxByteLength, and resize only with the required integrated semantics.
- **Accept:** Focused tests resize during callbacks, coercion, and species construction; iteration
  and out-of-bounds behavior match each algorithm. Constructor options are honored or explicitly
  refused. Run the pinned resizable-buffer subset before feature publication.
- **Exclude:** Rewriting all binary methods in one patch; split the audit by method family if needed.

## F07 — Establish the Unicode data source and build boundary

- **Status:** Implemented in the Broiler.VM working tree (local validation only; VM record JSD-0031
  stays proposed and unsigned). The owner decided on 2026-09-22, in conversation, to retrieve the
  data rather than wait, to publish a Unicode-3.0 notice with the licence text, and to cap the
  tables at 300 KB; JSD-0031 section 9 records this, unsigned. The UCD 17.0.0 files, the Unicode
  licence and the three ECMAScript property tables were retrieved twice, byte-identical, and are
  archived and hashed under `src/tests/unicode/pins/unicode.pin` (Broiler.Unicode was not used as
  the source: its names are matched loosely, it has no normalization or case-folding data and its
  inputs are not pinned; its generated sets served as a cross-check, 946 of 950 agreeing and none
  differing). A C# generator in the architecture test project writes the internal tables (236,721
  bytes against the 307,200 cap) and rules N22 and N23 hold the pin, the tables and the ban on the
  platform normalizer, each with witnesses; the offline build and the Native AOT publish are clean.
  Open: the release owner's co-signature on the notices entry, and how composition images carry
  the notice. Evidence: VM `docs/evidence/jseal-f07-u2/README.md`.
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

- **Status:** Implemented in the Broiler.VM working tree on the F07 tables (JSD-0031 U3; local
  validation only). `normalize` answers NFC, NFD, NFKC and NFKD for any string (full decomposition,
  canonical ordering, composition with blocking, Hangul in code), keeps its coercion order and
  RangeError, passes lone surrogates through, returns an already-normalized string (all ASCII
  included) without allocating, and measures, bounds (the 2^24-unit string ceiling) and charges the
  decomposition before any buffer exists, so U+FDFA repeated ends in fuel exhaustion or a RangeError
  rather than an unbounded allocation. The generator writes 18 differential probes from the archived
  `NormalizationTest.txt` - all 20,034 vectors of parts 0-5 and part 1's claim that every other code
  point is unchanged - which agree with Node (slowest 1.33 s). Pinned Test262 normalize plus staging
  26 -> 36 of 36 variants. Known limit: NFC/NFKC refuse when the intermediate decomposition, not the
  result, passes the string ceiling. Evidence: VM `docs/evidence/jseal-f08/README.md`.
- **Owner / prerequisites:** VM string built-ins; F07.
- **Work:** Implement NFC/NFD/NFKC/NFKD using the chosen data/backend, retaining argument validation
  and a cheap ASCII path where appropriate. Bound intermediate storage and work on expansion-heavy
  inputs. Keep string handling faithful to the required Unicode/code-unit behavior.
- **Accept:** Official normalization vectors and focused JS coercion/error tests pass, including
  canonical combining order, compatibility forms, supplementary characters, and ill-formed UTF-16
  cases required by the pinned language contract. `'e\u0301'.normalize('NFC')` equals `'\u00e9'`.
- **Exclude:** Intl collation/case mapping and silently returning an unnormalized string.

## F09 — Implement Unicode property escapes for supported regex modes

- **Status:** Implemented in the Broiler.VM working tree on the F07 tables (JSD-0031 U4; local
  validation only). Under `u`, `\p{...}` and `\P{...}` work inside and outside classes and in
  complements, resolved by exact name against the names ES2026 admits; misspellings, loose forms
  (`\p{letter}`, `Scx=`), unsupported properties and properties of strings are early SyntaxErrors,
  and `v` is not advertised. All `u`-mode Canonicalize now reads `CaseFolding.txt`, so
  `/\p{Lu}/iu.test('a')` is true and all 1,414 `\p{Lu}` pairs match under `iu`. A greedy quantifier
  over one class runs as one metered instruction with a single backtrack point (charging one step per
  character where the loop charged about five), and property classes reference their tables and are
  charged per lookup. Pinned Test262 property escapes 284 -> 1,170 of 1,170; RegExp plus
  literals/regexp 2,760 -> 3,648 with nothing newly failing. Declared differences from V8: `Hrkt`
  (the specification admits it) and `WSpace` (it does not). Still later: non-`u` Canonicalize from
  `UnicodeData.txt`, `v` mode and ID_Start/ID_Continue for identifiers. Evidence:
  VM `docs/evidence/jseal-f09/README.md`.
  **Later items (2026-09-22):** the follow-ups JSD-0031 listed are implemented too (section 12):
  identifiers are classified by code point against ID_Start/ID_Continue, escaped ones included (so
  `var \u0021 = 1` is an early SyntaxError), WhiteSpace is the language's set (U+0085 no longer
  separates tokens), group names use the same sets, the non-`u` Canonicalize reads `UnicodeData.txt`
  (`/\uA7CE/i` matches U+A7CF), and a `u`-mode `lastIndex` inside a surrogate pair starts at the pair.
  A regex literal's flags are early errors and `v` is an early SyntaxError naming the flag as
  unsupported, so 58 negative `v`-flag Test262 variants pass only because `v` is refused. Tables
  243,897 bytes. Pinned Test262 over those areas 4,274 -> 4,489 of 5,067, nothing newly failing. Known
  limit: 27 Greek ypogegrammeni letters need `SpecialCasing.txt`, which is not archived. Evidence:
  VM `docs/evidence/jseal-unicode-later/README.md`.
- **Owner / prerequisites:** VM regex parser/matcher; F07.
- **Work:** Add property/property-value resolution and matching for the targeted Unicode-mode
  grammar. Validate accepted aliases and invalid property names. Distinguish `u` requirements from
  any separate `v`-mode or string-property work; do not advertise the latter by implication.
- **Accept:** Positive/negative property classes, supplementary code points, invalid names,
  complements, and escaping are covered. `new RegExp('\\p{Letter}', 'u').test('a')` succeeds.
  Run the appropriate pinned regex tests under a timeout and with work limits intact.
- **Exclude:** Full regex parity and adding unsupported grammar only to reject it inconsistently later.

## F10 — Add RegExp.escape

- **Status:** Implemented in the Broiler.VM working tree; local validation only, not accepted VM
  milestone evidence, and no VM package carries it yet. `RegExp.escape` follows the pinned
  EncodeForRegExpEscape steps without a platform escaper: TypeError without coercion for
  non-strings, `\xHH` for a leading ASCII alphanumeric, backslash for syntax characters and `/`,
  control escapes, and `\x`/`\u` forms for the other punctuators, whitespace, line terminators and
  lone surrogates; charged for input and output. Probe `the-regexp-escape.js` (36 cases) agrees with
  Node, including round trips after `\c`, `\0`, `\1`, `\x` and `{`. Pinned Test262 RegExp/escape: 0
  -> 38 of 40 (cross-realm needs `$262.createRealm`). Evidence: VM
  `docs/evidence/jseal-f10/README.md`.
- **Owner / prerequisites:** VM regex built-ins; none; Unicode database work is not a blanket dependency.
- **Work:** Implement the pinned specification's escaping rules for leading characters, regex syntax,
  punctuators, whitespace, lone surrogates, and ordinary characters. Reuse deterministic formatting
  helpers without relying on an unrelated platform regex-escape function.
- **Accept:** Generated text round-trips as literal matching text, including when embedded next to
  escape sequences. Receiver/argument validation and focused Test262 cases pass.
- **Exclude:** Unicode property matching and a general regex parser rewrite.

## F11 — Establish the Iterator global and prototype hierarchy

- **Status:** Implemented in the Broiler.VM working tree; local validation only, not accepted VM
  milestone evidence, and no VM package carries it yet. Array (shared by typed arrays), String, Map,
  Set and RegExp String iterators each have their own prototype inheriting `%Iterator.prototype%`,
  with kind-checking `next` and their own tags. `Iterator` is abstract but subclassable;
  `constructor` and `@@toStringTag` are the specified accessors; `Iterator.from` uses
  GetIteratorFlattenable and `%WrapForValidIteratorPrototype%`. `Iterator.concat`, which the pinned
  edition places on the same constructor, was added too. Pinned Test262 test/built-ins/Iterator: 14
  -> 830 of 936 variants (2 need `$262.createRealm`; 84 are proposal-only and skipped);
  iterator-prototype spot checks 198 -> 244 with nothing newly failing. Evidence: VM
  `docs/evidence/jseal-f11-f15/README.md`.
- **Owner / prerequisites:** VM iterator built-ins; audit existing per-kind iterator prototypes.
- **Work:** Define the shared iterator prototype, distinguish array/string/map/set iterator
  prototypes where required, and add the correct Iterator construction/from behavior. Preserve
  existing iteration and closing protocols. State supported members explicitly while helpers land.
- **Accept:** Prototype identities and inheritance match the contract; Iterator.from accepts the
  required iterator/iterable shapes; invalid inputs fail. Modifying one kind-specific prototype
  does not accidentally modify all iterator kinds.
- **Exclude:** Replacing specialized iterators with one undifferentiated object.

## F12 — Add lazy Iterator map and filter

- **Status:** Implemented with F11; local validation only. `%IteratorHelperPrototype%` is a native
  state machine with generator-like states and a reentrancy TypeError; `map` and `filter` are lazy,
  pass an index, and close the source when a callback throws. Probe `the-iterator-helpers.js` cases
  25-44 and the Test262 map/filter directories.
- **Owner / prerequisites:** VM iterator built-ins; F11.
- **Work:** Implement shared helper state for lazy stepping, callback/index handling, completion,
  return, and abrupt completion. Add map/filter over that state without eagerly materializing input.
- **Accept:** No source step before consumption; correct callback count/index; reentry handling;
  iterator closing and exception precedence on callback failure; return propagation; finite
  consumption of an unbounded iterator under the runtime's limits.
- **Exclude:** Async iterator helpers and array-backed simulations of laziness.

## F13 — Add lazy Iterator take and drop

- **Status:** Implemented with F11; local validation only. `take`/`drop` convert the limit before
  reading `next`, reject NaN and negative limits with RangeError (closing the receiver), keep
  Infinity, close at the limit without another `next`, and charge each skipped element. Probe cases
  45-60.
- **Owner / prerequisites:** VM iterator built-ins; F12's helper state.
- **Work:** Add limit conversion and bounded skipping/consumption, including zero and infinity where
  specified. Preserve close behavior when take exhausts its limit and avoid unnecessary next calls.
- **Accept:** Negative/NaN/zero/fractional/infinite limit cases match the pinned algorithm; take closes
  at the correct point; drop remains metered for large skips; helper chains preserve laziness.
- **Exclude:** An unbounded host-side loop that bypasses VM fuel/cancellation.

## F14 — Add lazy Iterator flatMap

- **Status:** Implemented with F11; local validation only. `flatMap` rejects mapper strings, closes
  the outer iterator when an inner step fails, and on `return` closes inner then outer with the
  inner close's error taking precedence. Probe cases 61-74.
- **Owner / prerequisites:** VM iterator built-ins; F12.
- **Work:** Extend helper state to the outer and current inner iterator. Apply the specified mapper
  result validation and close both iterators in the proper order under failure or early return.
- **Accept:** Empty and nested iterators, mapper errors, inner-step errors, early return, and close
  exceptions have deterministic state and correct precedence. Repeated completion stays completed.
- **Exclude:** Recursive eager flattening or arbitrary-depth Array.flat semantics.

## F15 — Add terminal iterator helpers

- **Status:** Implemented with F11; local validation only. `reduce`, `toArray`, `forEach`, `some`,
  `every` and `find` share the step/close primitives with the specified short-circuit and callback
  error behaviour. `toArray` is bounded by fuel per step; the realm has no separate allocated-bytes
  accounting for Array growth, so that part of the acceptance line is met through fuel only. Probe
  cases 75-99.
- **Owner / prerequisites:** VM iterator built-ins; F11-F12.
- **Work:** Add toArray, forEach, reduce, some, every, and find using common iterator-step/close
  primitives. Keep callback and short-circuit semantics explicit; split the method checklist into
  additional slices if the common implementation does not keep the patch small.
- **Accept:** Empty inputs, missing reduce initial value, callback index, short-circuit closure,
  thrown callbacks, and large bounded inputs pass. toArray observes array allocation limits.
- **Exclude:** Array methods that merely happen to share names and async helpers.

## F16 — Add Array.fromAsync

- **Status:** Implemented in the Broiler.VM working tree; local validation only. `Array.fromAsync` is
  a built-in async function chaining continuations over the realm's AwaitOn and job queue (no
  blocking waits): async iterator, then sync iterator through CreateAsyncFromSyncIterator, then
  array-like; values and mapper results awaited in order; the receiver honoured through
  `Construct`; every failure a rejection; iterators closed on mapper or definition failure. Probe
  `the-array-from-async.js` (42 cases) agrees with Node apart from three declared cases where V8
  closes an iterator the pinned text does not. Pinned Test262 Array/fromAsync: 0 -> 146 passing; the
  whole Array subtree 5457 -> 5603 with no regressions. The ten remaining failures come from a
  separate front-end defect (a hoisted function declaration cannot see its enclosing function's
  let/const); thirty variants need BigInt. Evidence: VM `docs/evidence/jseal-f16/README.md`.
- **Owner / prerequisites:** VM array/promise built-ins; existing async iteration/job semantics audited.
- **Work:** Follow the correct preference for async iterator, sync iterator, and array-like input;
  await values and mapping results in order; honor the constructor receiver where required. Use the
  existing promise and job machinery, not blocking CLR waits.
- **Accept:** Async/sync/array-like cases, rejection, mapper ordering, early close, and constructor
  customization pass with an explicitly drained job queue. No job or iterator is lost on rejection.
- **Exclude:** Depending on the new Iterator helpers where the language does not require them.

## F17 — Add JSON.rawJSON and JSON.isRawJSON

- **Status:** Implemented in the Broiler.VM working tree; local validation only, not accepted VM
  milestone evidence, and no VM package carries it yet. `JSON.rawJSON` validates its text
  (non-empty, allowed first/last code units, a JSON primitive) and returns a frozen null-prototype
  object; the brand is a private runtime type, so `JSON.isRawJSON` cannot be forged by lookalikes,
  copies or Proxies. `JSON.stringify` emits raw text after `toJSON` and replacer processing, nested
  and with array replacers and gaps. Probe `the-raw-json-values.js` (69 cases) agrees with Node.
  Pinned Test262 JSON: 260 -> 290 passing, no regressions. Reviver source-text context remains
  excluded; since B05 admitted BigInt, the BigInt raw-JSON case is scored and fails for that
  excluded reason. Evidence: VM `docs/evidence/jseal-f17/README.md`.
- **Owner / prerequisites:** VM JSON built-ins; none.
- **Work:** Introduce a branded raw-JSON value with the exact permitted input validation and integrate
  it into stringify. Implement isRawJSON as a brand operation. Keep normal parsing/stringification
  behavior and replacer ordering intact.
- **Accept:** Valid raw primitives serialize as raw text; malformed or disallowed input is rejected;
  a lookalike object cannot forge the brand. Cover replacers, nesting, and immutable raw payload.
- **Exclude:** JSON reviver source-text context, which was not independently verified in this review.

## F18 — Add disposal symbols and SuppressedError

- **Status:** Implemented in the Broiler.VM working tree; local validation only, not accepted VM
  milestone evidence, and no VM package carries it yet. `Symbol.dispose`/`Symbol.asyncDispose` and
  `SuppressedError` exist with the specified descriptors and payload identity. **Edition note:** the
  pinned ES2026 edition does not contain explicit resource management and the harness skips it as a
  proposal, so the oracle was the pinned Test262 subtree run explicitly (SuppressedError 42/44, each
  Symbol 4/6; the remainder need `$262.createRealm`). Publishing proposal-stage globals ahead of the
  pinned edition was done without a decision record admitting them; a JSD is owed before these are
  advertised. Evidence: VM `docs/evidence/jseal-f18-f20/README.md`.
- **Owner / prerequisites:** VM symbol/error built-ins; none.
- **Work:** Add the required well-known symbols and SuppressedError constructor/prototype/descriptors.
  Reuse ordinary error creation and preserve error/suppressed payload identity.
- **Accept:** Symbol identity is stable within the intended realm/agent model; descriptors and
  constructor behavior match the pinned specification; arbitrary payloads survive unchanged.
- **Exclude:** Claiming resource-management syntax or stack APIs are implemented by these primitives.

## F19 — Implement DisposableStack

- **Status:** Implemented with F18; local validation only, same edition note. DisposableStack has
  its full method set; disposal methods are captured at registration, entries unwind LIFO, multiple
  throws form nested SuppressedError chains, a disposed stack refuses registration and `move`, and
  entries are detached before any runs so re-entry cannot double-execute. Unwinding is charged;
  exhaustion and cancellation are never caught. Explicit Test262 run: 184/186 (the rest need a nested
  realm). Probe `the-disposal-stacks.js` agrees with Node except one declared divergence.
- **Owner / prerequisites:** VM built-ins; F18.
- **Work:** Implement synchronous use/adopt/defer/move/dispose operations and disposed state. Capture
  disposal methods at the specified time and unwind in reverse order, combining errors correctly.
- **Accept:** LIFO order, move ownership, disposal twice, registration after disposal, throwing
  disposers, and suppressed-error chains pass. Unwinding remains metered and reentry does not double
  execute an entry. Publish the type only with the complete method set.
- **Exclude:** Async disposal and parser changes.

## F20 — Implement AsyncDisposableStack

- **Status:** Implemented with F18-F19; local validation only, same edition note.
  AsyncDisposableStack's `disposeAsync` always returns a promise, falls back to `Symbol.dispose`
  per the proposal's await rules, and awaits only through the realm's promise job queue. Explicit
  Test262 run: AsyncDisposableStack 206/208 and AsyncIteratorPrototype `@@asyncDispose` 18/18.
  `Iterator.prototype[Symbol.dispose]` is installed; its Test262 rows should be re-run now that F11
  supplies `%ArrayIteratorPrototype%`.
- **Owner / prerequisites:** VM built-ins; F18-F19's shared ownership rules and existing promises/jobs.
- **Work:** Implement asynchronous disposal with the correct async/sync method fallback, serialized
  awaiting, move semantics, and error aggregation. Keep all guest calls within normal VM turns.
- **Accept:** Mixed sync/async entries dispose in order; rejected disposers form the right error chain;
  repeated calls observe the specified completion; no blocking waits or CLR-finalizer guest execution.
- **Exclude:** `await using` parsing/lowering, which is F22.

## F21 — Add synchronous using declarations

- **Status:** Implemented in the Broiler.VM working tree under proposed VM record JSD-0034 (explicit
  resource management admitted ahead of the pinned ES2026 edition, proposal revision `38c13295`; the
  harness scores the `explicit-resource-management` feature). `using` parses where the proposal
  allows and is refused early elsewhere; each declaring statement list (catch bodies included) gets
  a hidden disposal scope disposed on every exit, combining errors through SuppressedError; opcodes
  0xA0-0xA4 with verifier rules, native entry points and corpus entries. A follow-up fixed the
  inherited try/finally lowering so inlined unwinding (finalisers, iterator closes, disposal) is no
  longer covered by the regions it leaves - a `finally` now runs at most once - and reserved `await`
  in class static blocks. Test262 statements/using 152 of 152; test/language plus test/built-ins
  73363 -> 73391 with no regressions. Evidence: VM `docs/evidence/jseal-f21-f22/README.md`,
  `jseal-finally-lowering/README.md`.
- **Owner / prerequisites:** VM compiler/runtime; F18-F19 primitives.
- **Work:** Add grammar/static semantics and lowering to resource scopes with cleanup on normal exit,
  return, throw, break, and continue. Capture completion values so cleanup failure combines with
  an existing abrupt completion correctly. Handle only the explicitly supported parse goals first.
- **Accept:** Nested scopes, loops, destructuring restrictions, initializer failure, and abrupt exits
  pass focused parser/runtime tests. Source locations remain meaningful. Artifact/verifier support
  is updated if lowering introduces new instructions.
- **Exclude:** Parsing `using` while lowering it as an ordinary variable declaration.

## F22 — Add await using declarations

- **Status:** Implemented with F21; local validation only. `await using` is accepted only where
  `await` is an operator (async functions and generators, module top level, `for await`/`await
  using` heads) and suspends through the ordinary Await machinery; module-level resources dispose
  before the module's evaluation settles. No JSeal module capability is claimed (I13).
- **Owner / prerequisites:** VM compiler/runtime; F20-F21 and existing async suspension machinery.
- **Work:** Extend resource scopes to suspend/resume during asynchronous disposal, with correct
  parse-goal restrictions and completion propagation. Include module contexts only where the
  engine's existing module implementation provides the required async evaluation semantics.
- **Accept:** Cleanup completes before the async function/module settles; nested asynchronous
  cleanup preserves order and exceptions; cancellation/budget failures follow a documented VM
  policy without running unmetered cleanup. Invalid contexts are rejected during compilation.
- **Exclude:** A JSeal module capability claim before I13.

## B01 — Design and add an internal BigInt value representation

- **Status:** Implemented behind a gate in the Broiler.VM working tree; local validation only, design
  in proposed VM record JSD-0033. BigInt is an internal `JsBigInt` over the BCL `BigInteger` in the
  unchanged 24-byte value (2^20-bit ceiling, charges recorded, formatting cost measured for B03).
  Every exhaustive value-kind switch handles a BigInt or refuses it by name; nothing converts one to
  a Number. Both gate halves are closed to public callers (every public descriptor door refuses the
  `broiler.javascript.bigint` surface). Evidence: VM `docs/evidence/jseal-b01-b02/README.md`.
- **Owner / prerequisites:** VM value/runtime/compiler owners; explicit manifest and compatibility design.
- **Work:** Choose an allowed representation compatible with the profile's portability/AOT rules,
  value layout, budgets, and artifact versions. Add internal storage/type discrimination and define
  allocation and operation-cost accounting. Audit all exhaustive value-kind switches.
- **Accept:** Design review covers serialization boundaries, host values, arithmetic limits,
  comparisons, formatting, and type mixing. Internal values retain exact integers beyond Number
  precision. Existing artifacts are handled by an explicit version policy.
- **Exclude:** Public BigInt admission, silent Number conversion, and assuming a library is permitted.

## B02 — Parse and lower exact BigInt literals behind the feature gate

- **Status:** Implemented behind the gate with B01; local validation only. Malformed spellings are
  refused at the literal's position with or without the gate; the internal gate lowers exact
  literals in every radix to a canonical constant (at most 8,192 bytes); the verifier admits it only
  beside the unadvertised surface; the wide manifest keeps its named refusal, so the retained
  unsupported-BigInt floor is unchanged.
- **Owner / prerequisites:** VM compiler/verifier; B01.
- **Work:** Parse supported literal radices directly into an exact representation; validate separators
  and invalid forms. Introduce the required constant/artifact representation and verification bounds.
  Preserve the existing named refusal for manifests that do not admit BigInt.
- **Accept:** Values above 2^53 survive compile/verify/execute exactly in the gated path; malformed
  literals fail at source positions; oversized constants are bounded; older manifests still refuse.
- **Exclude:** Publishing an incomplete BigInt global or permitting malformed artifacts.

## B03 — Implement BigInt arithmetic

- **Status:** Implemented behind the closed gate in the Broiler.VM working tree (JSD-0033 amended);
  local validation only. Arithmetic, unary minus, increment/decrement and compound assignment are
  exact and signed on BigInts; mixed Number/BigInt is a TypeError; a zero divisor, a negative
  exponent and results past 2^20 bits are RangeErrors, refused before allocating when the operand
  widths prove it. Work is charged per word with a size-squared term for multiplication, division,
  powers and formatting; formatting is divide-and-conquer with cancellation observed between steps.
  Computed values are observably equal by value. The wide manifest keeps its named refusal and no
  global exists. Open review points: computed BigInts are not reported to LiveBytes, and the ceiling
  edge of the gated increment lowering. Evidence: VM `docs/evidence/jseal-b03-b04/README.md`.
- **Owner / prerequisites:** VM numeric runtime; B01-B02.
- **Work:** Implement addition/subtraction, multiplication, division/remainder, and exponentiation
  with explicit mixed Number/BigInt rejection and work charging tied to operand/result size. Split
  multiplication/exponentiation into further slices if their resource model cannot be reviewed
  together. Keep the public feature gated until the complete numeric surface is ready.
- **Accept:** Exact signed arithmetic; truncating division/remainder rules; divide-by-zero and invalid
  exponent errors; large-result refusal; bounded cancellation; no lossy double intermediates.
- **Exclude:** Unmetered arbitrarily large operations and implicit mixed-type conversion.

## B04 — Implement BigInt bitwise and shift operations

- **Status:** Implemented behind the gate with B03; local validation only. `& | ^ ~ << >>` are
  two's-complement at any width, negative shift counts reverse direction, `>>>`, unary `+` and mixed
  operands are TypeErrors, and oversized left shifts are RangeErrors before allocating. `asIntN`/
  `asUintN` exist as internal operations; the BigInt global that exposes them is B05. The compiler
  does no constant folding, so there is nothing to keep consistent there.
- **Owner / prerequisites:** VM numeric runtime; B01-B03.
- **Work:** Implement bitwise operations, shifts, unary operations, and asIntN/asUintN with the
  specified signed behavior and operation-size limits. Preserve unsupported unsigned right shift
  and unary-plus behavior as the required errors.
- **Accept:** Negative values, sign extension, zero/large widths, shift direction, invalid Number
  mixing, and bounded oversized operations pass. Audit compiler constant folding for consistency.
- **Exclude:** Reusing fixed-width Number bitwise conversions.

## B05 — Complete BigInt conversion, comparison, and public admission

- **Status:** Implemented and admitted in the Broiler.VM working tree (JSD-0033 section 7, proposed and
  unsigned; local validation only). The wide manifest now lowers BigInt literals through an optional,
  advertised surface `broiler.javascript.bigint` that the full descriptor admits and a composition
  can decline (`JavaScriptProfile.BigIntManifest`); the numeric manifest keeps its named refusal. The
  `BigInt` global (NumberToBigInt, ToBigInt, StringToBigInt, `asIntN`/`asUintN`, `toString(radix)`,
  `toLocaleString`, `valueOf`, `@@toStringTag`), BigInt objects, exact mixed `==` and relational
  comparison, `Number(x)` rounded to nearest-even, and JSON's `toJSON`/TypeError exist; update
  expressions use three new opcodes (0xB0-0xB2). The wide Test262 floor's unsupported row was re-based
  by hand from 1990 to 0 with a written reason, and a whole-suite run found no previously passing
  variant failing. Still absent: BigInt64Array/BigUint64Array (B07), the DataView accessors (B08),
  and the host crossing and clone (B06). **JSeal impact:** the VM provider admits every surface, so
  its next VM pin exposes BigInt and JSeal's per-engine BigInt table and marshaling (B06) must move
  with it. Evidence: VM `docs/evidence/jseal-b05/README.md`.
- **Owner / prerequisites:** VM built-ins/runtime; B01-B04.
- **Work:** Add BigInt conversion/global/prototype behavior, string parsing/formatting, truthiness,
  equality, relational comparisons, and JSON rejection. Check the complete language-operation
  inventory, then enable the feature only for an explicit supported manifest.
- **Accept:** 0n is false, equal mathematical BigInts compare equal, mixed comparisons retain exactness,
  Number conversions are only allowed by the right operations, Symbol/error cases are correct, and
  the selected pinned BigInt tests pass. Update absent-feature records and format compatibility docs.
- **Exclude:** Describing a named refusal as BigInt support or enabling the global before operators work.

## B06 — Carry BigInt through the VM host API and JSeal

- **Status:** Partial: the VM half is implemented in the Broiler.VM working tree (JSD-0024 section
  19, the JSD-0032 section 5 matrix with carrier layout 2, notes in JSD-0033; all proposed and
  unsigned; local validation only); the JSeal half waits for the VM pin that carries it. The host
  surface gains `JsHostValueKind.BigInt`, `JsHostValue.BigInt(BigInteger)`,
  `JsHostValue.AsBigInt()`, `JsHostRealm.ToBoolean` and the refusal `SurfaceDeclined`, recorded in
  the API baseline. Exact values cross properties, callbacks, `Invoke`/`Construct` and promise
  settlement both ways, charged per word; a host value past the realm's 2^20-bit ceiling is a
  RangeError before allocation, and a realm whose composition declined the BigInt surface refuses
  one. `0n` is false; host values compare BigInts by mathematical value and never equal to a Number
  (Strings still compare by reference, now documented). A thrown BigInt reaches the host as itself.
  The clone carrier holds BigInt primitives and objects, and a declining destination refuses such a
  carrier before claiming moved bytes. The same change made `Array.prototype.toString` fall back to
  the intrinsic `%Object.prototype.toString%`, gave `Object.prototype.toString` the specification's
  builtinTag set and installed missing `Symbol.toStringTag` properties. Host-surface lane 65 -> 73
  rows, pinned Test262 selections +64 and +20 variants with none newly failing. **JSeal half
  (prepared, merged where it does not need the new VM):** `VmMarshal.Wrap` carries a VM BigInt as a
  BigInt handle, `Unwrap` accepts only this provider's BigInt handles and refuses any other with the
  existing foreign-engine exception (Broiler.JS now does the same instead of raising an
  InvalidCastException), and `VmRealm.ToBoolean` answers `0n` as false. The contract
  (docs/jseal.md): `JsValue` `==`/`Equals` keep comparing BigInt handles by reference (true means
  the same value, false means nothing), truthiness is `IJsValues.ToBoolean`, and value equality is
  the new `IJsValues.IsStrictlyEqual` - ECMAScript `===`, running no page script, with `Missing`
  taken as undefined - a source-compatible default interface member both providers implement (the
  J15 comparison shows it as the only contracts addition). Provider-neutral cases driven by a
  per-engine table (the value kind, BigInt64Array/BigUint64Array, the DataView accessors) cover
  exact crossing through properties, callbacks and `Invoke` with 2^200-scale values, zero BigInts
  false, value equality, foreign refusal, the typed arrays, DataView and clone. The contract, the
  Broiler.JS half and those cases (with the VM row stated false for the pinned `0.1.0-preview.3`)
  are merged; the VM half is in `jseal-vm-next3-over-normal.patch`, validated against local
  candidate `0.1.0-preview.4.local.7`. Broiler.JS `0.1.0-preview.1` clones a BigInt object as a
  plain object (recorded gap). Evidence: VM `docs/evidence/jseal-b06/README.md`.
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

- **Status:** Implemented in the Broiler.VM working tree (JSD-0033 section 8, proposed and unsigned;
  local validation only). `BigInt64Array` and `BigUint64Array` exist wherever a composition admits
  both the binary and the BigInt surfaces; naming either declares both, so a composition declining
  one refuses the program at verification. Elements convert by content type (ToBigInt, a Number is
  a TypeError), store modulo 2^64 and charge the narrowing of wider values; every `%TypedArray%`
  method works on them, mixing content types in `set`, construction or species results is a
  TypeError, and `sort` compares exactly. A review found that a `JSON.parse` reviver could store a
  Number into a BigInt element and end the invocation; the reviver now stores through
  CreateDataProperty, which also stops it overwriting a frozen holder's property. Three
  specification-correct changes also reach the Number kinds: `%TypedArray%.of` constructs through
  its receiver, `%TypedArray%.prototype[Symbol.toStringTag]` exists, and class fields on typed-array
  subclasses go through [[DefineOwnProperty]]. Pinned Test262: the TypedArray BigInt subtrees 2 ->
  952 of 958 variants, the BigInt TypedArrayConstructors subtrees 2 -> 497 of 573 (the rest need
  `$262.createRealm` or SharedArrayBuffer), and the 149 F04-F06 resizable-buffer files pass
  unshimmed; no variant regressed. Views clone by constructor name. **JSeal impact:** the next VM pin
  exposes both constructors; JSeal's per-engine typed-array and BigInt tables move with B06's JSeal
  half. Evidence: VM `docs/evidence/jseal-b07-b08/README.md`.
- **Owner / prerequisites:** VM binary built-ins; B05, V10-V11; integrate F05-F06 if already landed.
- **Work:** Add signed/unsigned 64-bit element conversions, constructors, views, indexing, iteration,
  generic typed-array methods, and content-type checks across typed-array operations.
- **Accept:** Modulo truncation, signedness, Number/BigInt mixing errors, copying, species, detachment,
  and shared-buffer aliasing within one ordinary buffer are covered. Existing Number arrays remain
  behaviorally unchanged.
- **Exclude:** SharedArrayBuffer or treating BigInt arrays as Float64 arrays.

## B08 — Add DataView BigInt accessors

- **Status:** Implemented with B07 (same record and evidence; local validation only).
  `getBigInt64`, `getBigUint64`, `setBigInt64` and `setBigUint64` share the path of the other
  DataView accessors: ToIndex, ToBigInt for a setter, littleEndian, then the detached/out-of-bounds
  TypeError and the range check. Pinned Test262: the BigInt DataView tests 34 -> 134 of 136
  variants; the other two need the immutable-arraybuffer feature and are skipped. The differential
  probe covers both byte orders, unaligned offsets, signed boundaries, truncation, wrong value types
  and detachment or resizing during coercion.
- **Owner / prerequisites:** VM binary built-ins; B05 and the shared binary bounds/coercion helpers.
- **Work:** Add getBigInt64/getBigUint64/setBigInt64/setBigUint64 with endian conversion, bounds,
  detached/out-of-bounds handling, and observable argument conversion order.
- **Accept:** Both endian modes, unaligned offsets, signed boundaries, overflow truncation, wrong
  value types, and detachment/resizing during coercion pass the relevant tests.
- **Exclude:** Requiring typed-array constructors when DataView can use the shared primitives directly.

## D01 — Decide the Intl scope and data strategy

- **Status:** Decided as proposed VM record JSD-0027
  (`src/Broiler.VM.Profile.JavaScript/docs/decisions/0027-intl-scope-and-data-strategy.md`); owner
  decisions pending on the data source, licence, size budget and time-zone data. Intl stays
  **deferred and absent** with an explicit consumer limitation; no empty or partial Intl object will
  be installed. The locale-named methods' actual fallbacks are documented from probes. Three
  ECMA-262 fallback defects are split out: N1 (an own Array.prototype.toLocaleString), N2 (full
  default case mapping, after F07) and N3 (canonical equivalence in localeCompare, after F08).
  Collator, NumberFormat and DateTimeFormat are bounded consumer-triggered slices with their own
  datasets; JSeal currently has no Intl consumer.
- **Owner / prerequisites:** VM roadmap/API/data owners; no implementation dependency for urgent work.
- **Work:** Produce a concrete proposal for Intl scope, locale/time-zone data, versioning, portability,
  package size, and supported fallbacks. Break an accepted implementation into separate Collator,
  NumberFormat, DateTimeFormat, and later-formatting slices with their own acceptance datasets.
- **Accept:** The decision says either deferred with an explicit consumer limitation, or scheduled
  with bounded first APIs and data dependencies. Existing locale-named methods' fallback behavior is
  accurately documented. No empty Intl object is presented as implementation.
- **Exclude:** Committing to a full internationalization subsystem through one roadmap checkbox.

## D02 — Decide whether shared memory belongs in the VM profile

- **Status:** Decided as proposed VM record JSD-0028
  (`src/Broiler.VM.Profile.JavaScript/docs/decisions/0028-shared-memory-and-atomics.md`); owner
  decision pending. SharedArrayBuffer and Atomics stay excluded as a pair. The record lists where
  the exclusion is recorded, works through agent ownership, once-only accounting, tearing and data
  races, wait lifetime and cancellation, embedding policy and the I14-I18 clone carrier, and states
  what would reopen it. Staged follow-ups S1-S5 carry acceptance criteria but are not scheduled;
  I14-I18 proceed without shared memory.
- **Owner / prerequisites:** VM architecture/agent model owners; no prerequisite for ordinary buffers.
- **Work:** Assess SharedArrayBuffer and Atomics against agent/thread ownership, shared accounting,
  synchronization, wait/wake, cancellation, and embedding policy. Write a concrete design and staged
  follow-up only if the deliberate exclusion is changed.
- **Accept:** The decision retains the exclusion explicitly or defines a supported agent model and
  separately scoped implementation gates. Data races and wait lifetime are addressed before exposing
  APIs. Existing feature inventories remain honest.
- **Exclude:** Adding the globals solely to match a typeof comparison.

## D03 — Decide a host-drained FinalizationRegistry cleanup model

- **Status:** Proposed as VM record JSD-0029
  (`src/Broiler.VM.Profile.JavaScript/docs/decisions/0029-finalization-registry-cleanup-model.md`);
  owner decision pending. The current registry is documented from code and probes: registrations are
  checked and stored, callbacks never run and dead records are never removed. The optional model
  sweeps liveness only at the host's explicit drain entry points, runs cleanup as ordinary metered
  jobs and never from a CLR finalizer, and uses a deterministic eligibility seam for tests. The
  default stays inert; implementation is follow-up D03-a, with D03-b (cleanupSome) and D03-c (symbol
  targets) separate.
- **Owner / prerequisites:** VM GC/job ownership; audit existing weak-reference semantics.
- **Work:** Document the current intentionally inert registry and design optional cleanup at a
  metered host drain point, with target liveness, held values, cancellation, exceptions, and disposal
  considered. Use deterministic test seams for eligibility; actual GC timing is not a test oracle.
- **Accept:** A proposal identifies where callbacks are queued/run and what remains implementation
  dependent, or records continued deferral. Any later implementation has a separate slice and cannot
  execute guest code directly from CLR finalizers.
- **Exclude:** Promising prompt or guaranteed garbage collection or calling callbacks from a finalizer.

## D04 — Decide the ShadowRealm support boundary

- **Status:** Decided as proposed VM record JSD-0030
  (`src/Broiler.VM.Profile.JavaScript/docs/decisions/0030-shadowrealm-support-boundary.md`); owner
  decision pending. ShadowRealm stays undefined with no stub; the proposal revision (Stage 2.7) and
  the pinned Test262 cases are recorded. The boundary for a later implementation is fixed:
  whole-realm isolation, only primitives and callables cross via fresh wrappers, and a child realm
  inherits the parent's compilation policy on every route, so a restricted parent cannot regain
  compilation. `importValue` waits for I09-I13. Follow-ups SR-1 to SR-7 are bounded; SR-6 records a
  Broiler.JS finding that child-realm compilation bypasses a selective EvalEvent handler.
- **Owner / prerequisites:** VM realm/module owners; coordinate with J04 and I09-I13 as applicable.
- **Work:** Pin the intended proposal/specification version and define realm isolation, callable
  wrapping, allowed cross-realm values, inherited compilation policy, lifetime, and optional module
  support. Distinguish this API from browser workers and structured-clone transport.
- **Accept:** A decision includes evaluation and policy-isolation examples plus bounded implementation
  follow-ups, or explicitly defers the feature. A restricted parent cannot regain dynamic compilation
  simply by constructing a child realm.
- **Exclude:** Reusing one global object and presenting it as realm isolation.
