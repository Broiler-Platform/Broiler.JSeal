using System.Collections.Concurrent;

using Broiler.JSeal.BroilerJs;

namespace Broiler.JSeal.Tests;

/// <summary>
/// The module contract (I09, <c>docs/jseal.modules.md</c>) as provider-neutral cases, written against
/// <see cref="IJsModules"/> alone so that I11 runs them unchanged against the VM provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rows come from the internal gate, not from a capability.</b> No provider declares
/// <see cref="JsCapabilities.Modules"/> before I13, so these theories cannot take their rows from
/// <c>EnginesDeclaring</c>. <see cref="ModuleContractProviders"/> builds a provider instance with its
/// internal module option set, which is the only way to reach the contract until then. I13 replaces
/// it with <c>EnginesDeclaring(JsCapabilities.Modules)</c> plus <c>AssertHas</c>.
/// </para>
/// <para>
/// <b>Every (engine, case) pair is in exactly one of two theories.</b>
/// <see cref="AModuleContractCaseHoldsThroughJsealAlone"/> runs the case and must pass.
/// <see cref="AModuleContractGapIsRecordedAndIsNotAPass"/> covers an engine with no adapter yet, and a
/// case the adapter cannot meet on its pinned engine: it runs the same case and requires it to FAIL,
/// either by the adapter's explicit refusal or, where no refusal is possible, by the contract's own
/// assertion catching the engine's wrong answer. A gap that starts passing fails that theory, so a
/// fixed engine cannot keep a stale gap entry.
/// </para>
/// </remarks>
public partial class JsealConformanceTests
{
    /// <summary>A provider instance with the module contract enabled, per engine that has an adapter.</summary>
    private static readonly IReadOnlyDictionary<string, Func<IJsEngineProvider>> ModuleContractProviders =
        new Dictionary<string, Func<IJsEngineProvider>>
        {
            ["broiler-js"] = static () => new BroilerJsEngineProvider { EnableModuleContract = true },
#if BROILER_VM_JS
            ["broiler-vm"] = static () => new Broiler.JSeal.Vm.VmEngineProvider { EnableModuleContract = true },
#endif
        };

    /// <summary>Registered engines without a module adapter, and why. A recorded gap, not an exemption.</summary>
    private static readonly IReadOnlyDictionary<string, string> ModuleContractEngineGaps =
        new Dictionary<string, string>();

    /// <summary>
    /// A case an engine's adapter cannot meet, why, and what the failure looks like: the exception an
    /// explicit refusal raises, or null when the contract's assertion catches a wrong answer.
    /// </summary>
    /// <remarks>
    /// A <see cref="JsModuleException"/> refusal is expected in <see cref="Phase"/>: the adapter's own
    /// refusal (a module that calls <c>import()</c>) is link-time.
    /// </remarks>
    private sealed record ModuleCaseGap(string Reason, Type? RefusedWith, JsModulePhase Phase = JsModulePhase.Link);

    private const string ImportCallRefused =
        "Broiler.JS 0.1.0-preview.3 resolves an import() specifier through JSModuleContext.Resolve at run time, synchronously, " +
        "and the adapter answers Resolve only from the host resolutions it made when the graph loaded; routing import() " +
        "through the map (I12) is not done for this provider, so the adapter refuses a module containing import() at link time";

    private static readonly IReadOnlyDictionary<(string Engine, string Case), ModuleCaseGap> ModuleCaseGaps =
        new Dictionary<(string, string), ModuleCaseGap>
        {
            [("broiler-js", nameof(TheNamespaceIsAvailableAsSoonAsTheModuleIsLinked))] = new(
                "Broiler.JS 0.1.0-preview.3 loads and links a graph only when an evaluation of it starts, and exposes no " +
                "public link-only entry point; GetNamespace refuses before Evaluate",
                typeof(InvalidOperationException)),
            [("broiler-js", nameof(AMissingExportIsALinkError))] = new(
                "Broiler.JS 0.1.0-preview.3 links when an evaluation starts, so a missing export is that evaluation's " +
                "SyntaxError rather than a Link-phase LoadAsync failure; the adapter does not resolve exports itself; " +
                "reported, not refusable",
                null),
            [("broiler-js", nameof(DynamicImportReachesTheNamespaceAStaticImportDoes))] = new(ImportCallRefused, typeof(JsModuleException)),
            [("broiler-js", nameof(ConcurrentDynamicImportsShareOneLoadAndOneEvaluation))] = new(ImportCallRefused, typeof(JsModuleException)),
            [("broiler-js", nameof(EveryDynamicImportFailureRejectsTheImport))] = new(ImportCallRefused, typeof(JsModuleException)),
            [("broiler-js", nameof(NestedDynamicImportsCarryTheCallingModulesKey))] = new(ImportCallRefused, typeof(JsModuleException)),
            [("broiler-js", nameof(ADynamicImportStopsAtItsGraphsFirstEvaluationError))] = new(ImportCallRefused, typeof(JsModuleException)),
            [("broiler-js", nameof(ImportInEvalAndFunctionCodeCarriesTheCallingModulesKey))] = new(ImportCallRefused, typeof(JsModuleException)),
            [("broiler-js", nameof(DynamicImportIsNotGuestEvaluation))] = new(ImportCallRefused, typeof(JsModuleException)),
            [("broiler-js", nameof(DisposingTheRealmAbandonsADeferredImport))] = new(ImportCallRefused, typeof(JsModuleException)),
            [("broiler-js", nameof(DisposingTheMapRejectsADeferredImport))] = new(ImportCallRefused, typeof(JsModuleException)),
            [("broiler-js", nameof(AnImporterRunsAfterItsDependencysTopLevelAwait))] = new(
                "Broiler.JS 0.1.0-preview.3 starts a host-requested evaluation one job after the request (its ImportAsync " +
                "awaits a job before it links), so a dependency is still Linked when Evaluate returns; the engine exposes no " +
                "synchronous link-and-evaluate entry point; reported, not refusable",
                null),
            [("broiler-js", nameof(DynamicImportFollowsTheMapsOptions))] = new(
                "import() in a host script meets the engine's own global import loader, which resolves through the adapter's " +
                "Resolve and so finds no specifier the map did not resolve for a module; the import rejects; reported, not refusable",
                null),
        };

