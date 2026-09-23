# JSeal correctness and maintenance slices

[Roadmap index](roadmap.md). J00, J02-J16, J18 and J19 are **complete**. J01 is **implemented; Linux
validation pending**. J17 is **partial** (its last item waits for a VM release). Follow-up audits
outside the numbered slices are listed at the end. Priority: P1 = fix first;
P2 = correctness or enabling work; P3 = maintenance or optimization. Dependencies name work in
this plan, not additional approval steps.

## J00 — Retain the review baseline

- **Status:** Complete. [Opt-in fixtures and reproduction](../diagnostics/J00/README.md) and
  [retained evidence](../diagnostics/J00/baseline-2026-09-19.json) capture all 99 observations.
  Missing-checkout, timeout, incomplete-output and duplicate-ID controls were checked. Ordinary
  suites remain green: Release 80/80; Release-VM 143/143. No production behavior changed.
- **Owner / priority:** JSeal; P2.
- **Prerequisites:** None.
- **Work:** Reduce the temporary review probes to small fixtures. Record JSeal, VM, and JS revisions,
  package pins, host invocation modes, commands, and observed outcomes. Separate pinned-package
  observations from current-source observations. Identify which cases belong to provider tests and
  which belong to VM tests.
- **Accept:** Another developer can reproduce the six confirmed JSeal defects and the listed VM
  differences without the original temporary directory. Keep known failures in an explicitly
  opt-in diagnostic lane until their fixes land.
- **Exclude:** New production behavior, a full Test262 run, and an overall conformance percentage.

## J01 — Make the differential runner usable with both engines

- **Status:** Implemented in the sibling Broiler.VM checkout; Windows validation complete,
  Linux execution pending. `eng/run-differential.py` now supports engine presets and JSON argument
  templates, per-engine divergences, bounded execution, UTF-8 and identity/output reports.
  `docs/evidence/jsp-1-j01/README.md` in VM retains the validation scope. A Windows/Linux CI job
  is wired; neither local WSL distribution can start because its backing disk is missing.
  This does not mark the broader upstream JSP-1 gate complete.
- **Owner / priority:** Broiler.VM tooling, with JSeal consuming results; P2. Maps to VM JSP-1.
- **Prerequisites:** J00's engine identity and probe format.
- **Work:** Extend the existing differential runner rather than create another one. Give each engine
  an executable and explicit argument template for script/module mode. Handle Windows executable
  names, portable temporary directories, UTF-8 output, timeouts, exit codes, and engine-specific
  expected divergences. Audit current behavior before treating older README claims as current.
- **Accept:** One script and one module fixture run on Windows and Linux; a hung child times out;
  a missing engine fails visibly; a stale expected divergence fails; emitted metadata identifies
  both engines. Preserve meaningful output differences.
- **Exclude:** Blind golden-file regeneration and using JS output as the sole correctness oracle.

## J02 — Test a standalone package consumer

- **Status:** Complete. [Harness and reproduction](../eng/package-consumer/README.md) exercise
  both providers through a sole provider PackageReference in separate temporary projects/processes.
  Local-feed-only restore, fresh caches, package hashes and assembly isolation are checked.
  Realm/evaluation/callback/disposal checks and both negative isolation controls passed on Windows.
  The existing Windows/Linux CI matrix now runs and uploads this check; its Linux execution has
  not been observed locally. J03 now extends this consumer with arguments regressions.
- **Owner / priority:** JSeal; P1 enabling work.
- **Prerequisites:** None; use J00's baseline if available.
- **Work:** Add a small consumer outside the solution's source dependency graph. Restore JSeal and
  provider packages from an isolated local feed with a fresh restore directory. Start with realm
  creation, primitive evaluation, a host callback, and disposal. Identify the expected assemblies.
- **Accept:** The consumer runs in a fresh process with no sibling checkout or preloaded test
  assemblies. It is available as a repeatable CI check. J03 adds the arguments regression atomically
  with its fix so this harness can land green.
- **Exclude:** Remote publication and resolving dependencies from ambient development outputs.

## J03 — Restore ordinary `arguments` support in the JS package composition

- **Status:** Complete. The JS provider now depends on `Broiler.JavaScript.Modules`, centrally
  pinned to `0.1.0-preview.1`. The standalone consumer reproduced the missing-builder failure
  before the fix and passes ordinary, zero-argument, strict and direct-eval arguments cases after
  it. The engine's existing assembly/module-initializer path supplies the builder; consumers need
  no preload or extra reference. The dependency does not opt into module capabilities. The
  [consumer notes](../eng/package-consumer/README.md) record the separate upstream JSArguments
  placement follow-up. Windows validation passed; Linux runs through the existing CI matrix.
- **Owner / priority:** JSeal provider package; P1.
- **Prerequisites:** J02.
- **Work:** Add the compatible Modules dependency required by `JSArgumentsBuilder`, including its
  central version pin and verified initialization path. Exercise the same consumer using only the
  documented provider package. Record a separate JS architectural follow-up if `JSArguments` should
  move into a lower-level assembly; do not combine that move with the immediate package repair.
