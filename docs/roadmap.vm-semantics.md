# Broiler.VM semantic correctness slices

[Roadmap index](roadmap.md). V01-V15 are **implemented** in the Broiler.VM working tree (V13 as the
proposed design record JSD-0026 that V14-V15 implement; local validation only; no VM package carries
them). Follow-up fixes VM-FIX-A to VM-FIX-J are listed at the end. The owner is the VM JavaScript profile
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

- **Status:** Implemented in the Broiler.VM working tree; local validation only, not accepted VM
  milestone evidence, and no VM package carries it yet. `JsEngine.Binary` pops both operands, then
  converts left before right; the same reversal was fixed for `& | ^ << >> >>>` and for relational
  `>`/`>=`. Compound assignment shares these opcodes, and the native baseline handlers step into the
  same switch. The new differential probe `the-numeric-operand-order.js` (44 cases) differed from
  Node in 41 cases before the fix and matches afterwards, including left-throw cases. Pinned Test262
  over 17 expression subtrees (bytecode): passing variants 1731 -> 1779, no regressions. The native
  form was not exercised: artifacts fail to instantiate on the validating machine before and after.
  Evidence: VM `docs/evidence/jseal-v01/README.md`.
- **Priority / prerequisites:** P1; none.
- **Work:** In `JsEngine.Binary`, pop both operands before coercing either, then coerce left before
  right. Audit other shared numeric helpers for the same reversal. Preserve operand order for the
  arithmetic operation itself. Use the existing interpreter/native dispatch structure.
- **Accept:** Subtraction with logging `valueOf` methods records `ab`, not `ba`. Test multiplication,
  division, remainder, and exponentiation paths sharing the helper; a left-side throw prevents right
  coercion. Run the corresponding cases on each supported execution form that uses the changed path.
- **Exclude:** Rewriting numeric representation or adding BigInt.

## V02 — Respect non-extensibility when creating symbol properties

- **Status:** Implemented in the Broiler.VM working tree; local validation only. Creating a
  Symbol-keyed property now takes the same extensibility decision as a String key: sloppy assignment
  creates nothing, strict assignment and Object.assign throw, Object.defineProperty(ies), accessor
  helpers and class fields throw, and Reflect.defineProperty (and a trap-less Proxy) answer false.
  Existing writable Symbol properties stay writable; the internal storage helper is unchanged. Review
  caught and the fix removed extra Proxy traps on the computed-Symbol class-field path, which now
  calls only `defineProperty`. Two String-key paths sharing the helper were fixed with it
  (`__defineGetter__`/`__defineSetter__` and Proxy class fields). New probe
  `the-symbol-keyed-integrity.js` (67 cases, agrees with Node). Pinned Test262 selection (bytecode):
  8188 -> 8210 of 8496 variants, no new failures. Evidence: VM `docs/evidence/jseal-v02-v03/README.md`.
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

- **Status:** Implemented with V02; local validation only. Symbol-keyed definitions use the same
  ValidateAndApplyPropertyDescriptor path as String keys: frozen Symbol values cannot change,
  non-configurable Symbol properties cannot be deleted, made configurable or converted between data
  and accessor, SameValue redefinitions succeed and partial descriptors merge. Object.freeze and seal
  over a Proxy pass the specified partial descriptors and trap invariants still run. Remaining
  separate defect: OrdinarySet onto an existing receiver property restates the whole descriptor
  (observable only through a Proxy receiver, for both key kinds).
- **Priority / prerequisites:** P2; V02.
- **Work:** Audit freeze/seal processing and symbol descriptor replacement. Apply non-writable and
  non-configurable rules consistently across string/symbol paths; factor descriptor validation only
  after matching behavior is established.
- **Accept:** Frozen symbol values cannot change. A non-configurable property cannot be deleted or
  converted between incompatible data/accessor descriptors. Valid SameValue redefinitions still
  succeed. Proxy-mediated defines retain invariant checks.
- **Exclude:** General property-storage redesign and unmeasured performance work.

## V04 — Implement RegExp guards in string search methods

