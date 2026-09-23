using System.Collections.Concurrent;

using Broiler.JSeal.BroilerJs;

namespace Broiler.JSeal.Tests;

/// <summary>
/// The Broiler.JS module adapter's own refusal - a module that calls <c>import()</c> - and the graphs
/// Broiler.JS 0.1.0-preview.1 made it refuse, which 0.1.0-preview.3 runs as ECMAScript says.
/// </summary>
/// <remarks>
/// Engine-specific, so these live beside the other Broiler.JS-only tests rather than in the shared
/// contract cases (<c>JsealConformanceTests.Modules.cs</c>), which assert the contract itself.
/// </remarks>
public class BroilerJsModuleAdapterTests
{
    [Theory]
    [InlineData("export const load = () => import('./other');")]
    // Nodes the engine's own AstReduce walker skips: object literal members, switch cases, parameter defaults.
    [InlineData("const o = { m() { return import('./other'); } };")]
    [InlineData("switch (1) { case 1: import('./other'); }")]
    [InlineData("function f(p = import('./other')) {}")]
    public async Task AModuleThatCallsImportIsRefusedAtLinkWithTheReason(string source)
    {
        var host = new Host { ["mem:/main"] = source, ["mem:/other"] = "export const o = 1;" };
        using var realm = ModuleRealm(out var map, host);

        var failure = await Assert.ThrowsAsync<JsModuleException>(() => host.Load(realm, map, "./main"));
        Assert.Equal(JsModulePhase.Link, failure.Phase);
        Assert.Equal("mem:/main", failure.Key?.Value);
        Assert.Contains("import() is not routed", failure.Message);
        Assert.False(map.TryGetModule(new JsModuleKey("mem:/main"), out _));
    }

    /// <summary>
    /// Each graph here was refused at link time on Broiler.JS 0.1.0-preview.1, which imported copies,
    /// ran a dependency where its declaration stood, and compiled module code as a CommonJS function
    /// body. It now links and evaluates, and <c>seen</c> is ECMAScript's answer.
    /// </summary>
    [Theory]
    // Exports are live bindings, whoever assigns them and however.
    [InlineData("export let x = 1; x = 2; export const seen = String(x);", "2")]
    [InlineData("export var x = 1; function set() { [x] = [2]; } set(); export const seen = String(x);", "2")]
    [InlineData("export function f() {} f = null; export const seen = String(f);", "null")]
    [InlineData("let y = 1; export { y as z }; for (y of [2]); export const seen = String(y);", "2")]
    [InlineData("export let x = 1; eval('x = 2'); export const seen = String(x);", "2")]
    [InlineData("export { x }; let x = 1; export const seen = String(x);", "1")]
    [InlineData("export { i }; for (var i = 0; i < 3; i++); export const seen = String(i);", "3")]
    // A static import is evaluated before the module body, wherever it stands.
    [InlineData("globalThis.first = 1; import * as other from './other'; export const seen = other.o + ':' + globalThis.first;", "1:1")]
    // Module code has no CommonJS bindings, and its top-level this and arguments are ECMAScript's.
    [InlineData("export const seen = [typeof module, typeof exports, typeof require, typeof __dirname, typeof __fileame].join();", "undefined,undefined,undefined,undefined,undefined")]
    [InlineData("let seen; try { require('./other'); } catch (e) { seen = e.name; } export { seen };", "ReferenceError")]
    [InlineData("let seen; try { exports.injected = 2; } catch (e) { seen = e.name; } export { seen };", "ReferenceError")]
    [InlineData("let seen; try { eval('module.exports = {}'); } catch (e) { seen = e.name; } export { seen };", "ReferenceError")]
    [InlineData("export const seen = typeof this + ',' + typeof arguments;", "undefined,undefined")]
    [InlineData("const f = () => this; export const seen = typeof f();", "undefined")]
    public async Task AGraphPreview1RefusedRunsAsEcmaScriptSays(string source, string expected)
    {
        var host = new Host { ["mem:/main"] = source, ["mem:/other"] = "export const o = 1;" };
        using var realm = ModuleRealm(out var map, host);

        var main = await host.Load(realm, map, "./main");
        main.Evaluate();
        host.RunUntilIdle(realm);
        Assert.Equal(JsModuleStatus.Evaluated, main.Status);
        Assert.Equal(expected, realm.GetProperty(main.GetNamespace(), "seen").AsString);
    }

