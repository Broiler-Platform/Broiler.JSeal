# Broiler.JSeal roadmap

Planning baseline: 2026-09-19. This is an implementation plan following the component review. Status
on 2026-09-22: **J00, J02-J16, J18, J19 and I09 are complete**; J01 is implemented with Linux
validation pending, and J17, I10, I12 and I18 are partial. In the sibling Broiler.VM working tree
V01-V15 (V13 as the design record that V14-V15 implement), F01-F22, B01-B05, B07 and B08 are
implemented (F07-F09 on archived Unicode 17 data), B06's VM half is implemented, BigInt is admitted
through an optional surface, the VM halves of I01, I03, I05, I07, I11 and I12 and I14-I17 are
implemented, and D01-D04 are proposed decision records; that VM work is local validation only and is
not in any released package. The Broiler.JS working tree has the module semantics and core fixes I10
needs, also unreleased. JSeal adoption of the VM work (I02, I04, I06, I08, J17's last item, I11,
I12's VM routing, I18's VM clone and B06's VM half) is prepared and validated against a local VM
candidate but waits for a VM release. Remaining: I13, I18's WorkerRealms, and merging the prepared
adoption (including B06's VM-dependent half) once a VM release exists. Nothing here has been
published; the existing package baseline is `0.1.0-preview.1`.

**Where the prepared adoption is.** Several statuses below name a patch file, such as
`jseal-vm-next3-over-normal.patch`. Those are not in this repository and are not meant to be: they
hold the code that only compiles against Broiler.VM host APIs no package carries yet, kept outside
the tree so that what is merged here always builds against the pinned packages. Each such status
says which local candidate it was validated against; the work is re-created against the released
package when the pin moves, and the figures quoted for it hold only of that unpublished candidate.

The objective is to make JSeal's behavior reliable across providers, simplify the adapters, and
close selected Broiler.VM gaps against current Broiler.JS. Correct ECMAScript behavior is the
acceptance criterion: copying a Broiler.JS defect is not progress. An explicit unsupported-feature
refusal is preferable to a plausible wrong result, but does not count as implementing the feature.

## Plan map

| Plan | Contents | Slice IDs |
|---|---|---|
| [JSeal correctness and maintenance](roadmap.jseal.md) | Reproduction, package composition, six confirmed defects, allocation reduction, contracts, diagnostics, documentation | J00-J19 |
| [VM semantic correctness](roadmap.vm-semantics.md) | Coercion order, object integrity, RegExp guards, arguments aliasing, species protocols, direct eval | V01-V15 |
| [VM feature additions](roadmap.vm-features.md) | Float16, resizable buffers, Unicode, iterator helpers, modern library APIs, BigInt, deferred decisions | F01-F22, B01-B08, D01-D04 |
| [Host and cross-repository integration](roadmap.integration.md) | Native VM host operations, adapter adoption, modules, structured clone, worker-realm capability | I01-I18 |

These are work-item IDs local to this plan. They do not replace Broiler.VM's existing `JS-*`,
`JSW-*`, or `JSP-*` milestone IDs, and do not change those milestones' status. Work in another
repository must update its owning ledger when it actually lands.

There are **87 slices**: 20 JSeal slices, 15 VM semantic slices, 34 VM feature/decision slices,
and 18 integration slices. This is a backlog with a recommended starting sequence, not a promise
to deliver all 87 before the next release.

## What the review established

The review read JSeal at `d64ebec`, Broiler.VM at `405fe2d`, and Broiler.JS at `73f071d1`.
After restoring for `Release-VM`, all 143 JSeal tests passed. Additional temporary probes found
defects not covered by that suite. Both local CLI hosts were also rebuilt and given identical
JavaScript probes. This was targeted verification, not a complete Test262 run.

JSeal currently consumes Broiler.JavaScript packages at `0.1.0-preview.1` and VM packages at
`0.1.0-preview.3`. A current source checkout and a pinned package are different test targets.
For example, current Broiler.JS has `Array.fromAsync` and grouping APIs that were absent from the
packaged provider exercised during the review. Never silently substitute one target for the other.

| Finding | Planned response |
|---|---|
| Guest eval becomes permitted while VM host script calls guest code | J04 |
| Standalone JS provider fails when a function uses `arguments` | J02-J03 |
| Callable JS Proxy is tagged as Object | J05 |
| Getters and coercions leak provider-specific exceptions | J06-J07 |
| An ordinary `undefined` property loses to an exotic named property | J08 |
| Removing the default provider leaves default selection broken | J10 |
| Suspected stale exotic indices when a handler withdraws a value | J09: resolve the sparse-index contract before changing it |
| VM binary transfers serialize bytes through guest arrays and strings | I01-I04 |
| VM needs Proxy for host exotic deletion and guest constructors for promises | I05-I08 |
| VM modules exist, but JSeal has no usable module-graph contract | I09-I13 |
| VM lacks structured clone and worker-realm transfer through JSeal | I14-I18 |
| VM implements several language protocols incorrectly | V01-V12 |
| VM rejects direct eval in function scope | V13-V15 |
| VM lacks several APIs available in current Broiler.JS | F01-F22, B01-B08 |
| Shared memory, Intl, cleanup callbacks, and ShadowRealm need scope decisions | D01-D04 |

The original review probes were temporary artifacts. [J00](../diagnostics/J00/README.md) now
retains independently understandable reproductions and a self-contained baseline, with source and
pinned-package identities kept separate. Known differences stay in this opt-in diagnostic lane.

## Starting points in the code

| Slices | Initial implementation surface |
|---|---|
| J02-J03, J19 | [Provider dependencies](../Broiler.JSeal.BroilerJs/Broiler.JSeal.BroilerJs.csproj), [central versions](../Directory.Packages.props), [CI](../.github/workflows/ci.yml) |
| J04, J17 | [VM source policy](../Broiler.JSeal.Vm/VmSourceProvider.cs), [source evaluation](../Broiler.JSeal.Vm/VmRealm.Source.cs) |
| J05 | [JS marshaling](../Broiler.JSeal.BroilerJs/BroilerJsMarshal.cs) |
| J06-J07 | [Member operations](../Broiler.JSeal.BroilerJs/BroilerJsRealm.Members.cs), [coercions](../Broiler.JSeal.BroilerJs/BroilerJsRealm.Values.cs), [jobs](../Broiler.JSeal.BroilerJs/BroilerJsRealm.Jobs.cs) |
| J08-J09 | [Exotic adapter](../Broiler.JSeal.BroilerJs/BroilerJsExoticObject.cs), [exotic contract](../Broiler.JSeal/Realm/IJsExotic.cs) |
| J10 | [Registry](../Broiler.JSeal/Hosting/JsEngineRegistry.cs), [registry tests](../Broiler.JSeal.Tests/JsealRegistryTests.cs) |
| J12, I01-I08 | [VM values and trampoline](../Broiler.JSeal.Vm/VmRealm.Values.cs), [captured intrinsics](../Broiler.JSeal.Vm/VmHostBridge.cs), [VM promises](../Broiler.JSeal.Vm/VmRealm.Jobs.cs) |
| J18, I09-I18 | [Capabilities](../Broiler.JSeal/Hosting/JsCapabilities.cs), [realm API](../Broiler.JSeal/Realm/IJsRealmCapabilities.cs), [clone API](../Broiler.JSeal/Realm/IJsClone.cs) |

In the sibling Broiler.VM repository, start with `src/Broiler.VM.Profile.JavaScript/JsEngine.cs`,
`JsObject.cs`, `JsRealm.Array.cs`, `JsRealm.String.cs`, `JsRealm.Binary.cs`, `JsHostRealm.cs`, and
`src/Broiler.VM.Profile.JavaScript.Compiler/JsCompiler.cs`. Its owning plans are
`src/Broiler.VM.Profile.JavaScript/docs/roadmap.parity.md`, `roadmap.workloads.md`, and
`roadmap.status.md`. File names here are navigation aids, not permission to place all new code
in already-large files; extract a focused helper when the algorithm has a clear boundary.

## What makes a slice small enough

One slice should produce one reviewable behavioral change, a focused regression or conformance
check, and any directly affected documentation. Ordinarily it is one PR in one repository. An API
addition and its consumer adoption are separate slices when they cross repository boundaries.

The scope of a slice is a ceiling, not permission for a large rewrite. If a primitive such as
resizing or BigInt needs several internal changes, land those behind an unadvertised feature gate
and split further. Do not expose a partially working global, opcode, or capability as complete.
If investigation changes an assumption, update the slice and dependency before implementation.

Each card states its owner, prerequisites, work, acceptance criteria, and exclusions. Owners are
repository responsibilities, not assignments to named people. Dependencies refer to completed,
usable behavior; an upstream API is usable by JSeal only after the matching package is available
and its version is explicitly pinned. No dates or throughput estimates are assumed.

## Recommended first ten PRs

1. **J00:** retain the review's minimal reproductions and exact engine identities.
2. **J04:** close the VM host-script permission leak, with focused policy regressions.
3. **J02:** add a standalone package-consumer smoke harness with passing baseline cases.
4. **J03:** repair the missing arguments dependency and enable its failing smoke case.
5. **J05:** correct callable-proxy classification.
6. **J06:** normalize exceptions from property operations.
7. **J07:** normalize exceptions from coercion and guest job boundaries.
8. **J08:** preserve ordinary properties whose value is `undefined`.
9. **J10:** repair default selection after unregistration.
10. **V01:** correct VM numeric operand coercion order.

J00 must not delay an urgent fix: its reproduction may be included in that fix's PR. J01's broader
differential harness is useful infrastructure, not a prerequisite for writing any regression test.
Do not merge deliberately failing tests into the normal CI lane between a harness and its fix.

## Delivery waves and exit gates

| Wave | Scope | Exit gate |
|---|---|---|
| A: dependable JSeal | J00, J02-J08, J10 | All six confirmed defects have regression coverage; both provider configurations and standalone package consumers pass |
| B: trustworthy comparison and cleanup | J01, J09, J11-J19 | Reproducible source/package comparisons, explicit capability coverage, clean extraction documentation, measured callback allocation change |
| C: VM semantics | V01-V12 | Every targeted protocol family has focused positive/negative tests and bounded differential runs |
| D: simpler host integration | I01-I08 | JSeal uses native VM buffer, deletion, and promise operations; corresponding guest-level workarounds are removed |
| E: selected feature growth | F01-F22, B01-B08, V13-V15 | Each selected family passes its own gate before it is advertised; no whole-engine parity claim |
| F: modules and worker transport | I09-I18 | Both providers honor the defined module contract; VM clone/transfer tests establish the worker-realm capability before it is enabled |
| Decisions | D01-D04 | Written scope and dependencies; no implementation is implied merely by listing an absence |

The waves are priority groups, not a global dependency chain. J10, V01, and F10 can be delivered
without waiting for modules, Unicode data, or BigInt. Resizable buffers must account for the clone
and native byte-access contracts, but initial fixed-buffer host copying does not need resizing.

## Verification and completion rules

- Restore for the configuration being tested. `Release` and `Release-VM` have different dependency
  graphs; reusing an unrelated restore can create misleading assembly-loading failures.
- For JSeal changes, run the affected cases and the shared conformance suite against both providers.
  Keep the Windows/Linux CI matrix and verify provider membership, not only a total test-count floor.
- For engine semantics, add focused VM tests and the relevant pinned Test262 subset. Compare with
  current Broiler.JS as a diagnostic; settle disagreements against the pinned specification/tests.
- Give external-process probes a timeout, explicit script/module mode, stable encoding, and an
  identity record containing engine revision or package version, configuration, OS, and command.
- Record separately: passed, failed, intentionally unsupported, and not exercised. A test that
  returns early because a capability is absent is not evidence that the capability works.
- Use local package feeds for package-consumer validation. Do not publish packages or update remote
  consumers merely to test a slice. Normal package release is a later handoff.
- A cross-repository slice is complete only when upstream tests pass, the compatible package is
  available, the consumer explicitly updates its pin, and consumer tests pass.
- A performance claim needs before/after measurements of the same workload. Removing allocations
  or guest calls from code alone does not establish an end-to-end speedup.
- Do not broaden tests repeatedly after the relevant gates pass unless a new change or failure
  justifies it. Documentation-only slices need link/content validation, not engine rebuilds.
- Keep the contracts project free of project/package references. Do not add engine-specific APIs
  or special cases to contracts to make one provider pass.

Suggested local JSeal checks, from the repository root:

```powershell
dotnet test Broiler.JSeal.slnx -c Release
dotnet test Broiler.JSeal.slnx -c Release-VM
```

Implementation must use the owning repository's current commands for focused VM, Test262, and
packaging checks; do not invent command flags from this plan.

## Existing delivered baseline

- [x] Engine-neutral contracts and value model.
- [x] Contracts project with zero project/package references.
- [x] Broiler.JS and Broiler.VM providers.
- [x] Multi-provider conformance and registry tests.
- [x] Packaging automation and publishing workflows.

These existing deliverables do not imply semantic parity or human approval. The numbered slices
remain proposed work until their individual acceptance criteria are met.