- **Status:** Implemented in the Broiler.VM working tree; local validation only. startsWith,
  endsWith and includes run IsRegExp (Symbol.match, then the RegExp brand) after ToString(this) and
  before ToString(search). The replaceAll audit found no Symbol.replace dispatch at all: replaceAll
  now performs IsRegExp, the `flags` read and non-global rejection, then dispatches for Object
  patterns only; matchAll's rejection uses the same `flags` read. Review found and fixed a related
  defect: the RegExp String Iterator never finished for a non-global pattern. New probe
  `the-regexp-guards-in-string-search.js` (41 cases; two Node divergences declared and backed by
  Test262). Pinned Test262 String/prototype/{startsWith,endsWith,includes,replaceAll,matchAll}:
  234 -> 270 of 290; wider String and RegExp/prototype selection 2911 -> 2949 of 3445, no
  regressions. Remaining failures are split out: `super[Symbol.replace]` in class methods, matchAll's
  RegExpCreate fallback, and RegExp.prototype[Symbol.matchAll] receiver handling. Evidence:
  VM `docs/evidence/jseal-v04/README.md`.
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

- **Status:** Implemented in the Broiler.VM working tree; local validation only. Sloppy functions
  with simple parameter lists get a mapped `arguments` object over their environment slots
  (`JsMappedArguments`); with duplicate formals the last one is mapped. Strict, class, arrow and
  default/rest/destructuring functions stay unmapped. No artifact-format or verifier change was
  needed; artifacts compiled earlier with duplicate formals stay unmapped until recompiled. New
  probe `the-mapped-arguments-object.js` (64 cases, agrees with Node and the specification); the
  stale `#diverges node 132` declaration was removed and case 132 now answers 9. Direct eval is V14.
  Evidence: VM `docs/evidence/jseal-v05-v06/README.md`.
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

- **Status:** Implemented with V05; local validation only. Mapped [[GetOwnProperty]]/[[Get]]
  answer the live binding; [[Set]]/[[DefineOwnProperty]] write the binding and disconnect on
  `writable: false` after setting the value; accessor replacement and successful deletion
  disconnect; a rejected descriptor leaves the map intact; the map is not exposed to hosts. Pinned
  Test262 language/arguments-object (bytecode): 432 -> 455 of 460; with the related Object and
  function-code subsets 4812 -> 4841 of 4875, no regressions. The five remaining failures are outside
  V05-V06 (two need direct eval, two need `Function.caller`, one needs a non-configurable strict
  `callee`). The native form could not be exercised on the validating machine.
- **Priority / prerequisites:** P2; V05.
- **Work:** Disconnect mappings when an indexed argument is deleted or redefined in ways that require
  it; implement GetOwnProperty/DefineOwnProperty behavior for mapped values. Respect accessor
  descriptors, writability transitions, and failed redefinitions.
- **Accept:** Deletion breaks aliasing; a successful accessor replacement breaks it; making the
  relevant index non-writable produces the specified final parameter/value relationship. An invalid
  descriptor does not partially mutate the map. Focused arguments-object tests pass.
- **Exclude:** Exposing the internal parameter map through the host API.

## V07 — Add ArraySpeciesCreate for map and filter

- **Status:** Implemented in the Broiler.VM working tree; local validation only. A shared
  ArraySpeciesCreate (IsArray through Proxy, `constructor` then `@@species`, fallback to
  ArrayCreate, TypeError for a non-constructor, construction through the engine's metered
  `Construct`) and CreateDataPropertyOrThrow back map and filter. Probe
  `the-array-species-and-spreading.js` covers subclass and custom species, requested length, getters
  running once, invalid species, array-likes and holes. Test262 map 381 -> 399 and filter 444 -> 462,
  no regressions. Cross-realm cases cannot be exercised (`$262.createRealm` refuses; one realm per
  engine) and remain counted as failures. Invalid-length variants still fail because
  `ArrayLengthOf` uses ToUint32 rather than ToLength (a JSP-4 follow-up). Evidence:
  VM `docs/evidence/jseal-v07-v09/README.md`.
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

- **Status:** Implemented with V07 in one reviewed patch; local validation only. slice, splice,
  concat, flat and flatMap build through ArraySpeciesCreate in each method's order (slice coerces its
  bounds before reading `constructor`; splice defines removed elements before mutating; flat reads
  length, depth, then `constructor`). slice/splice/concat set the result length; change-by-copy
  methods still return plain Arrays without reading `constructor`. Test262: slice 98 -> 118,
  splice 116 -> 138, concat 52 -> 133, flat 30 -> 38 and flatMap 31 -> 47 (both complete), no
  regressions over 9757 Array variants. Review follow-up: result elements are defined by a local
  CreateDataPropertyOrThrow rather than the result's own [[DefineOwnProperty]], which matters only
  for exotic species results.