- **Accept:** `(function(x){ return arguments[0]; })(7)` returns 7 in a fresh package-only process.
  Cover a zero-argument function and an ordinary direct-eval function that needs an arguments object.
  No assembly-load exception escapes. Both JSeal configurations still pass.
- **Exclude:** Claiming module evaluation support merely because Modules is now a dependency.

## J04 — Limit VM host-script permission to its authorized compilation

- **Status:** Complete. Host and classic source use one compilation permit, armed inside the VM
  step immediately before invoking the captured eval intrinsic and consumed before parsing.
  Guest callbacks cannot reuse it or inherit host-only forced strictness. Nested host/classic calls
  restore the enclosing state, including syntax failures and runtime throws. Four VM regressions
  were observed failing before the fix; all eight cross-provider cases pass afterward in
  [source authorization tests](../Broiler.JSeal.Tests/JsealConformanceTests.SourceAuthorization.cs).
  Windows suites pass: Release 84/84 and Release-VM 151/151.
- **Owner / priority:** JSeal VM provider; P1.
- **Prerequisites:** None; include the minimal reproduction from J00.
- **Work:** Replace ambient permission lasting throughout host-script execution with authorization
  consumed by the specific host compilation. Preserve legitimate nested calls to the host API by
  giving each its own scope. Keep guest `eval` and Function construction subject to `AllowGuestEval`
  even when a trusted script invokes guest callbacks. Keep forced strictness scoped to the intended
  host source, not transitively to guest compilation.
- **Accept:** In a restricted realm, install `function page(){return (0,eval)('42')}` through classic
  source, then call `page()` through host source: compilation is refused catchably. Cover Function
  construction, permissive controls, nested host/classic evaluations, failing compilation, and scope
  restoration after a throw. Host script itself still runs.
- **Exclude:** Removing guest globals or making the VM depend on browser CSP classes.

## J05 — Classify callable proxies correctly

- **Status:** Complete. Broiler.JS marshaling uses the engine's `IsFunction` predicate and keeps
  the proxy itself as the identity. Four cross-provider theories cover ordinary and nested proxies,
  receiver/argument forwarding, apply traps, host callback guards, repeated property reads and host
  round trips, noncallable controls, and revocation. Classification never reads guest properties.
  All eight cases pass in
  [callable proxy tests](../Broiler.JSeal.Tests/JsealConformanceTests.CallableProxies.cs).
  Windows suites pass: Release 88/88 and Release-VM 159/159.
  Both isolated package consumers and all four negative controls pass; local evidence is in
  `test-results/package-consumer/j05-validation/report.json`.
  The [value contract](jseal.md) specifies actual Array exotic classification; array proxies remain
  Object handles.
- **Separate follow-up found during validation:** Broiler.JS's host invocation path can run an
  `apply` trap on a noncallable proxy: evaluating `new Proxy({}, { apply: function () { return 42; } })`
  and passing the result to `realm.Invoke` returns 42. Its corrected `IsFunction` is false, so a host
  callback guard rejects it. Audit rejection of noncallable values at the invocation boundary in a
  separate change; this slice changes classification only. Broiler.VM rejects this as a host API
  refusal. Both providers raise a guest `TypeError` when invoking a revoked *callable* proxy.
- **Owner / priority:** JSeal Broiler.JS provider; P2.
- **Prerequisites:** None.
- **Work:** Use the engine's callability predicate before the general object arm of marshaling.
  Preserve canonical identity. Specify separately whether JSeal's array kind means an actual Array
  exotic or the language's `IsArray` result before changing array-proxy classification.
- **Accept:** A callable Proxy has `IsFunction=true`, passes a host callback guard, invokes normally,
  and retains identity across property reads. A noncallable Proxy remains noncallable. A revoked
  callable Proxy keeps its callable classification but invocation raises the appropriate guest error.
- **Exclude:** Guessing callability from a `.call` property or silently widening the array contract.

## J06 — Normalize exceptions from member operations

- **Status:** Complete. A provider-local `Execute` helper enters the realm and translates only
  `JSException`; explicit state and static delegates avoid closures on property reads. All member
  operations use it, including enumeration traversal. Definitions now use the engine's virtual
  `DefineProperty` with complete, null-prototype descriptors; prototype writes use `SetPrototypeOf`.
  These paths run Proxy traps and retain the engine's array-length and descriptor checks instead
  of bypassing them with storage writes. Guest values survive host callback round trips through the
  engine's existing exception unwrapping. Host failures remain distinguishable.
  [Member exception tests](../Broiler.JSeal.Tests/JsealConformanceTests.MemberExceptions.cs) cover
  15 getter/setter/Proxy routes with numeric, undefined and object throws, nested realm/pump
  restoration, and host programming errors. Before the fix 46 JS cases failed; all 94 cross-provider
  cases pass. Combined J06/J07 Windows suites pass: Release 152/152, Release-VM 287/287.
  Both isolated package consumers and all four negative controls pass; local evidence is in
  `test-results/package-consumer/j06-j07-validation/report.json`.
