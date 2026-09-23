namespace Broiler.JSeal.Tests;

/// <summary>
/// I12: guest <c>import()</c> routed through the realm's module map, as provider-neutral module
/// contract cases (registered in <see cref="ModuleContractCases"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What the contract asks of <c>import()</c></b> (<c>docs/jseal.modules.md</c>): the specifier
/// reaches the host's <see cref="IJsModuleHost.Resolve"/> with the calling module's key (or, from a
/// script, a script referrer), the module is loaded through the same map and so is the same record
/// and namespace a static import reaches, concurrent requests share one load and one evaluation,
/// every failure rejects the import's promise, and the promise settles only through the job queue
/// while the host runs its loop. Module loading is not guest evaluation: a realm without
/// <see cref="JsCapabilities.GuestEval"/> still imports.
/// </para>
/// <para>
/// <b>A specifier the calling module also imports statically</b> is answered by the language from
/// that module's own requests, which is why the reuse case below asks for one of each kind.
/// </para>
/// <para>
/// <b>What the contract deliberately does not ask.</b> The referrer of code that a promise job runs
/// with nothing of its own below it on the stack - <c>p.then(eval)</c> and <c>p.then(Function)</c> -
/// is host-defined, not the language's. The pinned ES2026 text reserves a job callback that carries
/// the enqueuing script or module to web browsers (HostMakeJobCallback and HostCallJobCallback: an
/// "ECMAScript host that is not a web browser must use the default implementation", whose
/// [[HostDefined]] is empty), so under the default hooks GetActiveScriptOrModule is null inside such
/// a job and EvaluateImportCall falls back to the current Realm Record. Node v24 rejects
/// <c>p.then(eval)</c> for exactly that reason while resolving indirect <c>eval</c> called from
/// module code, and Broiler.VM answers the enqueuing module. No case here pins either answer; see
/// the open question in <c>docs/jseal.modules.md</c>.
/// </para>
/// </remarks>
public partial class JsealConformanceTests
{
    /// <summary>A module whose <c>load</c> export imports whatever it is asked for.</summary>
    private const string LoaderModule =
        "import * as dep from './dep'; export const depNs = dep; export function load(name) { return import(name); }";

    private static JsValue Load(IJsRealm realm, IJsModule loader, string specifier) =>
        realm.Invoke(Export(realm, loader, "load"), JsValue.Undefined, [JsValue.String(specifier)]);

    private static string ErrorName(IJsRealm realm, JsValue error) =>
        realm.ToJsString(realm.GetProperty(realm.GetProperty(error, "constructor"), "name"));