    /// <summary>The contract's cases, by name.</summary>
    private static readonly IReadOnlyDictionary<string, Func<IJsEngineProvider, Task>> ModuleContractCases =
        new Dictionary<string, Func<IJsEngineProvider, Task>>
        {
            [nameof(TwoModulesEvaluateAndSettleOnlyThroughTheJobQueue)] = TwoModulesEvaluateAndSettleOnlyThroughTheJobQueue,
            [nameof(AMissingModuleFailsTheLoadAndIsNotCached)] = AMissingModuleFailsTheLoadAndIsNotCached,
            [nameof(ARefusedResolutionFailsBeforeAnyLoad)] = ARefusedResolutionFailsBeforeAnyLoad,
            [nameof(EveryPathToAModuleSeesOneNamespaceObject)] = EveryPathToAModuleSeesOneNamespaceObject,
            [nameof(ASharedDependencyIsLoadedAndEvaluatedOnce)] = ASharedDependencyIsLoadedAndEvaluatedOnce,
            [nameof(AnEvaluationErrorIsCachedAndSharedByEveryImporter)] = AnEvaluationErrorIsCachedAndSharedByEveryImporter,
            [nameof(AnEvaluationErrorStopsTheWalkBeforeALaterSibling)] = AnEvaluationErrorStopsTheWalkBeforeALaterSibling,
            [nameof(AThrowInACycleLeavesAnUnreachedDependencyLinked)] = AThrowInACycleLeavesAnUnreachedDependencyLinked,
            [nameof(ACycleMemberTakesTheErrorItsCycleRootRejectsWith)] = ACycleMemberTakesTheErrorItsCycleRootRejectsWith,
            [nameof(GuestErrorsUseTheSharedExceptionBoundary)] = GuestErrorsUseTheSharedExceptionBoundary,
            [nameof(EverySpecifierReachesTheHostsResolve)] = EverySpecifierReachesTheHostsResolve,
            [nameof(AModuleRealmHasTheSameGlobalNamesAsAClassicRealm)] = AModuleRealmHasTheSameGlobalNamesAsAClassicRealm,
            [nameof(ModulesLoadWithoutGuestEvalWhichStillGovernsEval)] = ModulesLoadWithoutGuestEvalWhichStillGovernsEval,
            [nameof(ARealmOpensAtMostOneMap)] = ARealmOpensAtMostOneMap,
            [nameof(ACompletedHostLoadIsStillAppliedInARealmTask)] = ACompletedHostLoadIsStillAppliedInARealmTask,
            [nameof(DisposingTheMapAbandonsItsLoads)] = DisposingTheMapAbandonsItsLoads,
            [nameof(ImportAttributesAreUnsupported)] = ImportAttributesAreUnsupported,
            [nameof(ModuleCodeIsStrict)] = ModuleCodeIsStrict,
            [nameof(LiveBindingsReachImporters)] = LiveBindingsReachImporters,
            [nameof(ACycleSeesATemporalDeadZoneThenTheLiveValue)] = ACycleSeesATemporalDeadZoneThenTheLiveValue,
            [nameof(AnImporterRunsAfterItsDependencysTopLevelAwait)] = AnImporterRunsAfterItsDependencysTopLevelAwait,
            [nameof(AnAsyncDependencyDoesNotDelayItsLaterSiblings)] = AnAsyncDependencyDoesNotDelayItsLaterSiblings,
            [nameof(AnAsyncRejectionLeavesTheSiblingsThatRanEvaluated)] = AnAsyncRejectionLeavesTheSiblingsThatRanEvaluated,
            [nameof(AnEarlierAsyncRejectionDoesNotHideTheModuleThatThrew)] = AnEarlierAsyncRejectionDoesNotHideTheModuleThatThrew,
            [nameof(AModuleWaitingOnTwoRejectionsTakesTheFirstInTime)] = AModuleWaitingOnTwoRejectionsTakesTheFirstInTime,
            [nameof(AnImporterOfTheThrowerThatWasNotReachedStaysLinked)] = AnImporterOfTheThrowerThatWasNotReachedStaysLinked,
            [nameof(AnEqualPrimitiveFromAnAwaitingModuleDoesNotHideTheThrower)] = AnEqualPrimitiveFromAnAwaitingModuleDoesNotHideTheThrower,
            [nameof(AnEvaluationStartedInAHostCallbackStopsAtItsThrower)] = AnEvaluationStartedInAHostCallbackStopsAtItsThrower,
            [nameof(AnAsyncRejectionInAHostCallbackLeavesTheSiblingsThatRanEvaluated)] = AnAsyncRejectionInAHostCallbackLeavesTheSiblingsThatRanEvaluated,
            [nameof(TheNamespaceIsAvailableAsSoonAsTheModuleIsLinked)] = TheNamespaceIsAvailableAsSoonAsTheModuleIsLinked,
            [nameof(TheNamespaceIsAModuleNamespaceExoticObject)] = TheNamespaceIsAModuleNamespaceExoticObject,
            [nameof(ASideEffectOnlyImportLoadsItsDependency)] = ASideEffectOnlyImportLoadsItsDependency,
            [nameof(AMissingExportIsALinkError)] = AMissingExportIsALinkError,
            [nameof(TopLevelDeclarationsAreNotGlobalProperties)] = TopLevelDeclarationsAreNotGlobalProperties,
            [nameof(ModulesDoNotShareTopLevelVarBindings)] = ModulesDoNotShareTopLevelVarBindings,
            [nameof(ModuleCodeHasNoCommonJsBindings)] = ModuleCodeHasNoCommonJsBindings,
            [nameof(TopLevelThisIsUndefinedAndArgumentsIsUnbound)] = TopLevelThisIsUndefinedAndArgumentsIsUnbound,
            [nameof(DynamicImportReachesTheNamespaceAStaticImportDoes)] = DynamicImportReachesTheNamespaceAStaticImportDoes,
            [nameof(ConcurrentDynamicImportsShareOneLoadAndOneEvaluation)] = ConcurrentDynamicImportsShareOneLoadAndOneEvaluation,
            [nameof(EveryDynamicImportFailureRejectsTheImport)] = EveryDynamicImportFailureRejectsTheImport,
            [nameof(NestedDynamicImportsCarryTheCallingModulesKey)] = NestedDynamicImportsCarryTheCallingModulesKey,
            [nameof(ADynamicImportStopsAtItsGraphsFirstEvaluationError)] = ADynamicImportStopsAtItsGraphsFirstEvaluationError,
            [nameof(ImportInEvalAndFunctionCodeCarriesTheCallingModulesKey)] = ImportInEvalAndFunctionCodeCarriesTheCallingModulesKey,
            [nameof(DynamicImportIsNotGuestEvaluation)] = DynamicImportIsNotGuestEvaluation,
            [nameof(DynamicImportFollowsTheMapsOptions)] = DynamicImportFollowsTheMapsOptions,
            [nameof(DisposingTheRealmAbandonsADeferredImport)] = DisposingTheRealmAbandonsADeferredImport,
            [nameof(DisposingTheMapRejectsADeferredImport)] = DisposingTheMapRejectsADeferredImport,
        };

    /// <summary>(engine, case) rows an engine's adapter must pass.</summary>
    public static IEnumerable<object[]> ModuleContractRows =>
        from provider in JsEngineRegistry.All
        where ModuleContractProviders.ContainsKey(provider.Name)
        from contractCase in ModuleContractCases.Keys
        where !ModuleCaseGaps.ContainsKey((provider.Name, contractCase))
        select new object[] { provider.Name, contractCase };

    /// <summary>(engine, case) rows that are recorded gaps: no adapter yet, or a case its engine cannot meet.</summary>
    public static IEnumerable<object[]> ModuleContractGapRows =>
        from provider in JsEngineRegistry.All
        from contractCase in ModuleContractCases.Keys
        where !ModuleContractProviders.ContainsKey(provider.Name) || ModuleCaseGaps.ContainsKey((provider.Name, contractCase))
        select new object[] { provider.Name, contractCase };

    [Theory]
    [MemberData(nameof(ModuleContractRows))]
    public Task AModuleContractCaseHoldsThroughJsealAlone(string engine, string contractCase) =>
        ModuleContractCases[contractCase](ModuleContractProviders[engine]());