- **Owner / priority:** JSeal Broiler.JS provider; P2.
- **Prerequisites:** None.
- **Work:** Introduce a small provider-local execution helper that enters the realm and translates
  guest `JSException` values. Apply it to property/index reads and writes, accessors, property
  existence/deletion/enumeration, and prototype operations that can execute guest code. Preserve
  scope restoration and exception payload identity.
- **Accept:** A getter throwing 42 reaches a host as `JsEngineException` carrying 42 on both engines.
  Add setter and Proxy-trap variants. Guest throws round-trip through a host callback without losing
  identity. Ordinary host programming errors remain distinguishable from guest throws.
- **Exclude:** Catching every CLR exception as a guest error or adding a cross-engine base class.

## J07 — Complete coercion and job exception boundaries

- **Status:** Complete. String and number coercions and each drained job use the J06 boundary.
  String coercion uses the engine's `StringValue` operation, which honors `Symbol.toPrimitive` and
  remains usable after a throw. Captured promise resolve/reject actions enter their owning realm
  and pump, restore a reentrant caller, and reject use after disposal. Guest promise failures remain
  rejections; a throwing queued host action stops the drain, preserves its exception, and leaves
  later jobs queued. Queue ordering is unchanged.
  [Coercion and job tests](../Broiler.JSeal.Tests/JsealConformanceTests.CoercionAndJobs.cs) cover
  repeated coercion failures, host callback round trips, primitive hints, symbols, job recovery,
  thenable/reaction failures, and resolve/reject calls from another realm. Initial regressions
  reproduced 14 JS failures; all 34 cross-provider cases now pass. Combined J06/J07 Windows suites
  pass: Release 152/152, Release-VM 287/287.
  The isolated package-consumer run recorded under J06 also passes for the combined changes.
- **Owner / priority:** JSeal providers; P2.
- **Prerequisites:** J06.
- **Work:** Apply the same guest-exception rule to string/number coercion and guest work reached
  while draining jobs. Audit resolve/reject callbacks and reentry. Document what happens when an
  explicitly queued host `Action` throws; preserve host exceptions rather than inventing guest ones.
- **Accept:** Throwing `toString`, `valueOf`, and `Symbol.toPrimitive` produce the same public
  exception category in both providers. Check thrown objects and `undefined`. A failing job restores
  the previous realm/pump and leaves remaining jobs in the documented state. Promise rejection must
  remain rejection where the language requires it.
- **Exclude:** Changing microtask scheduling to make exceptions disappear.

## J08 — Preserve exotic-property precedence for `undefined`

- **Status:** Complete. Broiler.JS checks ordinary property presence before reading a named value,
  preserving `undefined` and avoiding duplicate getter calls. Regression coverage also exposed the
  VM's handler-before-prototype ordering: the JSeal VM adapter now declines names already present
  on the prototype chain so the engine reads them with the original receiver. No upstream package
  change was needed. The named lookup contract now states own and inherited precedence explicitly.
  [Exotic precedence tests](../Broiler.JSeal.Tests/JsealConformanceTests.ExoticPrecedence.cs) cover
  own/inherited data properties, getters and setter-only accessors, supplying/declining handlers,
  getter receiver and call count, deletion, methods, and self-deleting getters. Before the fix 13 JS
  and 7 VM cases failed; all 26 cases pass. Windows suites pass: Release 165/165, Release-VM 313/313.
  Both isolated package consumers and all four negative controls pass; local evidence is in
  `test-results/package-consumer/j08-validation/report.json`.
- **Owner / priority:** JSeal Broiler.JS provider, with a VM adapter correction found by parity tests; P2.
- **Prerequisites:** None.
- **Work:** Distinguish an existing ordinary property from a missing property without using the
  returned value as an existence test. Respect the prototype-chain precedence already promised by
  `IJsExotic`. Avoid reading a getter twice while answering one lookup.
- **Accept:** An own ordinary `name=undefined` shadows a handler's named value. Repeat with an
  inherited property and a getter returning `undefined`; the getter runs once. Deleting the ordinary
  property exposes the handler's value. Ordinary methods keep precedence.
- **Exclude:** Enumeration redesign and the sparse-index question in J09.

## J09 — Specify and fix exotic index withdrawal

- **Status:** Complete. Retained the dense indexed contract used by both engine enumeration paths
  and the available collection handlers: `IndexedLength` is an exclusive bound, every entry below
  it must be supplied, and explicit `undefined` is a present value. Holes are invalid host behavior;
  both adapters raise `InvalidOperationException` when validation encounters one. Shrinking the
  bound withdraws entries. The JS provider now tracks handler-owned slots separately, refreshes
  only those slots and removes only them on shrinkage. Ordinary expandos, explicit definitions and
  accessors survive length changes. Handler slots are read-only, matching the VM's descriptors.
  Numeric host deletion now uses indexed storage, allowing deletion of an ordinary override to
  reveal the handler. No sparse-index support or enumeration redesign was added.
  [Index lifecycle tests](../Broiler.JSeal.Tests/JsealConformanceTests.ExoticIndices.cs) cover growth,
  replacement, shrinkage, warmed guest reads, presence/descriptors/keys/spread, expandos, ordinary
  accessors, hole rejection and recovery. Three initial regressions failed before the fix; all
  eight cross-provider cases pass. Windows suites pass: Release 169/169, Release-VM 321/321.
  Both isolated package consumers and all four negative controls pass; local evidence is in
  `test-results/package-consumer/j09-validation/report.json`.