- **Priority / prerequisites:** P2; V07.
- **Work:** Migrate slice/splice, then concat, then flat/flatMap to the shared operation. Treat these
  method families as separate PRs if index/coercion logic expands the patch. Retain method-specific
  length, hole, and property-creation behavior.
- **Accept:** Each method is verified with default/custom/throwing species and sparse receivers;
  observable construction order matches its algorithm. Change-by-copy methods still return the
  required plain arrays. The per-method checklist is complete before closing the slice.
- **Exclude:** Concat-spreadability, which has its own independent tests in V09.

## V09 — Honor Symbol.isConcatSpreadable

- **Status:** Implemented with V07-V08; local validation only. concat applies IsConcatSpreadable
  to the receiver and each argument (one `@@isConcatSpreadable` Get, ToBoolean when defined, else
  IsArray), measures spread operands with ToLength, throws past 2^53-1, charges every visited index
  and preserves holes. The probe covers flagged array-likes, arrays flagged false, throwing getters,
  lookup order, inherited and Proxy-provided hooks, and primitives. The four remaining concat
  failures are cross-realm.
- **Priority / prerequisites:** P2; V07 for coherent result construction; may be developed independently.
- **Work:** Implement IsConcatSpreadable independently from the receiver/result species choice.
  Consult the symbol for both array and non-array inputs, falling back to IsArray only when needed.
  Preserve hole and length behavior without an unmetered scan.
- **Accept:** A flagged array-like object spreads; an array flagged false remains one element.
  A throwing getter propagates, each lookup occurs in order, and sparse positions remain holes.
  Test inherited and Proxy-provided hooks.
- **Exclude:** Changing concat output by applying an unconditional iterable protocol.

## V10 — Respect typed-array species in map and filter

- **Status:** Implemented in the Broiler.VM working tree with V11-V12; local validation only.
  map/filter use SpeciesConstructor, TypedArraySpeciesCreate and TypedArrayCreateFromConstructor.
  Results that are not typed arrays (including Proxies), detached, too short or of a different
  content type raise TypeError; the content-type check is explicit (`JsElements.HoldsBigInts`).
  Probe `the-binary-species.js` cases 1-27 match Node. Remaining Test262 variants were blocked by
  typed-array constructors that did not accept iterables and by the absent `$262.detachArrayBuffer`
  host hook, both outside this slice. Evidence: VM `docs/evidence/jseal-v10-v12/README.md`.
- **Priority / prerequisites:** P2; V07's protocol design, not its Array-specific implementation.
- **Work:** Implement typed-array-specific species construction and result validation. Cover
  map/filter, element conversion, required result capacity, and invalid constructors. Keep future
  BigInt/Number content-type checks explicit rather than assuming all views contain Numbers.
- **Accept:** Custom typed-array species is observed; invalid or undersized results fail correctly;
  detached inputs and throwing getters follow the specified ordering. No unrelated element kind is
  accepted merely because it shares a CLR array representation.
- **Exclude:** Float16 or BigInt typed-array support and resizable views.

## V11 — Respect typed-array species in slice and subarray

- **Status:** Implemented in the Broiler.VM working tree; local validation only. slice constructs
  its species with the count, re-checks detachment afterwards and copies bytes directly only for an
  identical element kind; subarray constructs its species over the shared buffer with
  (buffer, beginByteOffset, newLength). Probe cases 28-53 match Node. **JSeal buffer reads:**
  honouring species let a page steer JSeal's subarray-based `TryGetArrayBufferBytes` (page code in a
  host read, or an `IndexOutOfRangeException` from an over-long view). JSeal now builds each chunk
  with the pinned `Uint8Array` constructor, which consults no species, and
  `APageThatRedefinesTypedArraySpeciesCannotChangeWhatTheHostReads` covers it; that JSeal change must
  precede any pin update to a VM package carrying V11.
- **Priority / prerequisites:** P2; V10.
- **Work:** Use the correct species algorithms for copying versus creating a shared-buffer view.
  Preserve byte offsets and lengths and revalidate detachment at the specified points. Do not treat
  subarray as slice with a different name.