    private static async Task DynamicImportReachesTheNamespaceAStaticImportDoes(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("dep", "globalThis.depRuns = (globalThis.depRuns || 0) + 1; export const value = 7;")
            .With("late", "import { value } from './dep'; globalThis.lateRuns = (globalThis.lateRuns || 0) + 1; export const seen = value;")
            .With("main", LoaderModule));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);

        // Asynchronous: nothing has settled when import() returns, and the host was asked with the
        // calling module's key.
        var importing = Load(realm, main, "./late");
        Assert.True(importing.IsObject);
        Assert.Contains(host.Resolved, request =>
            request.Kind == JsModuleRequestKind.Dynamic &&
            request.Specifier == "./late" &&
            request.Referrer == JsModuleReferrer.Module(new JsModuleKey("mem:/main")));

        var (state, ns) = Settlement(realm, host, importing);
        Assert.Equal("fulfilled", state);
        Assert.True(map.TryGetModule(new JsModuleKey("mem:/late"), out var late));
        Assert.Equal(late!.GetNamespace(), ns);
        Assert.Equal(7d, realm.GetProperty(ns, "seen").AsNumber);
        Assert.Equal(JsModuleStatus.Evaluated, late.Status);

        // The static dependency is the one record: its namespace from a dynamic import is the one a
        // static import bound, and no module loaded or ran twice.
        var (depState, depNs) = Settlement(realm, host, Load(realm, main, "./dep"));
        Assert.Equal("fulfilled", depState);
        Assert.Equal(Export(realm, main, "depNs"), depNs);
        Assert.Equal(Module(map, "dep").GetNamespace(), depNs);

        var (againState, again) = Settlement(realm, host, Load(realm, main, "./late"));
        Assert.Equal("fulfilled", againState);
        Assert.Equal(ns, again);
        Assert.Equal(1d, realm.GetProperty(realm.Global, "depRuns").AsNumber);
        Assert.Equal(1d, realm.GetProperty(realm.Global, "lateRuns").AsNumber);
        Assert.Equal(1, host.LoadsOf("dep"));
        Assert.Equal(1, host.LoadsOf("late"));
    }

    private static async Task ConcurrentDynamicImportsShareOneLoadAndOneEvaluation(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("dep", "export const unused = 0;")
            .With("x", "globalThis.xRuns = (globalThis.xRuns || 0) + 1; export const token = {};")
            .With("main", LoaderModule + " export function both() { return Promise.all([import('./x'), import('./x')]); }"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);

        var (state, pair) = Settlement(realm, host, realm.Invoke(Export(realm, main, "both"), JsValue.Undefined));
        Assert.Equal("fulfilled", state);
        Assert.Equal(realm.GetIndex(pair, 0), realm.GetIndex(pair, 1));
        Assert.Equal(Module(map, "x").GetNamespace(), realm.GetIndex(pair, 0));
        Assert.Equal(1d, realm.GetProperty(realm.Global, "xRuns").AsNumber);
        Assert.Equal(1, host.LoadsOf("x"));
    }

    private static async Task EveryDynamicImportFailureRejectsTheImport(IJsEngineProvider provider)
    {
        var host = new MemoryModuleHost()
            .With("dep", "export const unused = 0;")
            .With("broken", "export const = 1;")
            .With("throws", "globalThis.throwsRuns = (globalThis.throwsRuns || 0) + 1; throw new RangeError('boom');")
            .With("denied", "globalThis.deniedRan = true;")
            .With("main", LoaderModule);
        host.ResolveFirst = request => request.Specifier == "./denied"
            ? JsModuleResolution.Failed(JsModuleFailure.Refused, "policy")
            : null;
        var (realm, map, _) = OpenModules(provider, host);
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);

        foreach (var (specifier, error) in new[] { ("./missing", "TypeError"), ("./denied", "TypeError"), ("./broken", "SyntaxError"), ("./throws", "RangeError") })
        {
            var (state, reason) = Settlement(realm, host, Load(realm, main, specifier));
            Assert.Equal((specifier, "rejected"), (specifier, state));
            Assert.Equal((specifier, error), (specifier, ErrorName(realm, reason)));
        }

        Assert.Equal(0, host.LoadsOf("denied"));
        Assert.True(realm.GetProperty(realm.Global, "deniedRan").IsUndefined);
        Assert.False(map.TryGetModule(new JsModuleKey("mem:/missing"), out _));

        // An evaluation error is cached: the same value again, and the body does not run again.
        var first = Settlement(realm, host, Load(realm, main, "./throws")).Value;
        var second = Settlement(realm, host, Load(realm, main, "./throws")).Value;
        Assert.Equal(first, second);
        Assert.Equal(1d, realm.GetProperty(realm.Global, "throwsRuns").AsNumber);
        Assert.Equal(JsModuleStatus.Errored, Module(map, "throws").Status);

        // A load failure is not cached: the host is asked again, and the module it now has is used.
        host.With("missing", "export const found = 'late';");
        var (lateState, lateNs) = Settlement(realm, host, Load(realm, main, "./missing"));
        Assert.Equal("fulfilled", lateState);
        Assert.Equal("late", realm.GetProperty(lateNs, "found").AsString);
    }

    private static async Task ADynamicImportStopsAtItsGraphsFirstEvaluationError(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("dep", "export const unused = 0;")
            .With("a", "throw new RangeError('a');")
            .With("b", "globalThis.bRuns = (globalThis.bRuns || 0) + 1; export const b = 1;")
            .With("pair", "import * as a from './a'; import { b } from './b'; globalThis.pairRan = true;")
            .With("main", LoaderModule));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);

        var (state, reason) = Settlement(realm, host, Load(realm, main, "./pair"));
        Assert.Equal("rejected", state);
        Assert.Equal("RangeError", ErrorName(realm, reason));
        Assert.True(realm.GetProperty(realm.Global, "bRuns").IsUndefined, "b ran although its earlier sibling a threw");
        Assert.True(realm.GetProperty(realm.Global, "pairRan").IsUndefined);
        Assert.Equal(JsModuleStatus.Errored, Module(map, "pair").Status);
        Assert.Equal(JsModuleStatus.Errored, Module(map, "a").Status);
        Assert.Equal(JsModuleStatus.Linked, Module(map, "b").Status);

        // The untouched sibling runs when it is imported itself.
        Assert.Equal("fulfilled", Settlement(realm, host, Load(realm, main, "./b")).State);
        Assert.Equal(1d, realm.GetProperty(realm.Global, "bRuns").AsNumber);
    }

    private static async Task ImportInEvalAndFunctionCodeCarriesTheCallingModulesKey(IJsEngineProvider provider)
    {
        // GetActiveScriptOrModule: indirect eval code and a function the Function constructor made
        // have no script or module of their own, so their import() is resolved against the module
        // whose code is on the stack below them.
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("dep", "export const unused = 0;")
            .With("late", "export const value = 'late';")
            .With("main", LoaderModule +
                " export function viaEval(name) { return (0, eval)('import(' + JSON.stringify(name) + ')'); }" +
                " export function viaFunction(name) { return Function('name', 'return import(name)')(name); }"));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);

        foreach (var route in new[] { "viaEval", "viaFunction" })
        {
            host.Resolved.Clear();
            var (state, ns) = Settlement(realm, host, realm.Invoke(Export(realm, main, route), JsValue.Undefined, [JsValue.String("./late")]));
            Assert.Equal((route, "fulfilled"), (route, state));
            Assert.Equal(Module(map, "late").GetNamespace(), ns);
            Assert.Contains(host.Resolved, request =>
                request.Kind == JsModuleRequestKind.Dynamic &&
                request.Referrer == JsModuleReferrer.Module(new JsModuleKey("mem:/main")));
        }
    }

    private static async Task NestedDynamicImportsCarryTheCallingModulesKey(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("dep", "export const unused = 0;")
            .With("outer", "export const inner = import('./inner');")
            .With("inner", "export const depth = 2;")
            .With("main", LoaderModule));
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);

        var (state, outer) = Settlement(realm, host, Load(realm, main, "./outer"));
        Assert.Equal("fulfilled", state);
        Assert.Contains(host.Resolved, request =>
            request.Kind == JsModuleRequestKind.Dynamic &&
            request.Specifier == "./inner" &&
            request.Referrer == JsModuleReferrer.Module(new JsModuleKey("mem:/outer")));

        var (innerState, inner) = Settlement(realm, host, realm.GetProperty(outer, "inner"));
        Assert.Equal("fulfilled", innerState);
        Assert.Equal(Module(map, "inner").GetNamespace(), inner);
        Assert.Equal(2d, realm.GetProperty(inner, "depth").AsNumber);
    }

    private static async Task DynamicImportIsNotGuestEvaluation(IJsEngineProvider provider)
    {
        var (realm, map, host) = OpenModules(provider, new MemoryModuleHost()
            .With("dep", "export const unused = 0;")
            .With("late", "export const evalRefused = (() => { try { (0, eval)('1'); return false; } catch (e) { return e instanceof SyntaxError; } })();")
            .With("main", LoaderModule),
            new JsRealmOptions { AllowGuestEval = false });
        using var owned = realm;

        Assert.False(realm.Capabilities.HasFlag(JsCapabilities.GuestEval));
        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);

        var (state, ns) = Settlement(realm, host, Load(realm, main, "./late"));
        Assert.Equal("fulfilled", state);
        Assert.True(realm.GetProperty(ns, "evalRefused").AsBoolean);
    }

    private static async Task DynamicImportFollowsTheMapsOptions(IJsEngineProvider provider)
    {
        // Allowed from a script by default: the referrer is a script, not a module.
        {
            var (realm, map, host) = OpenModules(provider, new MemoryModuleHost().With("dep", "export const value = 3;"));
            using var owned = realm;

            realm.EvaluateHostScript("globalThis.fromScript = import('./dep');", "test:dynamic-from-script");
            var (state, ns) = Settlement(realm, host, realm.GetProperty(realm.Global, "fromScript"));
            Assert.Equal("fulfilled", state);
            Assert.Equal(3d, realm.GetProperty(ns, "value").AsNumber);
            Assert.Equal(Module(map, "dep").GetNamespace(), ns);

            // The referrer is the calling script's own label, which is what a host resolves a
            // relative specifier against; a classic script's likewise.
            realm.EvaluateClassicScript("globalThis.fromClassic = import('./dep');", "https://example.test/classic.js");
            Assert.Equal("fulfilled", Settlement(realm, host, realm.GetProperty(realm.Global, "fromClassic")).State);
            var fromScripts = host.Resolved.Where(request => request.Kind == JsModuleRequestKind.Dynamic).Select(request => request.Referrer).ToArray();
            Assert.Equal(
                new[] { JsModuleReferrer.Script("test:dynamic-from-script"), JsModuleReferrer.Script("https://example.test/classic.js") },
                fromScripts);
        }

        // Refused from scripts, and entirely: the import rejects and the host is never asked.
        foreach (var options in new[]
        {
            new JsModuleOptions { AllowImportFromScripts = false },
            new JsModuleOptions { AllowDynamicImport = false },
        })
        {
            var host = new MemoryModuleHost().With("dep", "export const value = 3;");
            using var realm = provider.CreateRealm(JsRealmOptions.Default);
            using var map = Assert.IsAssignableFrom<IJsModules>(realm).OpenModuleMap(host, options);

            realm.EvaluateHostScript("globalThis.fromScript = import('./dep');", "test:dynamic-refused");
            var (state, reason) = Settlement(realm, host, realm.GetProperty(realm.Global, "fromScript"));
            Assert.Equal("rejected", state);
            Assert.Equal("TypeError", ErrorName(realm, reason));
            Assert.DoesNotContain(host.Resolved, request => request.Kind == JsModuleRequestKind.Dynamic);
            Assert.Equal(0, host.LoadsOf("dep"));
        }
    }

    private static async Task DisposingTheRealmAbandonsADeferredImport(IJsEngineProvider provider)
    {
        var host = new MemoryModuleHost().With("dep", "export const unused = 0;").With("main", LoaderModule);
        var never = new TaskCompletionSource<JsModuleSource>();
        host.Deferred["mem:/slow"] = never;
        var (realm, map, _) = OpenModules(provider, host);

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);

        var importing = Load(realm, main, "./slow");
        RunUntilIdle(realm, host);
        Assert.True(map.HasPendingLoads);
        Assert.Equal("pending", Settlement(realm, host, importing).State);

        realm.Dispose();
        Assert.True(host.LoadTokens.Last().IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => map.LoadAsync("./slow", JsModuleReferrer.Host(null)));

        // A late completion does nothing and does not throw.
        never.SetResult(new JsModuleSource("export const late = 1;"));
        host.RunQueued();
        Assert.Equal("mem:/main", main.Key.Value);
    }

    private static async Task DisposingTheMapRejectsADeferredImport(IJsEngineProvider provider)
    {
        var host = new MemoryModuleHost().With("dep", "export const unused = 0;").With("main", LoaderModule);
        var never = new TaskCompletionSource<JsModuleSource>();
        host.Deferred["mem:/slow"] = never;
        var (realm, map, _) = OpenModules(provider, host);
        using var owned = realm;

        var main = await LoadRoot(realm, map, host, "./main");
        Assert.Equal("fulfilled", Settlement(realm, host, main.Evaluate()).State);

        var importing = Load(realm, main, "./slow");
        RunUntilIdle(realm, host);

        map.Dispose();
        Assert.True(host.LoadTokens.Last().IsCancellationRequested);
        var (state, reason) = Settlement(realm, host, importing);
        Assert.Equal("rejected", state);
        Assert.Equal("TypeError", ErrorName(realm, reason));

        // The realm lives on, and a late completion does nothing.
        never.SetResult(new JsModuleSource("export const late = 1;"));
        RunUntilIdle(realm, host);
        Assert.Equal(2d, realm.EvaluateHostScript("1 + 1", "test:after-map-disposal").AsNumber);
    }
}