- **Separate follow-up:** The JS provider's `OwnPropertyNames` invokes an ordinary indexed getter
  while traversing the engine's key enumerator; VM does not. This was observed with a getter at
  index 0 and a call counter during J09 validation. Audit enumeration side effects separately.
- **Owner / priority:** JSeal contracts and providers; P2 investigation, then bounded fix.
- **Prerequisites:** J08 is useful but not required.
- **Work:** Resolve whether `IndexedLength` permits holes below its bound, since the current wording
  can be read as a count of a dense collection. If withdrawal within a stable bound is supported,
  remove stale materialized indices when the handler stops supplying them. Otherwise document and
  validate the dense contract. Keep handler-generated indices distinct from ordinary user properties.
- **Accept:** Tests cover collection growth, shrinkage, replacement, an ordinary indexed expando,
  and the chosen hole/withdrawal behavior on both providers. If holes are supported, a removed value
  never reappears via cached storage or enumeration.
- **Exclude:** Treating the review's duplicate-name probe as a confirmed defect: `SupportedNames`
  explicitly forbids repeating ordinary property names.

## J10 — Repair registry defaults after removal

- **Status:** Complete. The public static registry delegates to an internal state instance with one
  lock protecting registrations and default selection. First registration still selects the default;
  removing it selects the remaining name first in ordinal, case-insensitive order. Replacement
  retains selection, unknown removal changes nothing, and empty/reset state accepts a new first
  default. Environment selection still trims whitespace, ignores case and falls back for unknown
  names. `All` returns a snapshot, and no provider code runs while the registry lock is held.
  [Registry tests](../Broiler.JSeal.Tests/JsealRegistryTests.cs) now use isolated state for all
  mutations, including environment-selection inputs, so conformance discovery sees only real
  providers. Tests include concurrent reads during selection, replacement and removal. Three
  regressions failed before the fix; all 13 registry tests pass. Windows suites pass:
  Release 171/171, Release-VM 323/323.
  Both isolated package consumers and all four negative controls pass; local evidence is in
  `test-results/package-consumer/j10-validation/report.json`.
- **Owner / priority:** JSeal contracts implementation; P2.
- **Prerequisites:** None.
- **Work:** Define a deterministic fallback when the selected default is removed. Keep registry
  mutations and default selection coherent, including replacement, reset, and environment override.
  Use an appropriately small synchronization strategy; a concurrent dictionary alone does not make
  the separate default-name state atomic.
- **Accept:** Register A and B, choose A, remove A: default resolves to B. Remove B: default gives the
  empty-registry error. Cover replacement, unknown removal, environment selection, and coordinated
  mutation/read tests. Isolate these tests from the global provider registry used by conformance.
- **Exclude:** A service-container rewrite or changing unknown environment-selection behavior.

## J11 — Define and enforce realm lifetime behavior

- **Status:** Complete. The realm contract now specifies idempotent disposal, readable
  `EngineName`/`Capabilities`, and `ObjectDisposedException` for every other realm operation with
  valid arguments, including unavailable capabilities and primitive inputs. Captured promise
  settlers obey that rule for pending and already settled promises without reading thenables or
  scheduling reactions. Hosts still serialize realm operations, settlement and disposal.
  Added missing JS guards for global access, job queries, buffer reads and transfer classification;
  VM buffer reads now guard primitive inputs, and capability checks follow disposal checks in both
  providers. JS teardown now removes its evaluation-policy subscription only if it installed one,
  preserving restrictions held by other wrappers on a borrowed context.
  [Lifetime conformance tests](../Broiler.JSeal.Tests/JsealConformanceTests.Lifetime.cs) cover all
  realm operation categories, repeated disposal, metadata, queued work and late settlement.
  [Adoption tests](../Broiler.JSeal.Tests/BroilerJsLifetimeTests.cs) verify that the original context,
  host promise queue and another wrapper's policy survive wrapper disposal. Ten cases failed
  before the fixes. Windows suites pass: Release 186/186, Release-VM 350/350.
  Both isolated package consumers and all four negative controls pass; local evidence is in
  `test-results/package-consumer/j11-validation/report.json`.
- **Owner / priority:** JSeal contracts and providers; P2 audit.
- **Prerequisites:** J06-J07.
- **Work:** Inventory public operations that currently bypass disposal checks, including buffer
  reads, pending-job queries, transfer classification, and global access. State which metadata may
  be inspected after disposal, and how retained resolve/reject delegates behave. Make the providers
  agree without widening the supported concurrency model.
- **Accept:** A small contract-driven matrix covers disposal twice, documented metadata, forbidden
  engine access, and late settlement. No guest execution occurs through an operation declared invalid
  after disposal. Adopted contexts retain their separate ownership rules.
- **Exclude:** Promising arbitrary concurrent realm execution or disposing a borrowed context.

## J12 — Reduce VM callback argument allocations