    [Theory]
    [InlineData("export let x = 1; export const y = x + 1;")]
    [InlineData("function f() { return 1; } export { f as g };")]
    [InlineData("let y = 1; export { y };")]
    [InlineData("export { f }; function f() { return 1; }")]
    [InlineData("import * as other from './other'; export const eager = other.o; function local() { let x = 0; x++; return x; }")]
    [InlineData("export const f = () => { var o = {}; o.x = 1; return o; };")]
    [InlineData("const o = { module: 1, exports: 2 }; export const k = o.module + o.exports;")]
    [InlineData("export const f = function () { return typeof this + typeof arguments; };")]
    [InlineData("export class C { field = this; method() { return this; } }")]
    // Near misses for the one refusal: neither is a call of import().
    [InlineData("export const meta = typeof import.meta;")]
    [InlineData("const o = { import: 1 }; export const k = o.import;")]
    public async Task ANearMissIsNotRefused(string source)
    {
        var host = new Host { ["mem:/main"] = source, ["mem:/other"] = "export const o = 1;" };
        using var realm = ModuleRealm(out var map, host);

        var main = await host.Load(realm, map, "./main");
        main.Evaluate();
        host.RunUntilIdle(realm);
        Assert.Equal(JsModuleStatus.Evaluated, main.Status);
    }

    /// <summary>
    /// Each module's top-level var and function declarations are its own, not the global object's:
    /// on Broiler.JS 0.1.0-preview.1 these graphs shared one global binding and were refused.
    /// </summary>
    [Theory]
    [InlineData("var count = 1; export function get() { return count; }", "import { get } from './dep'; var count = 99; export const seen = get() + ':' + count;", "1:99")]
    [InlineData("function helper() { return 'dep'; } export const k = helper();", "import { k } from './dep'; function helper() { return 'main'; } export const seen = k + helper();", "depmain")]
    [InlineData("var secret = 1; export const k = 1;", "import { k } from './dep'; export const seen = typeof secret;", "undefined")]
    [InlineData("if (true) { var secret = 1; } export const k = 1;", "import { k } from './dep'; export const seen = typeof secret;", "undefined")]
    [InlineData("export const k = 1;", "import { k } from './dep'; function Map() {} export const seen = k + ':' + (Map === globalThis.Map);", "1:false")]
    public async Task ModulesKeepTheirOwnTopLevelVarAndFunctionBindings(string dep, string main, string expected)
    {
        var host = new Host { ["mem:/main"] = main, ["mem:/dep"] = dep };
        using var realm = ModuleRealm(out var map, host);

        var root = await host.Load(realm, map, "./main");
        root.Evaluate();
        host.RunUntilIdle(realm);
        Assert.Equal(JsModuleStatus.Evaluated, root.Status);
        Assert.Equal(expected, realm.GetProperty(root.GetNamespace(), "seen").AsString);

        var globals = realm.OwnPropertyNames(realm.Global);
        foreach (var name in new[] { "count", "helper", "secret" })
            Assert.DoesNotContain(name, globals);
        Assert.True(realm.EvaluateHostScript("typeof Map === 'function' && Map.name === 'Map'", "test:map-intact").AsBoolean);
    }

    [Fact]
    public async Task ATopLevelVarOfOneModuleIsInvisibleToModulesLinkedLater()
    {
        var host = new Host
        {
            ["mem:/first"] = "var shared = 1; export const k = shared;",
            ["mem:/second"] = "var shared = 2; export const k = shared;",
            ["mem:/reader"] = "export const seen = typeof shared;",
            ["mem:/own"] = "let shared = 3; export const seen = shared;",
        };
        using var realm = ModuleRealm(out var map, host);

        var answers = new List<string>();
        foreach (var (name, export) in new[] { ("first", "k"), ("second", "k"), ("reader", "seen"), ("own", "seen") })
        {
            var module = await host.Load(realm, map, "./" + name);
            module.Evaluate();
            host.RunUntilIdle(realm);
            Assert.Equal(JsModuleStatus.Evaluated, module.Status);
            answers.Add(realm.ToJsString(realm.GetProperty(module.GetNamespace(), export)));
        }

        Assert.Equal(new[] { "1", "2", "undefined", "3" }, answers);
        Assert.DoesNotContain("shared", realm.OwnPropertyNames(realm.Global));
    }

    [Fact]
    public async Task ModuleCodeIsStrictAndKeepsItsLines()
    {
        var host = new Host
        {
            ["mem:/main"] = "#!/usr/bin/env host\nexport const kinds = (() => { try { undeclared = 1; return 'sloppy'; } catch (e) { return e.constructor.name; } })() + ',' + (function () { return typeof this; })();",
            ["mem:/octal"] = "export const k = 010;",
            ["mem:/with"] = "export const k = 1;\nwith ({}) {}",
        };
        using var realm = ModuleRealm(out var map, host);

        var main = await host.Load(realm, map, "./main");
        main.Evaluate();
        host.RunUntilIdle(realm);
        Assert.Equal("ReferenceError,undefined", realm.GetProperty(main.GetNamespace(), "kinds").AsString);

        Assert.Equal(JsModulePhase.Parse, (await Assert.ThrowsAsync<JsModuleException>(() => host.Load(realm, map, "./octal"))).Phase);
        var with = await Assert.ThrowsAsync<JsModuleException>(() => host.Load(realm, map, "./with"));
        Assert.Equal(JsModulePhase.Parse, with.Phase);
        Assert.Equal(2, Assert.IsType<JsEngineException>(with.InnerException).SourceLine);
    }