    [Theory]
    [MemberData(nameof(ModuleContractGapRows))]
    public async Task AModuleContractGapIsRecordedAndIsNotAPass(string engine, string contractCase)
    {
        // Whatever the gap, the registered provider claims neither flag and its realms are not IJsModules.
        using (var registered = Provider(engine).CreateRealm(JsRealmOptions.Default))
        {
            Assert.False(Provider(engine).Capabilities.HasFlag(JsCapabilities.Modules), $"'{engine}' declares Modules before I13");
            Assert.False(Provider(engine).Capabilities.HasFlag(JsCapabilities.DynamicImport), $"'{engine}' declares DynamicImport before I13");
            Assert.False(registered is IJsModules, $"a realm from the registered '{engine}' provider implements IJsModules");
        }

        if (!ModuleContractProviders.TryGetValue(engine, out var gated))
        {
            Assert.True(ModuleContractEngineGaps.ContainsKey(engine), $"'{engine}' has no module adapter and no recorded gap");
            return;
        }

        var gap = ModuleCaseGaps[(engine, contractCase)];
        var provider = gated();
        Assert.False(provider.Capabilities.HasFlag(JsCapabilities.Modules), $"'{engine}' reports Modules while a contract case is a gap");

        Exception? failure = null;
        try
        {
            await ModuleContractCases[contractCase](provider);
        }
        catch (Exception caught)
        {
            failure = caught;
        }

        Assert.True(failure is not null, $"'{engine}' / {contractCase} now passes; remove its gap ({gap.Reason}).");

        if (gap.RefusedWith is { } refusal)
        {
            // An explicit refusal, not an assertion that caught a wrong answer.
            Assert.IsType(refusal, failure);
            if (failure is JsModuleException moduleRefusal)
            {
                Assert.Equal(gap.Phase, moduleRefusal.Phase);
                if (gap.Phase == JsModulePhase.Link)
                    Assert.Contains("is refused: ", moduleRefusal.Message);
            }
        }
        else
        {
            Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(failure);
        }
    }

    /// <summary>Every case is a gap or a pass for each engine, and every gap names a real case and engine.</summary>
    [Fact]
    public void EveryModuleContractGapNamesACaseAndAReason()
    {
        foreach (var ((engine, contractCase), gap) in ModuleCaseGaps)
        {
            Assert.True(ModuleContractCases.ContainsKey(contractCase), $"gap for unknown case '{contractCase}'");
            Assert.True(ModuleContractProviders.ContainsKey(engine), $"case gap for '{engine}', which has no adapter at all");
            Assert.False(string.IsNullOrWhiteSpace(gap.Reason));
        }

        foreach (var provider in JsEngineRegistry.All)
        {
            Assert.True(
                ModuleContractProviders.ContainsKey(provider.Name) ^ ModuleContractEngineGaps.ContainsKey(provider.Name),
                $"'{provider.Name}' must have a module adapter or a recorded engine gap, not both or neither.");
        }

        var rows = ModuleContractRows.Select(row => ((string)row[0], (string)row[1])).ToHashSet();
        var gapRows = ModuleContractGapRows.Select(row => ((string)row[0], (string)row[1])).ToHashSet();
        Assert.Empty(rows.Intersect(gapRows));
        Assert.Equal(JsEngineRegistry.All.Count() * ModuleContractCases.Count, rows.Count + gapRows.Count);
    }

    // ── the host and the event loop ────────────────────────────────────────────────────────────

    /// <summary>
    /// An in-memory host: <c>./name</c> resolves to <c>mem:/name</c>, sources come from a dictionary,
    /// and realm tasks wait in a queue the test runs.
    /// </summary>
    private sealed class MemoryModuleHost : IJsModuleHost
    {
        private readonly ConcurrentQueue<Action> _tasks = new();

        public Dictionary<string, string> Sources { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Labels { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TaskCompletionSource<JsModuleSource>> Deferred { get; } = new(StringComparer.Ordinal);
        public Func<JsModuleRequest, JsModuleResolution?>? ResolveFirst { get; set; }
        public List<JsModuleRequest> Resolved { get; } = [];
        public List<string> Loaded { get; } = [];
        public List<CancellationToken> LoadTokens { get; } = [];

        public MemoryModuleHost With(string name, string text)
        {
            Sources["mem:/" + name] = text;
            return this;
        }

        public int LoadsOf(string name) => Loaded.Count(key => key == "mem:/" + name);

        public JsModuleResolution Resolve(in JsModuleRequest request)
        {
            Resolved.Add(request);
            if (ResolveFirst?.Invoke(request) is { } answer)
                return answer;

            return request.Specifier.StartsWith("./", StringComparison.Ordinal)
                ? JsModuleResolution.Resolved(new JsModuleKey("mem:/" + request.Specifier[2..]))
                : JsModuleResolution.Failed(JsModuleFailure.UnsupportedSpecifier, $"'{request.Specifier}' is not relative");
        }

        public ValueTask<JsModuleSource> LoadAsync(JsModuleKey key, CancellationToken cancellationToken)
        {
            Loaded.Add(key.Value);
            LoadTokens.Add(cancellationToken);

            if (Deferred.TryGetValue(key.Value, out var deferred))
                return new ValueTask<JsModuleSource>(deferred.Task);

            return new ValueTask<JsModuleSource>(Sources.TryGetValue(key.Value, out var text)
                ? new JsModuleSource(text, Labels.GetValueOrDefault(key.Value))
                : JsModuleSource.Failed(JsModuleFailure.NotFound, $"no module at '{key}'"));
        }

        public void QueueRealmTask(Action task) => _tasks.Enqueue(task);

        public bool HasQueuedTasks => !_tasks.IsEmpty;

        public bool RunQueued()
        {
            var ran = false;
            while (_tasks.TryDequeue(out var task))
            {
                task();
                ran = true;
            }

            return ran;
        }
    }

    /// <summary>The host's event loop: realm tasks and job checkpoints until both are empty.</summary>
    private static void RunUntilIdle(IJsRealm realm, MemoryModuleHost host)
    {
        for (var turn = 0; turn < 1_000; turn++)
        {
            var ranTasks = host.RunQueued();
            var ranJobs = realm.DrainJobs() > 0;
            if (!ranTasks && !ranJobs)
                return;
        }

        Assert.Fail("The realm did not become idle within 1,000 turns.");
    }

    private static (IJsRealm Realm, IJsModuleMap Map, MemoryModuleHost Host) OpenModules(
        IJsEngineProvider provider, MemoryModuleHost host, JsRealmOptions? options = null)
    {
        var realm = provider.CreateRealm(options ?? JsRealmOptions.Default);
        var modules = Assert.IsAssignableFrom<IJsModules>(realm);
        return (realm, modules.OpenModuleMap(host, new JsModuleOptions()), host);
    }

    private static async Task<IJsModule> LoadRoot(IJsRealm realm, IJsModuleMap map, MemoryModuleHost host, string specifier)
    {
        var loading = map.LoadAsync(specifier, JsModuleReferrer.Host(null));
        RunUntilIdle(realm, host);
        Assert.True(loading.IsCompleted, $"Loading '{specifier}' did not complete while the host ran its queue.");
        return await loading;
    }

    /// <summary>The settled state of a guest promise, observed through its own <c>then</c> after the loop idles.</summary>
    private static (string State, JsValue Value) Settlement(IJsRealm realm, MemoryModuleHost host, JsValue promise)
    {
        var state = "pending";
        var value = JsValue.Missing;
        var onFulfilled = realm.NewMethod("onFulfilled", (in JsCall call) => { state = "fulfilled"; value = call[0]; return JsValue.Undefined; }, 1);
        var onRejected = realm.NewMethod("onRejected", (in JsCall call) => { state = "rejected"; value = call[0]; return JsValue.Undefined; }, 1);
        realm.Invoke(realm.GetProperty(promise, "then"), promise, [onFulfilled, onRejected]);
        RunUntilIdle(realm, host);
        return (state, value);
    }

    private static JsValue Export(IJsRealm realm, IJsModule module, string name) =>
        realm.GetProperty(module.GetNamespace(), name);

    private static IJsModule Module(IJsModuleMap map, string name)
    {
        Assert.True(map.TryGetModule(new JsModuleKey("mem:/" + name), out var module), $"'{name}' is not a linked module");
        return module!;
    }

    // ── cases the contract requires ────────────────────────────────────────────────────────────

    private static async Task TwoModulesEvaluateAndSettleOnlyThroughTheJobQueue(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("dep", "export const x = 20; export function double(n) { return n * 2; } globalThis.order = (globalThis.order || '') + 'dep;';")
            .With("main", "import { x, double } from './dep'; export const result = double(x) + 2; globalThis.order += 'main;';"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("mem:/main", main.Key.Value);
        Assert.Equal("mem:/main", main.SourceLabel);
        Assert.Equal(JsModuleStatus.Linked, main.Status);
        Assert.Equal(JsModuleStatus.Linked, Module(map, "dep").Status);
        Assert.Equal(new[] { "mem:/main", "mem:/dep" }, host.Loaded);
        Assert.Equal(2, host.Resolved.Count);
        Assert.Equal(JsModuleRequestKind.HostRoot, host.Resolved[0].Kind);
        Assert.Equal(JsModuleRequestKind.Static, host.Resolved[1].Kind);
        Assert.Equal(JsModuleReferrer.Module(new JsModuleKey("mem:/main")), host.Resolved[1].Referrer);
        Assert.False(map.HasPendingLoads);

        // Loading does not evaluate.
        Assert.True(realm.GetProperty(realm.Global, "order").IsUndefined);

        var done = main.Evaluate();
        Assert.Equal(done, main.Evaluate());

        // Nothing is Evaluated, and nothing has fulfilled, before the host drains jobs.
        Assert.NotEqual(JsModuleStatus.Evaluated, main.Status);

        var (state, _) = Settlement(realm, host, done);
        Assert.Equal("fulfilled", state);
        Assert.Equal(JsModuleStatus.Evaluated, main.Status);
        Assert.Equal(JsModuleStatus.Evaluated, Module(map, "dep").Status);
        Assert.True(main.EvaluationError.IsMissing);
        Assert.Equal(42d, Export(realm, main, "result").AsNumber);
        Assert.Equal("dep;main;", realm.GetProperty(realm.Global, "order").AsString);
    }

    private static async Task AMissingModuleFailsTheLoadAndIsNotCached(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("main", "import { found } from './missing'; globalThis.mainRan = found;"));
        using var owned = realm;

        var loading = map.LoadAsync("./main", JsModuleReferrer.Host(null));
        RunUntilIdle(realm, host);
        var failure = await Assert.ThrowsAsync<JsModuleException>(async () => await loading);

        Assert.Equal(JsModulePhase.Load, failure.Phase);
        Assert.Equal("mem:/missing", failure.Key?.Value);
        Assert.Equal("./missing", failure.Specifier);
        Assert.Equal(JsModuleReferrer.Module(new JsModuleKey("mem:/main")), failure.Referrer);
        Assert.False(map.TryGetModule(new JsModuleKey("mem:/main"), out _));
        Assert.True(realm.GetProperty(realm.Global, "mainRan").IsUndefined);

        // Not cached: the host is asked again, and the source it now has is used. The module that did
        // load is kept rather than loaded twice.
        host.With("missing", "export const found = 'late';");
        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal(2, host.LoadsOf("missing"));
        Assert.Equal(1, host.LoadsOf("main"));

        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);
        Assert.Equal("late", realm.GetProperty(realm.Global, "mainRan").AsString);
    }