- **Status:** Complete. The VM callback adapter now uses a managed inline array for up to eight
  arguments and rents a separate array for larger calls. A single `finally` returns the rented
  array with all references cleared after conversion, return-value conversion, host failures and
  guest throws. Each recursive invocation retains its own buffer and receives a span limited to
  the actual argument count. Existing guest exception translation is preserved.
  [Callback conformance tests](../Broiler.JSeal.Tests/JsealConformanceTests.CallbackArguments.cs)
  cover arities 0, 1, 8, 9 and 32: receiver and object identity, ordered arguments, undefined versus
  Missing, new.target, recursive calls, nested failures, guest/host exceptions and subsequent reuse.
  [Retained measurements and reproduction](../diagnostics/J12/README.md) isolate the actual adapter
  inside a live VM turn. On Windows x64/.NET 10.0.12, numeric callbacks at non-zero measured arities
  drop from 48/216/240/792 bytes per call to zero after warm-up; zero arguments remain zero.
  Full JSeal roundtrips drop by the same amounts. The VM host API's two argument arrays remain,
  and pool misses or other value conversions may still allocate. Corrected the `JsCall` allocation
  claim to distinguish its frame from the whole engine crossing.
  Windows suites pass: Release 201/201, Release-VM 380/380. Both isolated package consumers and all
  four negative controls pass; local evidence is in
  `test-results/package-consumer/j12-validation/report.json`.
- **Owner / priority:** JSeal VM provider; P3.
- **Prerequisites:** Correct callback exception/lifetime behavior from J07 and J11.
- **Work:** Measure small host callbacks, then use an inline managed-reference buffer for common
  arities and a pooled fallback for larger calls, following the existing JS adapter's approach.
  Clear pooled references on every return path. Do not use unmanaged `stackalloc` for JsValue.
- **Accept:** Correct receiver, arguments, Missing, and new.target at arities 0, 1, 8, 9, and a larger
  case, including recursive callbacks and throws. Retained measurements show the specific adapter
  allocation removed. Account separately for arrays still allocated by the underlying VM host API.
- **Exclude:** A claim of zero allocations across the entire engine boundary without measuring it.

## J13 — Remove dead private scaffolding

- **Status:** Complete. Confirmed that `VmRealm` has one constructor and always owns its runtime,
  then removed `_ownsRuntime` and its unreachable disposal branch. Replaced the constant-returning
  `ParseOptions(options)` helper with `SliceParseOptions.Script`, preserving the separate strict-mode
  argument. VM array creation and calls already share `VmMarshal.UnwrapAll`; JS array creation and
  transfer options now reuse their existing `UnwrapAll` helper instead of duplicating its loop.
  The JS empty-array fast path, owned/borrowed context paths, disposal order and public `DocumentUrl`
  remain unchanged. Production code is 14 lines smaller with no public API or intended behavior
  change. Existing tests cover the affected array, transfer, source, callback and lifetime paths;
  no new behavior tests were needed. Windows suites pass: Release 201/201, Release-VM 380/380.
  Both isolated package consumers and all four negative controls pass; local evidence is in
  `test-results/package-consumer/j13-validation/report.json`.
- **Owner / priority:** JSeal VM provider; P3.
- **Prerequisites:** None; reconcile with in-flight ownership work first.
- **Work:** Remove `_ownsRuntime` if every constructor still owns the runtime, and replace
  `ParseOptions(options)` with its constant value if its argument remains unused. Reuse existing
  argument-conversion helpers where semantics match. Keep genuinely different ownership paths in
  the JS provider intact.
- **Accept:** Public API and runtime behavior are unchanged, builds pass, and the resulting diff is
  smaller. Review changes against current code rather than deleting fields based only on this plan.
- **Exclude:** Removing public `DocumentUrl`; J17 decides its intended behavior.

## J14 — Repair extraction documentation and comments

- **Status:** Complete. Replaced the extraction-era guide with the current project layout,
  contracts, source authorization, capabilities, registration and provider-extension steps.
  Removed five broken local links and obsolete HtmlBridge project/script/budget instructions.
  README and guide now distinguish building both provider libraries from conditional VM test
  references, describe the actual CI jobs, and state that no dedicated neutrality-ratchet gate
  exists here. Downstream integration claims are delegated to the integration roadmap instead of
  treating historical bridge counts as current facts. API/setup comments now describe current
  strict-mode, lifetime, module, registry and capability behavior with shorter rationale.
  [Documentation validation](../diagnostics/J14/README.md) checks local links, headings and project
  examples; missing-file and missing-anchor controls passed. The guide's C# example compiled with
  zero warnings/errors and was not executed. Comparison with the start of J14 confirmed all source
  and project edits were comment-only; 44 changed XML documentation blocks parsed successfully.
  The human-review record is byte-for-byte unchanged and remains PENDING. No behavioral tests or
  package checks were rerun, and no vendored build files changed. Local evidence is under
  `test-results/j14/`.
- **Owner / priority:** JSeal; P3.
- **Prerequisites:** None.
- **Work:** Replace stale HtmlBridge paths, nonexistent local script links, outdated provider counts,
  and historical migration narratives with current contracts and short rationale. Move substantial
  architectural history into a linked document only when it remains useful. Document which neutrality
  checks actually exist instead of describing an absent script as a CI gate.
