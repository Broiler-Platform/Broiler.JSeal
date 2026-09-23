using System.Collections.Concurrent;

using Broiler.JSeal.BroilerJs;

namespace Broiler.JSeal.Tests;

/// <summary>
/// The Broiler.JS module adapter's own refusals: the graphs Broiler.JS 0.1.0-preview.1 would run
/// wrongly, refused at link time with the reason, and the near misses it must still run.
/// </summary>
/// <remarks>
/// Engine-specific, so these live beside the other Broiler.JS-only tests rather than in the shared
/// contract cases (<c>JsealConformanceTests.Modules.cs</c>), which assert the contract itself.
/// </remarks>
public class BroilerJsModuleAdapterTests
{
    [Theory]
    [InlineData("export let x = 1; x = 2;", "'x' is reassigned")]
    [InlineData("export var x = 1; function set() { [x] = [2]; }", "'x' is reassigned")]
    [InlineData("export function f() {} f = null;", "'f' is reassigned")]
    [InlineData("let y = 1; export { y as z }; for (y of [2]);", "'y' is reassigned")]
    [InlineData("export let x = 1; eval('');", "a direct eval could reassign")]
    [InlineData("export { x }; let x = 1;", "precedes the declaration of 'x'")]
    [InlineData("globalThis.first = 1; import * as other from './other';", "follows other statements")]
    [InlineData("export const load = () => import('./other');", "import() is not routed")]
    // Nodes the engine's own AstReduce walker skips: object literal members, switch cases, parameter defaults.
    [InlineData("const o = { m() { return import('./other'); } };", "import() is not routed")]
    [InlineData("switch (1) { case 1: import('./other'); }", "import() is not routed")]
    [InlineData("function f(p = import('./other')) {}", "import() is not routed")]
    [InlineData("export let x = 1; const o = { m() { x = 2; } };", "'x' is reassigned")]
    // A var hoisted out of a block, loop or try is still a module-level binding.
    [InlineData("if (true) { var x = 1; } export { x }; export function bump() { x = 2; }", "'x' is reassigned")]
    [InlineData("try { var x = 1; } finally {} export { x }; export function bump() { x = 5; }", "'x' is reassigned")]
    [InlineData("export { x }; if (true) { var x = 1; }", "precedes the declaration of 'x'")]
    [InlineData("export { i }; for (var i = 0; i < 3; i++);", "precedes the declaration of 'i'")]
    // The CommonJS parameters the engine passes to module code, which ECMAScript module code does not have.
    [InlineData("export const k = typeof module;", "'module'")]
    [InlineData("export const x = 1; exports.injected = 2;", "'exports'")]
    [InlineData("export const x = 1; const o = { module };", "'module'")]
    [InlineData("export const k = typeof require;", "'require'")]
    [InlineData("let caught; try { require('./other'); } catch (e) { caught = e; } export const seen = caught;", "'require'")]
    [InlineData("export const k = typeof __dirname;", "'__dirname'")]
    [InlineData("export const k = typeof __fileame;", "'__fileame'")]
    [InlineData("export const f = () => module;", "'module'")]
    [InlineData("export const k = typeof arguments;", "'arguments'")]
    [InlineData("export const k = typeof this;", "'this'")]
    [InlineData("export const f = () => this;", "'this'")]
    [InlineData("export const k = 1; eval('module.exports = {}');", "direct eval")]
    public async Task AGraphTheEngineWouldRunWronglyIsRefusedAtLinkWithTheReason(string source, string reason)
    {
        var host = new Host { ["mem:/main"] = source, ["mem:/other"] = "export const o = 1;" };
        using var realm = ModuleRealm(out var map, host);

        var failure = await Assert.ThrowsAsync<JsModuleException>(() => host.Load(realm, map, "./main"));
        Assert.Equal(JsModulePhase.Link, failure.Phase);
        Assert.Equal("mem:/main", failure.Key?.Value);
        Assert.Contains(reason, failure.Message);
        Assert.False(map.TryGetModule(new JsModuleKey("mem:/main"), out _));
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
    [InlineData("export const meta = typeof import.meta;")]
    public async Task ANearMissIsNotRefused(string source)
    {
        var host = new Host { ["mem:/main"] = source, ["mem:/other"] = "export const o = 1;" };
        using var realm = ModuleRealm(out var map, host);

        var main = await host.Load(realm, map, "./main");
        main.Evaluate();
        host.RunUntilIdle(realm);
        Assert.Equal(JsModuleStatus.Evaluated, main.Status);
    }

    [Theory]
    // Broiler.JS 0.1.0-preview.1 binds a module's top-level var and function declarations on the
    // realm's global object, so modules that share such a name would share one binding.
    [InlineData("var count = 1; export function get() { return count; }", "import { get } from './dep'; var count = 99; export const seen = get() + ':' + count;", "'count'")]
    [InlineData("function helper() { return 'dep'; } export const k = helper();", "import { k } from './dep'; function helper() { return 'main'; } export const seen = k + helper();", "'helper'")]
    [InlineData("var secret = 1; export const k = 1;", "import { k } from './dep'; export const seen = typeof secret;", "'secret'")]
    [InlineData("if (true) { var secret = 1; } export const k = 1;", "import { k } from './dep'; export const seen = typeof secret;", "'secret'")]
    [InlineData("export const k = 1;", "import { k } from './dep'; function Map() {} export const seen = k;", "'Map'")]
    public async Task ATopLevelVarThatTwoModulesOrTheGlobalObjectWouldShareIsRefused(string dep, string main, string reason)
    {
        var host = new Host { ["mem:/main"] = main, ["mem:/dep"] = dep };
        using var realm = ModuleRealm(out var map, host);

        var failure = await Assert.ThrowsAsync<JsModuleException>(() => host.Load(realm, map, "./main"));
        Assert.Equal(JsModulePhase.Link, failure.Phase);
        Assert.Contains(reason, failure.Message);
        Assert.Contains("global object", failure.Message);
    }

    [Fact]
    public async Task ATopLevelVarIsCheckedAgainstModulesLinkedEarlier()
    {
        var host = new Host
        {
            ["mem:/first"] = "var shared = 1; export const k = shared;",
            ["mem:/second"] = "var shared = 2; export const k = shared;",
            ["mem:/reader"] = "export const seen = typeof shared;",
            ["mem:/own"] = "let shared = 3; export const seen = shared;",
        };
        using var realm = ModuleRealm(out var map, host);

        var first = await host.Load(realm, map, "./first");
        first.Evaluate();
        host.RunUntilIdle(realm);
        Assert.Equal(JsModuleStatus.Evaluated, first.Status);

        Assert.Contains("'shared'", (await Assert.ThrowsAsync<JsModuleException>(() => host.Load(realm, map, "./second"))).Message);
        Assert.Contains("'shared'", (await Assert.ThrowsAsync<JsModuleException>(() => host.Load(realm, map, "./reader"))).Message);

        // A module's own top-level lexical declaration of the name is its own binding, not the global one.
        var own = await host.Load(realm, map, "./own");
        own.Evaluate();
        host.RunUntilIdle(realm);
        Assert.Equal(3d, realm.GetProperty(own.GetNamespace(), "seen").AsNumber);
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

    private sealed class Host : Dictionary<string, string>, IJsModuleHost
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
