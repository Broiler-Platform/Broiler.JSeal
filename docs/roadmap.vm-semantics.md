# Broiler.VM semantic correctness slices

[Roadmap index](roadmap.md). All slices are **not started**. The owner is the VM JavaScript profile
unless stated otherwise. These slices refine existing VM parity stages: V01 relates to JSP-4;
V02-V03 to JSP-5; V04-V12 to JSP-6; V13-V15 to JSP-3/JSP-10 and JSW-3. They do not mark those
stages complete.

The review confirmed V01-V04, the basic arguments-aliasing failure, array species failure, and
concat-spreadability failure on current-source builds. Companion methods and edge cases below are
planned audits/tests, not claims that every listed variation was reproduced during the review.
Correctness follows the pinned language specification and Test262 subset, not engine-to-engine
agreement alone. Keep instruction charging, cancellation, allocation accounting, and guest exception
handling intact in every new path.

## V01 — Coerce numeric operands in source order

- **Priority / prerequisites:** P1; none.
- **Work:** In `JsEngine.Binary`, pop both operands before coercing either, then coerce left before
  right. Audit other shared numeric helpers for the same reversal. Preserve operand order for the
  arithmetic operation itself. Use the existing interpreter/native dispatch structure.
- **Accept:** Subtraction with logging `valueOf` methods records `ab`, not `ba`. Test multiplication,
  division, remainder, and exponentiation paths sharing the helper; a left-side throw prevents right
  coercion. Run the corresponding cases on each supported execution form that uses the changed path.
- **Exclude:** Rewriting numeric representation or adding BigInt.

## V02 — Respect non-extensibility when creating symbol properties

- **Priority / prerequisites:** P1; none.
- **Work:** Route symbol-key creation through the same extensibility decision as string keys.
  Cover assignment, Object.defineProperty, and Reflect.defineProperty while preserving their
  distinct throw/boolean behavior. Do not put language-specific rejection into a storage helper
  that internal initialization legitimately uses unless its callers are audited.
- **Accept:** A frozen, sealed, or merely non-extensible object never gains a new symbol key.
  Strict assignment throws, sloppy assignment does not create a property, Reflect reports failure,
  and Object.defineProperty throws. Existing writable symbol properties remain writable on a
  non-extensible object when their descriptors allow it.
- **Exclude:** A blanket ban on every symbol write and unrelated private-field behavior.

## V03 — Apply descriptor integrity rules to existing symbol properties

- **Priority / prerequisites:** P2; V02.
- **Work:** Audit freeze/seal processing and symbol descriptor replacement. Apply non-writable and
  non-configurable rules consistently across string/symbol paths; factor descriptor validation only
  after matching behavior is established.
- **Accept:** Frozen symbol values cannot change. A non-configurable property cannot be deleted or
  converted between incompatible data/accessor descriptors. Valid SameValue redefinitions still
  succeed. Proxy-mediated defines retain invariant checks.
- **Exclude:** General property-storage redesign and unmeasured performance work.

## V04 — Implement RegExp guards in string search methods

- **Priority / prerequisites:** P2; none.
- **Work:** Use the language's IsRegExp operation before string conversion in startsWith, endsWith,
  and includes. Honor Symbol.match overrides and the specified observable getter/coercion order.
  Audit replaceAll's non-global-RegExp rejection separately within this slice; split if its dispatch
  implementation needs broader work.
- **Accept:** `'abc'.startsWith(/a/)` throws TypeError. A regex with Symbol.match=false may be
  treated as a string; an ordinary object with Symbol.match=true is rejected. Verify throwing hooks,
  receiver conversion order, and the equivalent methods' focused Test262 cases.
- **Exclude:** Unicode matcher support, RegExp.escape, or a regex-engine rewrite.

## V05 — Implement mapped arguments for simple sloppy functions

- **Priority / prerequisites:** P2; none.
- **Work:** Add the parameter map connecting eligible arguments indices to parameter bindings.
  Handle duplicate formal names according to their mapping rules. Keep strict functions, arrow
  functions, and functions with non-simple parameters on their appropriate unmapped paths.
- **Accept:** Updating arguments[0] changes the mapped parameter, and updating the parameter changes
  arguments[0]. Cover missing actual arguments, duplicate formals, strict mode, defaults, rest, and
  destructuring. Preserve callback reentry and closure visibility.
- **Exclude:** Direct eval and descriptor operations completed by V06. Do not claim complete mapped
  arguments conformance until V06 is done.

## V06 — Complete mapped-arguments descriptors and disconnection

- **Priority / prerequisites:** P2; V05.
- **Work:** Disconnect mappings when an indexed argument is deleted or redefined in ways that require
  it; implement GetOwnProperty/DefineOwnProperty behavior for mapped values. Respect accessor
  descriptors, writability transitions, and failed redefinitions.
- **Accept:** Deletion breaks aliasing; a successful accessor replacement breaks it; making the
  relevant index non-writable produces the specified final parameter/value relationship. An invalid
  descriptor does not partially mutate the map. Focused arguments-object tests pass.
- **Exclude:** Exposing the internal parameter map through the host API.

## V07 — Add ArraySpeciesCreate for map and filter

- **Priority / prerequisites:** P2; none.
- **Work:** Introduce a shared species-construction operation with correct constructor/species lookup,
  null/undefined fallback, constructability checks, and length handling. Apply it first to map/filter.
  Reuse the realm's existing construction machinery so metering and guest errors remain intact.