- **Accept:** Repository-local documentation links resolve, examples use this repository's project
  names, and capability claims agree with executable tests. Preserve the human-review record's
  pending status. Validate documentation only; no new behavior tests are needed.
- **Exclude:** Rewriting vendored build files independently of their upstream owner.

## J15 — Restore useful nullable diagnostics in contracts

- **Status:** Complete. With every inherited suppression lifted, the contracts produced no nullable,
  CS9113, CA1416, CA2255 or SYSLIB0013 diagnostics, so the reference-free contracts now inherit no
  repository `NoWarn` entry and promote `nullable` warnings to errors. The audit fixed two
  annotation/documentation mismatches: `IJsRealmAdoption.TryAdopt` gained the `[NotNullWhen(true)]`
  its documentation already promised, and the `TryGetArrayBufferBytes` documentation now says null
  on false, as both providers and the annotation do (its missing parameter tag was added). A
  [before/after public API comparison](../diagnostics/J15/README.md) shows exactly that one
  source- and binary-compatible attribute change. A build control showed an injected nullable
  violation fails the contracts build.
- **Owner / priority:** JSeal contracts; P3.
- **Prerequisites:** None.
- **Work:** Audit inherited global warning suppressions against the reference-free contracts
  assembly. Stop suppressing nullable diagnostics that the contracts can satisfy; fix actual
  annotation/guard mismatches. Retain a narrow, explained suppression only where required.
- **Accept:** Contracts build cleanly with the selected diagnostics active, public annotations are
  intentional, and the project remains reference-free. Verify public API changes explicitly.
- **Exclude:** Broad formatting changes or adding meaningless null guards to every method.

## J16 — Narrow nullable suppressions in providers

- **Status:** Complete. The nullable family (CS8600-CS8767) is no longer suppressed anywhere; both
  providers, the tests and the diagnostic projects build with zero warnings in Release and
  Release-VM. Enabling it surfaced four diagnostics: `BroilerJsEngineProvider.TryAdopt` now carries
  the J15 contract attribute; `VmRealm`'s exotic delete trap handles the pinned VM package's
  unannotated `JsHostValue.AsString()` with a local pattern match instead of a suppression; a test
  probe reports non-string results by kind. No engine null became Undefined and no observable null
  behaviour changed. The diagnostics were few enough to take in one change rather than per family.
  Follow-up polish removed the now-redundant null-forgiving operators and the unused CS9113, CA1416
  and SYSLIB0013 entries; the global `NoWarn` list now holds only CA2255, which the providers'
  registration module initializers need (removing it raises four warnings).
- **Owner / priority:** JSeal providers; P3.
- **Prerequisites:** J15 and settled changes from J06-J11.
- **Work:** Enable diagnostic families incrementally per provider, distinguish engine annotation
  deficiencies from real bugs, and use localized adapters or suppressions for unavoidable boundary
  mismatches. Split into one provider or diagnostic family per PR if necessary.
- **Accept:** The selected family is no longer suppressed globally; builds pass; any corrected
  observable null behavior has focused coverage. Do not turn an engine null into Undefined without
  checking the Missing contract.
- **Exclude:** An all-repository nullability campaign in one PR.

## J17 — Make source labels and DocumentUrl meaningful

- **Status:** Partial; the remaining item waits on a Broiler.VM release. Each evaluation selects one
  source identity with `JsRealmOptions.SourceLabelFor`: a non-blank label, else a non-blank
  `DocumentUrl`, else `anonymous`. `DocumentUrl` is retained and documented as that fallback only;
  it is not used for module resolution, caching, origin checks or permission, and labels never
  select strictness or permission. `JsEngineException` gains `SourceLabel`, `SourceLine` and
  `SourceColumn`; both providers attach the label to guest throws and syntax errors. A reported
  syntax-error line is the ECMAScript line in the supplied text or no line at all (each provider
  withholds lines where its pinned front end is unreliable), and lines survive forced strictness.
  [Source identity tests](../Broiler.JSeal.Tests/JsealConformanceTests.SourceIdentity.cs) keep
  guaranteed metadata apart from provider-specific stack details. **Upstream:** Broiler.VM now has
  `JsScriptUnit.SourceName`, the template/U+2028 front-end fixes and the script-goal route
  `EvaluateScript`; the Broiler.JS working tree counts lone CR, U+2028 and U+2029 as line breaks and
  reports unterminated tokens where scanning stopped. The prepared VM adoption
  (`jseal-vm-next3-over-normal.patch`, validated on candidate `0.1.0-preview.4.local.7`) runs host
  scripts through `EvaluateScript` with the selected label and removes `VmRealm.MayMiscountLines`;
  the Broiler.JS line-withholding can go once a Broiler.JS package with the lexer fix is pinned.
- **Owner / priority:** JSeal with a VM source-provider API dependency if needed; P2.
- **Prerequisites:** J04. Design before implementation if the engine API lacks source metadata.
- **Work:** Specify precedence between explicit source label and optional document URL, carry the
  selected label through VM compilation, and preserve line/column information under forced strict
  mode. State what stack data the VM can actually provide. Document or deprecate an unused public
  option if it cannot be honored; do not remove it silently.