    private static async Task ARefusedResolutionFailsBeforeAnyLoad(IJsEngineProvider provider)
    {
        var host = new MemoryModuleHost().With("main", "import * as denied from './denied';").With("denied", "globalThis.deniedRan = true;");
        host.ResolveFirst = request => request.Specifier == "./denied"
            ? JsModuleResolution.Failed(JsModuleFailure.Refused, "policy")
            : null;
        var (realm, map, _) = OpenModules(provider, host);
        using var owned = realm;

        var loading = map.LoadAsync("./main", JsModuleReferrer.Host(null));
        RunUntilIdle(realm, host);
        var failure = await Assert.ThrowsAsync<JsModuleException>(async () => await loading);

        Assert.Equal(JsModulePhase.Resolve, failure.Phase);
        Assert.Equal("./denied", failure.Specifier);
        Assert.Null(failure.Key);
        Assert.Equal(0, host.LoadsOf("denied"));
    }

    private static async Task EveryPathToAModuleSeesOneNamespaceObject(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("dep", "export const value = 7;")
            .With("main", "import * as ns from './dep'; import { value } from './dep'; globalThis.seenNs = ns; globalThis.seenValue = value;"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);

        var dep = Module(map, "dep");
        var ns = dep.GetNamespace();
        Assert.True(ns.IsObject);
        Assert.Equal(ns, dep.GetNamespace());
        Assert.Equal(ns, realm.GetProperty(realm.Global, "seenNs"));
        Assert.Equal(7d, realm.GetProperty(realm.Global, "seenValue").AsNumber);

        // A second host load of either module answers the same record, and loads nothing.
        Assert.Same(dep, await LoadRoot(realm, map, host, "./dep"));
        Assert.Same(main, await LoadRoot(realm, map, host, "./main"));
        Assert.Equal(1, host.LoadsOf("dep"));
    }

    private static async Task ASharedDependencyIsLoadedAndEvaluatedOnce(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("shared", "globalThis.sharedRuns = (globalThis.sharedRuns || 0) + 1; export const token = {};")
            .With("a", "import { token } from './shared'; export const fromA = token;")
            .With("b", "import { token } from './shared'; export const fromB = token;")
            .With("main", "import { fromA } from './a'; import { fromB } from './b'; export const same = fromA === fromB;")
            .With("later", "import { token } from './shared'; export const again = token;"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);
        Assert.True(Export(realm, main, "same").AsBoolean);

        var later = await LoadRoot(realm, map, host, "./later");
        Assert.Equal("fulfilled", Settlement(realm, host, later.Evaluate()).State);
        Assert.Equal(Export(realm, Module(map, "a"), "fromA"), Export(realm, later, "again"));
        Assert.Equal(1d, realm.GetProperty(realm.Global, "sharedRuns").AsNumber);
        Assert.Equal(1, host.LoadsOf("shared"));
    }