- **Accept:** Species constructor arguments, shared-buffer aliasing for subarray, independent bytes
  for slice, incompatible results, and detachment during observable hooks are covered. Existing
  JSeal buffer reads remain correct while they still use these guest operations.
- **Exclude:** Resizable-view semantics; F05-F06 extend the relevant checks later.

## V12 — Respect ArrayBuffer species in slice

- **Status:** Implemented in the Broiler.VM working tree; local validation only.
  ArrayBuffer.prototype.slice uses SpeciesConstructor and validates brand, detachment, SameValue and
  capacity before copying any byte, then re-checks the receiver's detachment. Every fixed-buffer
  Test262 ArrayBuffer/prototype/slice variant passes (the SharedArrayBuffer receiver case is
  excluded). Probe cases 54-70 match Node, including that no bytes reach a refused result.
- **Priority / prerequisites:** P2; species protocol design from V07.
- **Work:** Implement the buffer-specific species construction and output validation, including
  same-buffer/insufficient-capacity cases and detachment observed during construction.
- **Accept:** Custom and throwing species behave correctly; source/output bytes are independent;
  no bytes are copied before validation requires them to be; fixed-buffer Test262 cases pass.
- **Exclude:** SharedArrayBuffer, transfer, and resizing.

## V13 — Design a direct-eval environment boundary

- **Status:** Design complete (proposed, not implemented) as VM decision record JSD-0026,
  `src/Broiler.VM.Profile.JavaScript/docs/decisions/0026-the-direct-eval-environment-boundary.md`.
  Each direct-eval site gets a verified scope map in the caller artifact (an optional format-v2
  section admitted only with the dynamic surface); an eval request carries inherited strictness and
  syntactic permissions; eval code resolves the caller's live records through new name
  instructions; sloppy functions containing eval get an eval-variables record; compile permission is
  checked on every route. The walk-through covers the card's cases plus program-body sites under
  block/catch/`with` records and the Annex B.3.4 catch exemption. Nine ordered steps map to V14
  (1-5) and V15 (6-9). Probing found silent wrong answers where the build evaluates today (spread
  direct eval, `with` before a local eval, module top-level eval, program-body eval under records);
  step 1 turns them into explicit refusals. No code changed; existing refusals remain. Evidence:
  VM `docs/evidence/jseal-v13/README.md`.
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

- **Status:** Partial by design of the plan: steps 1-5 of the proposed, unsigned VM record JSD-0026
  are implemented in the Broiler.VM working tree (local validation only; approval of design records
  is deferred under the VM MVP terms). Direct eval at a site with a verified scope-map row (function
  code, eval code, and script bodies under block/for-let/switch/catch/`with` records) evaluates
  against the caller's live records: reads, writes, TDZ and const errors, captured bindings,
  closures, nested and re-entrant evaluation, `with`, `arguments`, `this`, `new.target`, strict
  isolation and eval-local lexicals. `function f(){let x=7;return eval('x')}` answers 7. New format
  pieces: EvalScopes section kind 13, the EvalCode flag, opcodes 0x90-0x95, diagnostics 1626/1627,
  and a 0x01 eval request with source-provider capability version 2. The wrong answers V13 found at
  mapped sites now raise the explicit EvalError; spread eval is now direct. **Still wrong:** a
  direct eval at a script's top level with nothing in between keeps the old global path until V15
  (eval lexicals leak, strictness is not inherited, no global conflict checks). Still refused by
  name: sloppy var/function introduction (V15), `super`, private names, parameter-initialiser sites
  and module-scoped sites. Pinned Test262 (bytecode): eval-code/direct 55 -> 106, annexB eval-code
  308 -> 349, a wider selection 6078 -> 6238 of 6870; four accidental passes now meet the V15
  refusal. **JSeal impact:** the pin update that takes this VM change must dispatch eval requests
  through `JsCompiler.TryReadProgramRequest` in `VmSourceProvider`. Evidence: VM
  `docs/evidence/jseal-v14/README.md`. **Later:** V15 implemented steps 6-9 and removed the
  top-level global path and every refusal listed above; see V15.
- **Priority / prerequisites:** P2; V13, V05-V06 for the arguments interaction.
- **Work:** Implement direct eval's access to caller bindings for expressions and assignment, including
  captured mutable bindings and nested invocations. Keep indirect eval global and a shadowed eval
  binding an ordinary call. Add explicit handling for any still-unsupported declaration shapes.