- **Accept:** A syntax error and a thrown error can be attributed to the supplied script identity;
  line positions survive the strictness mechanism; labels cannot grant evaluation permission.
  Tests separate guaranteed metadata from unsupported stack details.
- **Exclude:** Source maps, debugger protocols, and invented VM stack frames.

## J18 — Specify capabilities and expose untested coverage

- **Status:** Complete. [Capability
  coverage](../Broiler.JSeal.Tests/JsealConformanceTests.Capabilities.cs) accounts for every flag
  and provider. Each witness theory takes its rows from `EnginesDeclaring` for its own flag and
  asserts the capability with `AssertHas`; the 25 silent `Lacks` early returns were removed.
  Undeclared flags are reported as refusal rows (the contract's `JsCapabilityUnavailableException`
  rule), absence-only rows for GlobalIsVariableScope and ReentrantHostCalls, or recorded-gap rows
  for Modules and DynamicImport, which also appear as two skipped placeholder witnesses until I13; a
  provider declaring either fails the suite. Meta-tests check that every witness exists and is
  driven by its own flag and that every provider/flag pair is witnessed or refused exactly once. VM
  builds assert the identities `{broiler-js, broiler-vm}` rather than a count; Release asserts
  broiler-js alone. The enum's numeric values are pinned. The [guide](jseal.md) documents provider
  ability versus realm permission (AllowGuestEval plus the VM provider's bridge checks) and decides
  that same-realm structured clone needs its own flag on a new bit before any provider supports only
  one half; I18 adopts it. BigInt support is a stated per-engine table, and broiler-vm's refusal is
  asserted rather than skipped. Windows suites: Release 221 passed / 2 skipped, Release-VM 403
  passed / 2 skipped.
- **Owner / priority:** JSeal contracts/tests; P2.
- **Prerequisites:** J00; coordinate with I09 and I18 before adding flags.
- **Work:** Document provider ability versus realm permission. Map each advertised capability to an
  executable witness. Make unsupported cases visible in the test report instead of silent early
  returns. Determine whether same-realm structured clone needs a capability separate from
  WorkerRealms; preserve existing enum values and compatibility.
- **Accept:** Both provider identities are asserted in VM builds; a removed capability witness
  cannot leave an apparently fully exercised suite. Module capability claims are tied to the new
  module API once I13 lands. Before that API exists, record its coverage gap explicitly; J18 can
  complete without waiting for I13, and I13 later supplies the missing witness. An unsupported
  capability is not counted as passing implementation.
- **Exclude:** Claiming complete language support from the Document capability bundle.

## J19 — Establish the package handoff gate

- **Status:** Complete. [The package gate](../eng/package-consumer/README.md) extends the local-feed
  consumer check. `eng/package-gate.mjs` holds the version rules as tested pure functions (NuGet
  version grammar, case-insensitive prerelease labels, ranges, one version per engine family,
  provider packages declaring exactly their own family, NuGet unification to the highest declared
  floor, feed closure, byte-identical candidate archives); `node --test` runs 21 fixtures, and CI
  runs them. `eng/test-package-consumer.ps1` runs BroilerJs, Vm and Both consumers, each in a fresh
  process with its negative controls, gating before and after. An optional candidate lane
  (`-CandidateFeed`, `-CandidateVersion`) evaluates an unpublished engine family in a temporary copy
  with rewritten pins and a fresh package cache, then runs both configurations; default pins never
  change and nothing is published. Under PowerShell 7.6.6 the pinned lane passed (5 graphs,
  26 staged archives, 0 gate errors), and the candidate lane passed against a locally packed
  Broiler.VM `0.1.0-preview.4-local.jseal.1` family built from the merged VM working tree, after
  exposing and fixing two staging defects. Current-source comparisons remain a documented separate
  lane (the retained J00 probes), not an automated one.
- **Owner / priority:** JSeal integration/packaging; P2.
- **Prerequisites:** J02-J03.
- **Work:** Extend the local-feed consumer checks to a candidate set of upstream engine packages.
  Record all versions, reject incompatible mixed families, and exercise both providers in separate
  fresh processes as well as together. Use current-source comparisons as a separate lane.
- **Accept:** A candidate dependency update can be evaluated without changing default pins or
  publishing externally. The eventual pin update is explicit and passes both configurations,
  consumer smoke tests, and package validation. Missing dependencies fail before release.
- **Exclude:** Floating versions, opportunistic upgrades, and automatic publication.

## Follow-up audits outside the numbered slices

- **Guest-evaluation guard of the Broiler.JS provider (prompted by VM JSD-0030 SR-6), 2026-09-21.**
  On the pinned Broiler.JavaScript `0.1.0-preview.1` package, a realm with `AllowGuestEval=false`
  refuses every guest route to the compiler found: eval reached indirectly, each function kind's
  constructor through prototypes, `call`/`apply`/`bind`/`Reflect`, ShadowRealm `evaluate` (direct,
  indirect, subclassed, nested), promise jobs and async continuations; string timer callbacks throw
  TypeError, no interop global exists and `import('clr')` is rejected.
  [Guest-evaluation route tests](../Broiler.JSeal.Tests/JsealConformanceTests.GuestEvalRoutes.cs)
  pin these. **Open upstream:** ShadowRealm child contexts do not forward the creator's evaluation
  policy (SR-6); it is unreachable today only because `importValue` is an unimplemented stub, which
  a test pins exactly so that any package changing it forces a new review. An upstream Broiler.JS
  patch exists but is not landed or packaged. Handing a permissive realm's `eval`, `Function` or
  ShadowRealm into a restricted realm grants compilation on Broiler.JS (the language asks the
  callee's realm); Broiler.VM refuses the foreign handle.
- **J05 follow-up.** `Invoke` and `Construct` on both providers now refuse a non-function handle with
  the realm's TypeError before any Proxy trap runs. Guest-level calls of a noncallable Proxy with an
  `apply` trap still run the trap on the pinned Broiler.JS engine; the Broiler.JS working tree now throws the
  TypeError before any trap runs, and its key enumeration no longer invokes getters (the J09 follow-up),
  but neither is packaged.
- **Broiler.JS parser and intrinsic-prototype fixes (upstream, 2026-09-22).** Found while fixing
  the J05/J09 engine defects. In the Broiler.JS working tree: a doubled quote no longer continues a
  string (`'a''b'` is two literals); only commas separate arguments, object-literal definitions and
  binding elements (`f(1
2)`, `f(1,,2)`, `{a:1
b:2}` and `[a b]` are SyntaxErrors, and an
  identifier after a complete expression is no longer dropped silently); every built-in class records
  its intrinsic constructor and prototype per realm, so engine-created objects, primitive lookups,
  SpeciesConstructor defaults and `Promise.any`'s AggregateError no longer read a replaced global;
  and line and column numbers are 1-based on every line (the DevTools projection reports 0-based
  columns). Failing-first tests in the parser, built-in and debugger suites; the full suite passes
  apart from the two time-zone-dependent tests that also fail on the base; pinned Test262
  language/{expressions,statements,literals} 19697 -> 19705 of 20909 and 24 further language
  directories 3305 -> 3307 of 3487, with no regressions. Not packaged. **JSeal impact:** once a
  Broiler.JS package with these fixes is pinned, J17's Broiler.JS column reporting must be re-checked
  against the consistent base. The two items left over, `(1,)` and the Temporal/Intl
  namespace prototypes, were closed by the follow-ups below.
- **`JsValue.Missing` handed to the VM (2026-09-22).** Found while refreshing the VM adoption: the VM
  provider passed `Missing` arguments, property values and callback results to the engine as the
  profile's own Missing marker, which it resolves to its uninitialised-binding marker, so a guest
  reading such an argument threw a ReferenceError instead of seeing `undefined` - on the pinned VM
  `0.1.0-preview.3` as well. `VmMarshal.Unwrap` now hands `Missing` over as `undefined`, as the
  Broiler.JS provider always has; the shared case `MissingIsUndefinedWhereTheEngineNeedsAValue`
  failed first on VM and passes on both engines.
- **Broiler.JS follow-ups (upstream, 2026-09-22).** In the Broiler.JS working tree, not packaged: the
  arrow cover grammar is strict (`(1,)`, `()`, `(...a)` without `=>`, a rest element with a trailing
  comma or initializer, and parenthesized binding targets at any depth are SyntaxErrors); Temporal
  and Intl objects and generator functions take the realm's intrinsics, and the intrinsic registry is
  internal with first registration winning; the keyed Promise combinators and async disposal settle
  on promise jobs (the Test262 "flakiness" was the host exiting first), `Promise.resolve` adopts
  thenables, and `await using` takes one job per awaited resource. Full suite as before (only the two
  time-zone tests fail); pinned Test262 language 19261 -> 19272 of 20386, intl402/Temporal/Promise/
  generators/disposal 8806 -> 8861 of 8893, modules 1095 -> 1097 of 1667, no regressions. Open
  upstream: a native `await` takes two jobs where the specification takes one, and `await using` in
  async generators and its SuppressedError seeding predate this work.
- **Broiler.JS async ordering (upstream, 2026-09-22).** In the Broiler.JS working tree, not packaged:
  `await` follows the specification's steps and resumes in the reaction job (one job for a native
  promise or a plain value; a thenable's `then` runs in a job), so an async function settles one
  promise instead of one per await step; async generators use the same Await, queue their requests,
  and `yield*` over an async iterator follows the specification; `for await` awaits values only for a
  sync iterable; each `await using` disposal is the function's own Await, seeds the body error into
  SuppressedError and records needsAwait for a null resource. Two tests that encoded the old
  for-await unwrapping were corrected to the specification's result. `IJSDisposableStack` gains four
  members, a breaking change for external implementers. Full suite 9739 -> 9767 passing (only the two
  time-zone tests fail); pinned Test262 async/disposal lane 5903 -> 6012 of 6409, Promise/Async*/
  DisposableStack built-ins 977 -> 992 of 1014, no regressions. Open: `Array.fromAsync` still steps
  with the sync protocol.