    private static async Task AnEvaluationErrorIsCachedAndSharedByEveryImporter(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("boom", "globalThis.boomRuns = (globalThis.boomRuns || 0) + 1; throw new RangeError('boom');")
            .With("main", "import * as boom from './boom'; globalThis.mainRan = true;")
            .With("second", "import * as boom from './boom'; globalThis.secondRan = true;"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        var first = main.Evaluate();
        var (state, thrown) = Settlement(realm, host, first);

        Assert.Equal("rejected", state);
        Assert.Equal("RangeError", realm.ToJsString(realm.GetProperty(realm.GetProperty(thrown, "constructor"), "name")));
        Assert.Equal(JsModuleStatus.Errored, main.Status);
        Assert.Equal(JsModuleStatus.Errored, Module(map, "boom").Status);
        Assert.Equal(thrown, Module(map, "boom").EvaluationError);
        Assert.Equal(thrown, main.EvaluationError);
        Assert.Equal(first, main.Evaluate());
        Assert.True(realm.GetProperty(realm.Global, "mainRan").IsUndefined);

        // A later importer rejects with the identical value, and the body does not run again.
        var second = await LoadRoot(realm, map, host, "./second");
        var (secondState, secondThrown) = Settlement(realm, host, second.Evaluate());
        Assert.Equal("rejected", secondState);
        Assert.Equal(thrown, secondThrown);
        Assert.Equal(1d, realm.GetProperty(realm.Global, "boomRuns").AsNumber);
        Assert.True(realm.GetProperty(realm.Global, "secondRan").IsUndefined);
    }

    private static async Task AnEvaluationErrorStopsTheWalkBeforeALaterSibling(IJsEngineProvider provider)
    {
        // InnerModuleEvaluation stops at the first abrupt completion: a sibling after the thrower is
        // never visited, stays Linked, and runs only when something evaluates it later.
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("a", "throw new RangeError('a');")
            .With("b", "globalThis.bRuns = (globalThis.bRuns || 0) + 1; export const b = 1;")
            .With("main", "import * as a from './a'; import { b } from './b'; globalThis.mainRan = true;"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        var (state, thrown) = Settlement(realm, host, main.Evaluate());

        Assert.Equal("rejected", state);
        Assert.Equal("RangeError", realm.ToJsString(realm.GetProperty(realm.GetProperty(thrown, "constructor"), "name")));
        Assert.True(realm.GetProperty(realm.Global, "bRuns").IsUndefined, "b ran although its earlier sibling a threw");
        Assert.True(realm.GetProperty(realm.Global, "mainRan").IsUndefined);
        Assert.Equal(JsModuleStatus.Errored, main.Status);
        Assert.Equal(JsModuleStatus.Errored, Module(map, "a").Status);
        Assert.Equal(thrown, Module(map, "a").EvaluationError);
        Assert.Equal(JsModuleStatus.Linked, Module(map, "b").Status);
        Assert.True(Module(map, "b").EvaluationError.IsMissing);

        // Evaluating the untouched sibling now runs it, once.
        Assert.Equal("fulfilled", Settlement(realm, host, Module(map, "b").Evaluate()).State);
        Assert.Equal(1d, realm.GetProperty(realm.Global, "bRuns").AsNumber);
        Assert.Equal(JsModuleStatus.Evaluated, Module(map, "b").Status);
    }

    private static async Task AThrowInACycleLeavesAnUnreachedDependencyLinked(IJsEngineProvider provider)
    {
        // a and b form a cycle; b is visited (and throws) before a's second request, x, is reached.
        // Both cycle members are errored with b's error, and x is never visited.
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("a", "import * as b from './b'; import * as x from './x'; globalThis.aRan = true;")
            .With("b", "import * as a from './a'; throw new TypeError('b');")
            .With("x", "globalThis.xRan = true;"));
        using var owned = realm;

        var a = await LoadRoot(realm, map, host, "./a");
        var (state, thrown) = Settlement(realm, host, a.Evaluate());

        Assert.Equal("rejected", state);
        Assert.Equal("b", realm.ToJsString(realm.GetProperty(thrown, "message")));
        Assert.True(realm.GetProperty(realm.Global, "xRan").IsUndefined, "x ran although the walk stopped at b");
        Assert.True(realm.GetProperty(realm.Global, "aRan").IsUndefined);
        Assert.Equal(JsModuleStatus.Errored, a.Status);
        Assert.Equal(JsModuleStatus.Errored, Module(map, "b").Status);
        Assert.Equal(thrown, Module(map, "b").EvaluationError);
        Assert.Equal(JsModuleStatus.Linked, Module(map, "x").Status);
    }

    private static async Task ACycleMemberTakesTheErrorItsCycleRootRejectsWith(IJsEngineProvider provider)
    {
        // root and sync form a cycle whose root awaits a rejecting asynchronous sibling. sync's own
        // body ran and the language marks it evaluated with no error of its own, but ModuleEvaluate
        // sends a cycle member to its [[CycleRoot]], so evaluating sync answers the root's rejected
        // promise. Its status is therefore Errored, with the value that promise rejects with.
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("sync", "import * as root from './root'; globalThis.syncRuns = (globalThis.syncRuns || 0) + 1;")
            .With("slow", "await null; throw new TypeError('slow');")
            .With("root", "import * as sync from './sync'; import * as slow from './slow'; globalThis.rootRan = true;"));
        using var owned = realm;

        var root = await LoadRoot(realm, map, host, "./root");
        var (state, thrown) = Settlement(realm, host, root.Evaluate());

        Assert.Equal("rejected", state);
        Assert.Equal("slow", realm.ToJsString(realm.GetProperty(thrown, "message")));
        Assert.Equal(1d, realm.GetProperty(realm.Global, "syncRuns").AsNumber);
        Assert.True(realm.GetProperty(realm.Global, "rootRan").IsUndefined);
        Assert.Equal(JsModuleStatus.Errored, root.Status);
        Assert.Equal(thrown, root.EvaluationError);
        Assert.Equal(JsModuleStatus.Errored, Module(map, "slow").Status);

        // The cycle member ran, but its evaluation is its cycle root's, which rejected.
        Assert.Equal(JsModuleStatus.Errored, Module(map, "sync").Status);
        Assert.Equal(thrown, Module(map, "sync").EvaluationError);
        var again = Settlement(realm, host, Module(map, "sync").Evaluate());
        Assert.Equal("rejected", again.State);
        Assert.Equal(thrown, again.Value);
        Assert.Equal(1d, realm.GetProperty(realm.Global, "syncRuns").AsNumber);
    }

    private static async Task GuestErrorsUseTheSharedExceptionBoundary(IJsEngineProvider provider)
    {
        var host = new MemoryModuleHost()
            .With("broken", "export const = 1;")
            .With("main", "import * as broken from './broken';")
            .With("thrower", "export function fail() { throw new TypeError('from a module'); }");
        host.Labels["mem:/broken"] = "broken.js (test label)";
        var (realm, map, _) = OpenModules(provider, host);
        using var owned = realm;

        // A parse failure is a load-time JsModuleException carrying the shared boundary's exception.
        var loading = map.LoadAsync("./main", JsModuleReferrer.Host(null));
        RunUntilIdle(realm, host);
        var failure = await Assert.ThrowsAsync<JsModuleException>(async () => await loading);
        Assert.Equal(JsModulePhase.Parse, failure.Phase);
        Assert.Equal("mem:/broken", failure.Key?.Value);
        Assert.Equal("broken.js (test label)", failure.SourceLabel);
        var syntax = Assert.IsType<JsEngineException>(failure.InnerException);
        Assert.Equal("broken.js (test label)", syntax.SourceLabel);
        Assert.Equal(failure.Thrown, syntax.Thrown);
        Assert.Equal("SyntaxError", realm.ToJsString(realm.GetProperty(realm.GetProperty(syntax.Thrown, "constructor"), "name")));

        // A guest throw from module code, reached from the host, is a JsEngineException.
        var thrower = await LoadRoot(realm, map, host, "./thrower");
        Assert.Equal("fulfilled", Settlement(realm, host, thrower.Evaluate()).State);
        var guest = Assert.Throws<JsEngineException>(() => realm.Invoke(Export(realm, thrower, "fail"), JsValue.Undefined));
        Assert.Equal("from a module", realm.ToJsString(realm.GetProperty(guest.Thrown, "message")));
    }