- **Accept:** `function f(){let x=7;return eval('x')}` returns 7; eval assignment is visible to the
  caller and its closures; nested calls do not share the wrong environment. Restricted compilation
  is refused through every route. Interpreter and supported native forms agree.
- **Exclude:** Claiming unrestricted direct eval before V15's declaration/isolation tests pass.

## V15 — Complete eval declaration and strictness semantics

- **Status:** Implemented in the Broiler.VM working tree (local validation only; the design record
  JSD-0026 is proposed and unsigned). Steps 6-9 of JSD-0026: a sloppy direct eval's var and function
  declarations belong to its caller after every EvalDeclarationInstantiation check (including the
  pinned Annex B.3.4 catch-clause exemption); indirect and top-level direct eval are eval code
  (let/const/class do not leak, strictness is inherited, eval-created globals are configurable,
  global conflict checks apply); parameter-initialiser sites have their own rows. Follow-ups gave
  functions with observable parameter expressions a separate body variable environment
  (FunctionDeclarationInstantiation step 28), admitted `super` and the calling class's private names
  in evaluated source, and added a module row kind so module-scoped eval sites read imports live. No
  direct-eval shape is refused by name any longer. Pinned Test262: the eval selection 1779 -> 2123
  of 2228, then over eval-code, function-code, arguments-object and class elements 9975 -> 10087 of
  10225, then with global-code 7226 -> 7228 of 7347, with no regressions. A script-goal host route,
  `JsHostRealm.EvaluateScript` (JSD-0024 section 16), now serves JSeal's host scripts, whose
  captured-eval path V15 turned into eval code; JSeal adopts it with the next VM pin. Evidence: VM
  `docs/evidence/jseal-v15/README.md`, `jseal-v15-finish/README.md`,
  `jseal-vm-module-gaps/README.md`.
- **Priority / prerequisites:** P2; V14.
- **Work:** Implement lexical isolation, strict-eval variable isolation, permitted sloppy var
  introduction, declaration conflicts, and closure access to eval-created bindings. Cover global
  direct/indirect cases as well as function cases. Remove the blanket direct-eval refusal only for
  the supported, verified surface.
- **Accept:** Eval let/const/class bindings do not leak. Strict eval var stays isolated. Sloppy eval
  var behaves as specified without overwriting conflicting lexical bindings. Cover thrown parse
  errors, delete/closure interactions, arguments, and a bounded pinned Test262 eval subset.
- **Exclude:** Eval compilation caching; optimize only after stable semantics and profiling.

## Follow-up fixes outside the numbered slices

Wave reviews found defects outside the V cards. They were fixed as separately described changes in
the Broiler.VM working tree (local validation only, no package yet) and are recorded here so the
owning VM ledgers can pick them up:

- **VM-FIX-A:** the end-user host writes UTF-8 to redirected streams whatever the console code page
  (the retained differential lane now fails only on its long-standing case 36); the conformance
  harness supplies `$262.detachArrayBuffer` through a new public `JsHostRealm.DetachArrayBuffer`;
  detached typed arrays keep their ordinary properties and canonical numeric keys follow the
  integer-indexed rules. Evidence: VM `docs/evidence/jseal-fixes-a/README.md`.
- **VM-FIX-B:** `**` and `Math.pow` follow Number::exponentiate; a ToPropertyKey opcode makes
  computed compound, logical and update assignments convert their key once (not yet for
  `super[k] op= v`); `super[Symbol]` keys work; Array and %TypedArray% `toLocaleString` follow
  ECMA-262 (JSD-0027 N1, open only for its detached-buffer test). Evidence:
  VM `docs/evidence/jseal-vm-fixes-b/README.md`.
- **VM-FIX-C:** Array methods measure length with ToLength and throw at 2^53-1; results are defined
  through their own [[DefineOwnProperty]]; one realm `%ThrowTypeError%` backs strict
  `arguments.callee` and Function caller/arguments; OrdinarySet onto an existing receiver property
  passes only `{ value }`. Pinned Test262 over Array, arguments-object, Reflect/set and Proxy
  subsets: 6032 -> 6233 of 6713, no regressions. Evidence: VM `docs/evidence/jseal-fixes-c/README.md`.