- **Accept:** Mapping an Array subclass preserves its default species. A custom species constructor
  is called with the correct length; a throwing species getter runs once and propagates; invalid
  species fails. Test plain array-like receivers and holes. Capture cross-realm cases if the harness
  can create them; do not silently omit them from coverage reporting.
- **Exclude:** Giving change-by-copy methods species behavior the specification does not require.

## V08 — Apply array species to the remaining allocating methods

- **Priority / prerequisites:** P2; V07.
- **Work:** Migrate slice/splice, then concat, then flat/flatMap to the shared operation. Treat these
  method families as separate PRs if index/coercion logic expands the patch. Retain method-specific
  length, hole, and property-creation behavior.
- **Accept:** Each method is verified with default/custom/throwing species and sparse receivers;
  observable construction order matches its algorithm. Change-by-copy methods still return the
  required plain arrays. The per-method checklist is complete before closing the slice.
- **Exclude:** Concat-spreadability, which has its own independent tests in V09.

## V09 — Honor Symbol.isConcatSpreadable

- **Priority / prerequisites:** P2; V07 for coherent result construction; may be developed independently.
- **Work:** Implement IsConcatSpreadable independently from the receiver/result species choice.
  Consult the symbol for both array and non-array inputs, falling back to IsArray only when needed.
  Preserve hole and length behavior without an unmetered scan.
- **Accept:** A flagged array-like object spreads; an array flagged false remains one element.
  A throwing getter propagates, each lookup occurs in order, and sparse positions remain holes.
  Test inherited and Proxy-provided hooks.
- **Exclude:** Changing concat output by applying an unconditional iterable protocol.

## V10 — Respect typed-array species in map and filter

- **Priority / prerequisites:** P2; V07's protocol design, not its Array-specific implementation.
- **Work:** Implement typed-array-specific species construction and result validation. Cover
  map/filter, element conversion, required result capacity, and invalid constructors. Keep future
  BigInt/Number content-type checks explicit rather than assuming all views contain Numbers.
- **Accept:** Custom typed-array species is observed; invalid or undersized results fail correctly;
  detached inputs and throwing getters follow the specified ordering. No unrelated element kind is
  accepted merely because it shares a CLR array representation.
- **Exclude:** Float16 or BigInt typed-array support and resizable views.

## V11 — Respect typed-array species in slice and subarray

- **Priority / prerequisites:** P2; V10.
- **Work:** Use the correct species algorithms for copying versus creating a shared-buffer view.
  Preserve byte offsets and lengths and revalidate detachment at the specified points. Do not treat
  subarray as slice with a different name.
- **Accept:** Species constructor arguments, shared-buffer aliasing for subarray, independent bytes
  for slice, incompatible results, and detachment during observable hooks are covered. Existing
  JSeal buffer reads remain correct while they still use these guest operations.
- **Exclude:** Resizable-view semantics; F05-F06 extend the relevant checks later.

## V12 — Respect ArrayBuffer species in slice

- **Priority / prerequisites:** P2; species protocol design from V07.
- **Work:** Implement the buffer-specific species construction and output validation, including
  same-buffer/insufficient-capacity cases and detachment observed during construction.
- **Accept:** Custom and throwing species behave correctly; source/output bytes are independent;
  no bytes are copied before validation requires them to be; fixed-buffer Test262 cases pass.
- **Exclude:** SharedArrayBuffer, transfer, and resizing.

## V13 — Design a direct-eval environment boundary

- **Priority / prerequisites:** P2 design; J04 remains fixed independently.
- **Work:** Write a VM-owned design for carrying directness, caller lexical/variable environments,
  strictness, source identity, and compile permission into dynamic compilation. Include the current
  lowering-time name-resolution constraints and how eval-visible bindings are represented. Choose
  an artifact/version strategy and identify native-form compatibility needs.
- **Accept:** The design walks through local reads/writes, captured variables, nested eval, shadowed
  eval, indirect eval, strict eval isolation, and declaration conflicts. It names a bounded set of
  implementation steps and memory/fuel implications. Existing unsupported cases keep their explicit
  refusal until those steps are implemented.
- **Exclude:** Replacing the current EvalError with global eval, which would return wrong results.

## V14 — Execute eval against caller bindings

- **Priority / prerequisites:** P2; V13, V05-V06 for the arguments interaction.
- **Work:** Implement direct eval's access to caller bindings for expressions and assignment, including
  captured mutable bindings and nested invocations. Keep indirect eval global and a shadowed eval
  binding an ordinary call. Add explicit handling for any still-unsupported declaration shapes.
- **Accept:** `function f(){let x=7;return eval('x')}` returns 7; eval assignment is visible to the
  caller and its closures; nested calls do not share the wrong environment. Restricted compilation
  is refused through every route. Interpreter and supported native forms agree.
- **Exclude:** Claiming unrestricted direct eval before V15's declaration/isolation tests pass.

## V15 — Complete eval declaration and strictness semantics

- **Priority / prerequisites:** P2; V14.
- **Work:** Implement lexical isolation, strict-eval variable isolation, permitted sloppy var
  introduction, declaration conflicts, and closure access to eval-created bindings. Cover global
  direct/indirect cases as well as function cases. Remove the blanket direct-eval refusal only for
  the supported, verified surface.
- **Accept:** Eval let/const/class bindings do not leak. Strict eval var stays isolated. Sloppy eval
  var behaves as specified without overwriting conflicting lexical bindings. Cover thrown parse
  errors, delete/closure interactions, arguments, and a bounded pinned Test262 eval subset.
- **Exclude:** Eval compilation caching; optimize only after stable semantics and profiling.