    private static async Task EverySpecifierReachesTheHostsResolve(IJsEngineProvider provider)
    {
        var host = new MemoryModuleHost()
            .With("dep", "export const dep = 1;")
            .With("first", "import * as dep from './dep';")
            .With("builtin", "import m from 'module';")
            .With("clr", "import c from 'clr';")
            .With("spelled", "import { dep } from 'mem:/dep';");
        var (realm, map, _) = OpenModules(provider, host);
        using var owned = realm;

        await LoadRoot(realm, map, host, "./first");

        // Each is refused by the host, so each fails, even though the engine knows 'module' and 'clr',
        // and 'mem:/dep' is the text of a key already in the map.
        foreach (var (root, specifier) in new[] { ("./builtin", "module"), ("./clr", "clr"), ("./spelled", "mem:/dep") })
        {
            var loading = map.LoadAsync(root, JsModuleReferrer.Host(null));
            RunUntilIdle(realm, host);
            var failure = await Assert.ThrowsAsync<JsModuleException>(async () => await loading);
            Assert.Equal(JsModulePhase.Resolve, failure.Phase);
            Assert.Equal(specifier, failure.Specifier);
            Assert.Contains(host.Resolved, request => request.Specifier == specifier);
        }

        // And a host that does answer 'module' gets its own module, not the engine's.
        host.ResolveFirst = request => request.Specifier == "module"
            ? JsModuleResolution.Resolved(new JsModuleKey("module"))
            : null;
        host.Sources["module"] = "export default 'the host module';";
        var builtin = await LoadRoot(realm, map, host, "./builtin");
        Assert.Equal("fulfilled", Settlement(realm, host, builtin.Evaluate()).State);
        Assert.True(map.TryGetModule(new JsModuleKey("module"), out var hostModule));
        Assert.Equal("the host module", Export(realm, hostModule!, "default").AsString);
    }

    private static Task AModuleRealmHasTheSameGlobalNamesAsAClassicRealm(IJsEngineProvider provider)
    {
        using var moduleRealm = provider.CreateRealm(JsRealmOptions.Default);
        using var classicRealm = Provider(provider.Name).CreateRealm(JsRealmOptions.Default);
        Assert.IsAssignableFrom<IJsModules>(moduleRealm);
        Assert.False(classicRealm is IJsModules);

        Assert.Equal(
            classicRealm.OwnPropertyNames(classicRealm.Global).Order(StringComparer.Ordinal),
            moduleRealm.OwnPropertyNames(moduleRealm.Global).Order(StringComparer.Ordinal));
        return Task.CompletedTask;
    }