    [Fact]
    public async Task AGraphThatKeepsGrowingStopsAtMaxModulesDuringTheLoad()
    {
        // Every module imports a new one, so the graph never finishes loading by itself.
        var host = new Host { OnLoad = key => $"import * as next from './{key.Value[5..]}x';" };
        using var realm = new BroilerJsEngineProvider { EnableModuleContract = true }.CreateRealm(JsRealmOptions.Default);
        var map = ((IJsModules)realm).OpenModuleMap(host, new JsModuleOptions { MaxModules = 5 });

        var failure = await Assert.ThrowsAsync<JsModuleException>(() => host.Load(realm, map, "./m"));
        Assert.Equal(JsModulePhase.Load, failure.Phase);
        Assert.Contains("MaxModules", failure.Message);
        Assert.Equal(5, host.Loads);
    }

    [Fact]
    public async Task DrainingJobsFromResolveIsRefusedWithoutLosingAJob()
    {
        var host = new Host { ["mem:/main"] = "export const ok = 1;" };
        using var realm = ModuleRealm(out var map, host);
        var ran = false;
        realm.EnqueueJob(() => ran = true);
        host.OnResolve = () => realm.DrainJobs();

        var failure = await Assert.ThrowsAsync<JsModuleException>(() => host.Load(realm, map, "./main"));
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.True(ran, "the job queued before the refused drain was lost");
    }

    [Fact]
    public async Task AHostThatReentersTheRealmFromResolveFailsThatRequestOnly()
    {
        var host = new Host { ["mem:/main"] = "export const ok = 1;" };
        using var realm = ModuleRealm(out var map, host);
        host.OnResolve = () => realm.EvaluateHostScript("1", "test:reentry");

        var failure = await Assert.ThrowsAsync<JsModuleException>(() => host.Load(realm, map, "./main"));
        Assert.Equal(JsModulePhase.Resolve, failure.Phase);
        Assert.IsType<InvalidOperationException>(failure.InnerException);

        // The realm and the map stay usable, and the next request asks the host again.
        host.OnResolve = null;
        Assert.Equal(JsModuleStatus.Linked, (await host.Load(realm, map, "./main")).Status);
        Assert.Equal(1d, realm.EvaluateHostScript("1", "test:after-reentry").AsNumber);
    }

    [Fact]
    public void ARegisteredProviderNeverBuildsAModuleRealm()
    {
        using var realm = new BroilerJsEngineProvider().CreateRealm(JsRealmOptions.Default);
        Assert.False(realm is IJsModules);
    }

    private static IJsRealm ModuleRealm(out IJsModuleMap map, Host host)
    {
        var realm = new BroilerJsEngineProvider { EnableModuleContract = true }.CreateRealm(JsRealmOptions.Default);
        map = ((IJsModules)realm).OpenModuleMap(host, new JsModuleOptions());
        return realm;
    }

    internal sealed class Host : Dictionary<string, string>, IJsModuleHost
    {
        private readonly ConcurrentQueue<Action> _tasks = new();

        public Action? OnResolve { get; set; }

        public JsModuleResolution Resolve(in JsModuleRequest request)
        {
            OnResolve?.Invoke();
            return JsModuleResolution.Resolved(new JsModuleKey("mem:/" + request.Specifier[2..]));
        }

        public Func<JsModuleKey, string>? OnLoad { get; init; }

        public int Loads { get; private set; }

        public ValueTask<JsModuleSource> LoadAsync(JsModuleKey key, CancellationToken cancellationToken)
        {
            Loads++;
            if (OnLoad is { } generate)
                return new(new JsModuleSource(generate(key)));

            return new(TryGetValue(key.Value, out var text) ? new JsModuleSource(text) : JsModuleSource.Failed(JsModuleFailure.NotFound, key.Value));
        }

        public void QueueRealmTask(Action task) => _tasks.Enqueue(task);

        public void RunUntilIdle(IJsRealm realm)
        {
            for (var turn = 0; turn < 1_000; turn++)
            {
                var ran = false;
                while (_tasks.TryDequeue(out var task))
                {
                    task();
                    ran = true;
                }

                if (realm.DrainJobs() == 0 && !ran)
                    return;
            }

            Assert.Fail("The realm did not become idle.");
        }

        public async Task<IJsModule> Load(IJsRealm realm, IJsModuleMap map, string specifier)
        {
            var loading = map.LoadAsync(specifier, JsModuleReferrer.Host(null));
            RunUntilIdle(realm);
            return await loading;
        }
    }
}