- **VM-FIX-D:** front-end repairs: hoisted functions see their body's lexical bindings, directive
  prologues end only where a semicolon is inserted, tagged templates use a per-site registry (no
  global-object property), strict reserved words are refused, a named function expression's name
  is immutable (also through direct eval), unary operators on the base of `**` are early errors, a
  Symbol completion prints, module destructuring declarations bind, and a module whose evaluation
  threw rejects every later import with the same value. Merging with V15 required recording the
  hoisted lexical slots explicitly for eval conflict checks. Evidence:
  VM `docs/evidence/jseal-frontend-fixes/README.md`.
- **VM-FIX-E:** RegExp protocols: `matchAll` falls back through RegExpCreate and `@@matchAll`,
  `RegExp.prototype[Symbol.matchAll]` follows SpeciesConstructor and the `flags` Get, the RegExp
  String Iterator steps through RegExpExec with the pinned done semantics, the constructor uses
  IsRegExp, and primitive patterns are not asked for their symbols. Pinned Test262 selection
  2652 -> 2756 of 4451. Evidence: VM `docs/evidence/jseal-regexp-protocols/README.md`.
- **VM-FIX-F:** object integrity: freeze/seal through [[DefineOwnProperty]], Array mutators with
  DeletePropertyOrThrow, the append fast path honouring inherited setters, ArraySetLength's double
  conversion, the module namespace [[DefineOwnProperty]], Array.from/%TypedArray%.from through their
  receiver with ToLength, `JSON[Symbol.toStringTag]`, IsArray for array replacers, and iterator
  steps whose getters throw. Pinned Test262 selection 12739 -> 12811 of 15511. Evidence:
  VM `docs/evidence/jseal-jsp5-integrity/README.md`.
- **VM-FIX-G:** the RegExp Symbol methods (`match`, `replace`, `search`, `split`) take any Object
  receiver, read `flags` and go through RegExpExec (a custom `exec` is observed); `split` builds its
  "y" splitter through SpeciesConstructor, `replace` uses GetSubstitution with `groups`, and the
  built-in `exec` always reads `lastIndex` through ToLength. Pinned Test262 selection 2756 -> 2958 of
  4451, no regressions. Evidence: VM `docs/evidence/jseal-regexp-symbols/README.md`.
- **VM-FIX-H:** `for await` closes its iterator on an async generator's forced return; the conformance
  harness has `$262.IsHTMLDDA` through a new `JsHostRealm.NewHtmlDdaObject` (JSD-0024 section 18,
  unreachable by ordinary guests), and `??`/`?.` use a strict nullish test; `using await` in a static
  block reports the reserved-word diagnostic; RegExp group names are read by code point; module
  requests settle when a loader throws; `DetachClone` refuses `Missing`. Pinned Test262 focused
  selection 6460 -> 6514 of 6929, nothing newly failing. Evidence: VM `docs/evidence/jseal-small-gaps/README.md`.
- **VM-FIX-I:** host exotic-object hooks and every host re-throw translate what they raise, so a
  foreign reference or a BigInt a realm declined is a guest TypeError rather than a ProfileFault
  (JSD-0024 section 19.1); `Array.of` constructs through its receiver; the Error, NativeError, Date
  and RegExp prototypes are ordinary objects; a typed array built from an Array follows a replaced
  %ArrayIteratorPrototype%.next; `ArrayBuffer` reads `new.target.prototype` before allocating; the
  `JSON.parse` reviver's [[Delete]] route was verified and given regressions. Pinned Test262 selection
  12865 -> 12899 of 13326, RegExp 2460 -> 2462, nothing newly failing. Evidence:
  VM `docs/evidence/jseal-vm-fix-i/README.md`.
- **VM-FIX-J:** `Date.prototype.toJSON` is generic; `Array.prototype.entries`/`keys`/`values` begin
  with ToObject, the array iterator measures with LengthOfArrayLike and a keys iterator never reads
  the element; `indexOf`/`lastIndexOf` handle a -0 start; inside a step every exotic-object hook is a
  charged, gated crossing like the deletion hook and the module loader, and outside every step the
  handler is not asked, so an uncaught exotic object ends as an ordinary guest fault (JSD-0024 section
  19.2); the regex matcher's metering unit and its uncharged backtrack-memory bound are documented.
  Pinned Test262 focused selection 942 -> 972 of 974, wide selection 27696 -> 27728 of 29161, nothing
  newly failing. Evidence: VM `docs/evidence/jseal-vm-fix-j/README.md`.