    private static async Task ModulesLoadWithoutGuestEvalWhichStillGovernsEval(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("dep", "export const x = 1;")
            .With("main", "import { x } from './dep'; try { (0, eval)('1'); globalThis.evalRan = true; } catch (e) { globalThis.evalRefused = e instanceof SyntaxError; } export const y = x + 1;"),
            new JsRealmOptions { AllowGuestEval = false });
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);
        Assert.Equal(2d, Export(realm, main, "y").AsNumber);
        Assert.True(realm.GetProperty(realm.Global, "evalRefused").AsBoolean);
        Assert.True(realm.GetProperty(realm.Global, "evalRan").IsUndefined);
    }

    private static Task ARealmOpensAtMostOneMap(IJsEngineProvider provider)
    {
        using var realm = provider.CreateRealm(JsRealmOptions.Default);
        var modules = Assert.IsAssignableFrom<IJsModules>(realm);
        using var map = modules.OpenModuleMap(new MemoryModuleHost(), new JsModuleOptions());
        Assert.Throws<InvalidOperationException>(() => modules.OpenModuleMap(new MemoryModuleHost(), new JsModuleOptions()));
        return Task.CompletedTask;
    }

    private static async Task ACompletedHostLoadIsStillAppliedInARealmTask(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost().With("main", "export const ok = true;"));
        using var owned = realm;

        // The host's LoadAsync completed synchronously, and the result still waits for a realm task.
        var loading = map.LoadAsync("./main", JsModuleReferrer.Host(null));
        Assert.False(loading.IsCompleted);
        Assert.True(host.HasQueuedTasks);

        RunUntilIdle(realm, host);
        Assert.True((await loading).Status == JsModuleStatus.Linked);
    }

    private static async Task DisposingTheMapAbandonsItsLoads(IJsEngineProvider provider)
    {
        var host = new MemoryModuleHost();
        var never = new TaskCompletionSource<JsModuleSource>();
        host.Deferred["mem:/slow"] = never;
        var (realm, map, _) = OpenModules(provider, host);
        using var owned = realm;

        var loading = map.LoadAsync("./slow", JsModuleReferrer.Host(null));
        RunUntilIdle(realm, host);
        Assert.True(map.HasPendingLoads);

        map.Dispose();
        Assert.True(host.LoadTokens.Single().IsCancellationRequested);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await loading);
        Assert.Throws<ObjectDisposedException>(() => map.LoadAsync("./slow", JsModuleReferrer.Host(null)));

        // A late completion does nothing and does not throw.
        never.SetResult(new JsModuleSource("export const late = 1;"));
        RunUntilIdle(realm, host);
    }

    private static async Task ImportAttributesAreUnsupported(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("data", "export default 1;")
            .With("main", "import data from './data' with { type: 'json' };"));
        using var owned = realm;

        var loading = map.LoadAsync("./main", JsModuleReferrer.Host(null));
        RunUntilIdle(realm, host);
        var failure = await Assert.ThrowsAsync<JsModuleException>(async () => await loading);
        Assert.Equal(JsModulePhase.Resolve, failure.Phase);
        Assert.Equal("./data", failure.Specifier);
        Assert.Contains("UnsupportedAttributes", failure.Message);
        Assert.Equal(0, host.LoadsOf("data"));
    }

    private static async Task ModuleCodeIsStrict(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("main",
                "export const assigned = (() => { try { undeclaredInModule = 1; return 'sloppy'; } catch (e) { return e.constructor.name; } })();\n" +
                "export const thisInFunction = (function () { return typeof this; })();")
            .With("with", "export const k = 1;\nwith ({}) {}")
            .With("octal", "export const k = 010;"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);
        Assert.Equal("ReferenceError", Export(realm, main, "assigned").AsString);
        Assert.Equal("undefined", Export(realm, main, "thisInFunction").AsString);
        Assert.True(realm.GetProperty(realm.Global, "undeclaredInModule").IsUndefined);

        // Strict-only early errors fail the load, as the module's SyntaxError.
        foreach (var name in new[] { "./with", "./octal" })
        {
            var loading = map.LoadAsync(name, JsModuleReferrer.Host(null));
            RunUntilIdle(realm, host);
            var failure = await Assert.ThrowsAsync<JsModuleException>(async () => await loading);
            Assert.Equal(JsModulePhase.Parse, failure.Phase);
            Assert.Equal("SyntaxError", realm.ToJsString(realm.GetProperty(realm.GetProperty(failure.Thrown, "constructor"), "name")));
        }
    }

    // ── cases the contract requires that a pinned engine may not meet ──────────────────────────

    private static async Task LiveBindingsReachImporters(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("dep", "export let x = 1; export function bump() { x++; }")
            .With("main", "import { x, bump } from './dep'; const before = x; bump(); export const seen = before + ':' + x;"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);
        Assert.Equal("1:2", Export(realm, main, "seen").AsString);
        Assert.Equal(2d, Export(realm, Module(map, "dep"), "x").AsNumber);
    }

    private static async Task ACycleSeesATemporalDeadZoneThenTheLiveValue(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("a", "import { readA } from './b'; export const a = 'a1'; export const later = readA();")
            .With("b", "import { a } from './a'; export function readA() { return a; } " +
                       "try { a; globalThis.early = 'no TDZ'; } catch (e) { globalThis.early = e.constructor.name; }"));
        using var owned = realm;

        var a = await LoadRoot(realm, map, host, "./a");
        Assert.Equal("fulfilled", Settlement(realm, host, a.Evaluate()).State);
        Assert.Equal("ReferenceError", realm.GetProperty(realm.Global, "early").AsString);
        Assert.Equal("a1", Export(realm, a, "later").AsString);
        Assert.Equal(JsModuleStatus.Evaluated, Module(map, "b").Status);
        Assert.Equal(1, host.LoadsOf("a"));
    }

    private static async Task AnImporterRunsAfterItsDependencysTopLevelAwait(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("dep", "await null; globalThis.depFinished = true; export const v = 'after';")
            .With("main", "import { v } from './dep'; export const seen = v + ':' + globalThis.depFinished;"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        var done = main.Evaluate();
        Assert.Equal(JsModuleStatus.EvaluatingAsync, Module(map, "dep").Status);
        Assert.Equal("fulfilled", Settlement(realm, host, done).State);
        Assert.Equal("after:true", Export(realm, main, "seen").AsString);
    }

    private static async Task AnAsyncDependencyDoesNotDelayItsLaterSiblings(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("slow", "globalThis.order = ['slow:start']; await null; globalThis.order.push('slow:end');")
            .With("quick", "globalThis.order.push('quick');")
            .With("main", "import * as slow from './slow'; import * as quick from './quick'; globalThis.order.push('main'); export const order = globalThis.order.join(',');"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);
        Assert.Equal("slow:start,quick,slow:end,main", Export(realm, main, "order").AsString);
    }

    private static async Task AnAsyncRejectionLeavesTheSiblingsThatRanEvaluated(IJsEngineProvider provider)
    {
        // An asynchronous module's rejection does not stop the walk the way a synchronous throw does:
        // InnerModuleEvaluation has already gone on to quick, which ran and is evaluated, and only the
        // thrower and what waits on it (main) are errored when the rejection settles.
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("slow", "await null; throw new RangeError('slow');")
            .With("quick", "globalThis.quickRuns = (globalThis.quickRuns || 0) + 1; export const quick = 1;")
            .With("main", "import * as slow from './slow'; import { quick } from './quick'; globalThis.mainRan = true;"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        var (state, thrown) = Settlement(realm, host, main.Evaluate());

        Assert.Equal("rejected", state);
        Assert.Equal("RangeError", realm.ToJsString(realm.GetProperty(realm.GetProperty(thrown, "constructor"), "name")));
        Assert.Equal(1d, realm.GetProperty(realm.Global, "quickRuns").AsNumber);
        Assert.True(realm.GetProperty(realm.Global, "mainRan").IsUndefined);
        Assert.Equal(JsModuleStatus.Errored, main.Status);
        Assert.Equal(thrown, main.EvaluationError);
        Assert.Equal(JsModuleStatus.Errored, Module(map, "slow").Status);
        Assert.Equal(JsModuleStatus.Evaluated, Module(map, "quick").Status);
        Assert.True(Module(map, "quick").EvaluationError.IsMissing);

        // Evaluating the sibling again settles at once and does not run it a second time.
        Assert.Equal("fulfilled", Settlement(realm, host, Module(map, "quick").Evaluate()).State);
        Assert.Equal(1d, realm.GetProperty(realm.Global, "quickRuns").AsNumber);
    }

    private static async Task AnEarlierAsyncRejectionDoesNotHideTheModuleThatThrew(IJsEngineProvider provider)
    {
        // The walk visits slow (which awaits), then bad, which throws synchronously and stops it: c
        // is never visited. slow rejects later with its own error, which neither main (already
        // errored with bad's) nor bad takes.
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("slow", "await null; throw new RangeError('slow');")
            .With("bad", "throw new TypeError('bad');")
            .With("c", "globalThis.cRan = true;")
            .With("main", "import * as slow from './slow'; import * as bad from './bad'; import * as c from './c';"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        var (state, thrown) = Settlement(realm, host, main.Evaluate());

        Assert.Equal("rejected", state);
        Assert.Equal("TypeError: bad", realm.ToJsString(thrown));
        Assert.Equal(JsModuleStatus.Errored, Module(map, "bad").Status);
        Assert.Equal(thrown, Module(map, "bad").EvaluationError);
        Assert.Equal(JsModuleStatus.Errored, Module(map, "slow").Status);
        Assert.Equal("RangeError: slow", realm.ToJsString(Module(map, "slow").EvaluationError));
        Assert.Equal(thrown, main.EvaluationError);
        Assert.True(realm.GetProperty(realm.Global, "cRan").IsUndefined);
        Assert.Equal(JsModuleStatus.Linked, Module(map, "c").Status);
    }

    private static async Task AModuleWaitingOnTwoRejectionsTakesTheFirstInTime(IJsEngineProvider provider)
    {
        // mid waits on two asynchronous modules. late comes first in the walk but rejects last, so mid
        // and main are errored with early's error (AsyncModuleExecutionRejected keeps the first).
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("late", "await null; await null; await null; throw new RangeError('late');")
            .With("early", "await null; throw new TypeError('early');")
            .With("mid", "import * as late from './late'; import * as early from './early';")
            .With("main", "import * as mid from './mid';"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        var (state, thrown) = Settlement(realm, host, main.Evaluate());

        Assert.Equal("rejected", state);
        Assert.Equal("TypeError: early", realm.ToJsString(thrown));
        Assert.Equal(JsModuleStatus.Errored, Module(map, "mid").Status);
        Assert.Equal(thrown, Module(map, "mid").EvaluationError);
        Assert.Equal("RangeError: late", realm.ToJsString(Module(map, "late").EvaluationError));
        Assert.Equal(thrown, Module(map, "early").EvaluationError);
    }

    private static async Task AnImporterOfTheThrowerThatWasNotReachedStaysLinked(IJsEngineProvider provider)
    {
        // x requests a, but the walk reaches a first through main and stops there: only main and a
        // are on the stack when a throws, so x is never visited and stays Linked. Evaluating it later
        // meets a's cached error without running x's body.
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("a", "throw new RangeError('a');")
            .With("x", "import * as a from './a'; globalThis.xRan = true;")
            .With("main", "import * as a from './a'; import * as x from './x'; globalThis.mainRan = true;"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        var (state, thrown) = Settlement(realm, host, main.Evaluate());

        Assert.Equal("rejected", state);
        Assert.Equal(JsModuleStatus.Errored, main.Status);
        Assert.Equal(JsModuleStatus.Errored, Module(map, "a").Status);
        Assert.Equal(thrown, Module(map, "a").EvaluationError);
        Assert.Equal(JsModuleStatus.Linked, Module(map, "x").Status);
        Assert.True(Module(map, "x").EvaluationError.IsMissing);

        var (xState, xThrown) = Settlement(realm, host, Module(map, "x").Evaluate());
        Assert.Equal("rejected", xState);
        Assert.Equal(thrown, xThrown);
        Assert.True(realm.GetProperty(realm.Global, "xRan").IsUndefined);
        Assert.Equal(JsModuleStatus.Errored, Module(map, "x").Status);
    }

    private static async Task AnEqualPrimitiveFromAnAwaitingModuleDoesNotHideTheThrower(IJsEngineProvider provider)
    {
        // As AnEarlierAsyncRejectionDoesNotHideTheModuleThatThrew, but both throw the same string:
        // slow awaits and rejects later, bad throws synchronously and stops the walk before c.
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("slow", "await null; throw 'same';")
            .With("bad", "throw 'same';")
            .With("c", "globalThis.cRan = true;")
            .With("main", "import * as slow from './slow'; import * as bad from './bad'; import * as c from './c';"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        var (state, thrown) = Settlement(realm, host, main.Evaluate());

        Assert.Equal("rejected", state);
        Assert.Equal("same", thrown.AsString);
        Assert.Equal(JsModuleStatus.Errored, Module(map, "slow").Status);
        Assert.Equal(JsModuleStatus.Errored, Module(map, "bad").Status);
        Assert.Equal("same", Module(map, "bad").EvaluationError.AsString);
        Assert.True(realm.GetProperty(realm.Global, "cRan").IsUndefined);
        Assert.Equal(JsModuleStatus.Linked, Module(map, "c").Status);
    }

    /// <summary>Evaluates <paramref name="module"/> from inside a host function the host invokes, as an event handler would.</summary>
    private static JsValue EvaluateFromAHostCallback(IJsRealm realm, IJsModule module)
    {
        var started = JsValue.Missing;
        var start = realm.NewMethod("start", (in JsCall call) =>
        {
            started = module.Evaluate();
            return JsValue.Undefined;
        }, 0);

        realm.Invoke(start, JsValue.Undefined, []);
        Assert.False(started.IsMissing);
        return started;
    }

    private static async Task AnEvaluationStartedInAHostCallbackStopsAtItsThrower(IJsEngineProvider provider)
    {
        // AnEvaluationErrorStopsTheWalkBeforeALaterSibling, started from a host callback.
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("a", "throw new RangeError('a');")
            .With("b", "globalThis.bRuns = (globalThis.bRuns || 0) + 1; export const b = 1;")
            .With("main", "import * as a from './a'; import { b } from './b'; globalThis.mainRan = true;"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        var (state, thrown) = Settlement(realm, host, EvaluateFromAHostCallback(realm, main));

        Assert.Equal("rejected", state);
        Assert.True(realm.GetProperty(realm.Global, "bRuns").IsUndefined, "b ran although its earlier sibling a threw");
        Assert.Equal(JsModuleStatus.Errored, main.Status);
        Assert.Equal(JsModuleStatus.Errored, Module(map, "a").Status);
        Assert.Equal(thrown, Module(map, "a").EvaluationError);
        Assert.Equal(JsModuleStatus.Linked, Module(map, "b").Status);
        Assert.True(Module(map, "b").EvaluationError.IsMissing);
    }

    private static async Task AnAsyncRejectionInAHostCallbackLeavesTheSiblingsThatRanEvaluated(IJsEngineProvider provider)
    {
        // AnAsyncRejectionLeavesTheSiblingsThatRanEvaluated, started from a host callback.
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("slow", "await null; throw new RangeError('slow');")
            .With("quick", "globalThis.quickRuns = (globalThis.quickRuns || 0) + 1; export const quick = 1;")
            .With("main", "import * as slow from './slow'; import { quick } from './quick'; globalThis.mainRan = true;"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        var (state, thrown) = Settlement(realm, host, EvaluateFromAHostCallback(realm, main));

        Assert.Equal("rejected", state);
        Assert.Equal(1d, realm.GetProperty(realm.Global, "quickRuns").AsNumber);
        Assert.Equal(JsModuleStatus.Errored, main.Status);
        Assert.Equal(JsModuleStatus.Errored, Module(map, "slow").Status);
        Assert.Equal(thrown, Module(map, "slow").EvaluationError);
        Assert.Equal(JsModuleStatus.Evaluated, Module(map, "quick").Status);
        Assert.True(Module(map, "quick").EvaluationError.IsMissing);
    }

    private static async Task TheNamespaceIsAvailableAsSoonAsTheModuleIsLinked(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost().With("dep", "export const x = 1;"));
        using var owned = realm;

        var dep = await LoadRoot(realm, map, host, "./dep");
        var ns = dep.GetNamespace();
        Assert.True(ns.IsObject);
        Assert.Equal("fulfilled", Settlement(realm, host, dep.Evaluate()).State);
        Assert.Equal(ns, dep.GetNamespace());
    }

    private static async Task TheNamespaceIsAModuleNamespaceExoticObject(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost().With("dep", "export const b = 1; export const a = 2;"));
        using var owned = realm;

        var dep = await LoadRoot(realm, map, host, "./dep");
        Assert.Equal("fulfilled", Settlement(realm, host, dep.Evaluate()).State);
        var ns = dep.GetNamespace();
        realm.DefineValue(realm.Global, "probedNamespace", ns);

        Assert.Equal("[object Module]", realm.EvaluateHostScript("Object.prototype.toString.call(probedNamespace)", "test:ns-tag").AsString);
        Assert.Equal(new[] { "a", "b" }, realm.OwnPropertyNames(ns));
        Assert.False(realm.EvaluateHostScript("Object.isExtensible(probedNamespace)", "test:ns-extensible").AsBoolean);

        // A strict write to an export, or of a new name, throws and changes nothing.
        Assert.Equal("TypeError,TypeError", realm.EvaluateHostScript(
            "'use strict'; [() => { probedNamespace.a = 99; }, () => { probedNamespace.extra = 1; }]" +
            ".map(write => { try { write(); return 'written'; } catch (e) { return e.constructor.name; } }).join()",
            "test:ns-write").AsString);
        Assert.Equal(2d, realm.GetProperty(ns, "a").AsNumber);
    }

    private static async Task ASideEffectOnlyImportLoadsItsDependency(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("effect", "globalThis.effectRan = true;")
            .With("main", "import './effect'; export const after = globalThis.effectRan;"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);
        Assert.True(Export(realm, main, "after").AsBoolean);
    }

    private static async Task AMissingExportIsALinkError(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("dep", "export const present = 1;")
            .With("main", "import { absent } from './dep'; globalThis.absentWas = typeof absent;"));
        using var owned = realm;

        var loading = map.LoadAsync("./main", JsModuleReferrer.Host(null));
        RunUntilIdle(realm, host);
        var failure = await Assert.ThrowsAsync<JsModuleException>(async () => await loading);
        Assert.Equal(JsModulePhase.Link, failure.Phase);
        Assert.True(realm.GetProperty(realm.Global, "absentWas").IsUndefined);
    }

    private static async Task TopLevelDeclarationsAreNotGlobalProperties(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("main", "var hiddenVar = 1; function hiddenFunction() {} export const seen = typeof globalThis.hiddenVar + ',' + typeof globalThis.hiddenFunction;"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);
        Assert.Equal("undefined,undefined", Export(realm, main, "seen").AsString);
        Assert.DoesNotContain("hiddenVar", realm.OwnPropertyNames(realm.Global));
    }

    private static async Task ModulesDoNotShareTopLevelVarBindings(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("dep", "var count = 1; function helper() { return 'dep'; } export function read() { return count + ':' + helper(); }")
            .With("main", "import { read } from './dep'; var count = 99; function helper() { return 'main'; } export const seen = read() + ',' + count + ':' + helper();"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);
        Assert.Equal("1:dep,99:main", Export(realm, main, "seen").AsString);
    }

    private static async Task ModuleCodeHasNoCommonJsBindings(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("main", "export const kinds = [typeof module, typeof exports, typeof require, typeof __dirname].join();"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);
        Assert.Equal("undefined,undefined,undefined,undefined", Export(realm, main, "kinds").AsString);
        Assert.Equal(new[] { "kinds" }, realm.OwnPropertyNames(main.GetNamespace()));
    }

    private static async Task TopLevelThisIsUndefinedAndArgumentsIsUnbound(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("main", "export const kinds = typeof this + ',' + typeof arguments;"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);
        Assert.Equal("undefined,undefined", Export(realm, main, "kinds").AsString);
    }
}
