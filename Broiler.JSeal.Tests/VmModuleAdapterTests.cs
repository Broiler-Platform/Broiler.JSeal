#if BROILER_VM_JS
using Broiler.JSeal.Vm;

using Host = Broiler.JSeal.Tests.BroilerJsModuleAdapterTests.Host;

namespace Broiler.JSeal.Tests;

/// <summary>
/// The Broiler.VM module adapter's own behavior beside the shared contract cases: the gate, the
/// map's bound, re-entry from the host, and a realm without the contract.
/// </summary>
/// <remarks>
/// Engine-specific, like <see cref="BroilerJsModuleAdapterTests"/>, whose in-memory host these share.
/// The contract itself is asserted by the provider-neutral cases in
/// <c>JsealConformanceTests.Modules.cs</c>, which run against this adapter through the same gate.
/// </remarks>
public class VmModuleAdapterTests
{
    [Fact]
    public void ARegisteredProviderNeverBuildsAModuleRealm()
    {
        using var realm = new VmEngineProvider().CreateRealm(JsRealmOptions.Default);
        Assert.False(realm is IJsModules);

        using var gated = new VmEngineProvider { EnableModuleContract = true }.CreateRealm(JsRealmOptions.Default);
        Assert.True(gated is IJsModules);
        Assert.False(gated.Capabilities.HasFlag(JsCapabilities.Modules));
        Assert.False(gated.Capabilities.HasFlag(JsCapabilities.DynamicImport));
    }

    [Fact]
    public void AGuestImportInARealmWithoutTheContractRejectsWithATypeError()
    {
        using var realm = new VmEngineProvider().CreateRealm(JsRealmOptions.Default);
        realm.EvaluateHostScript(
            "globalThis.outcome = 'pending'; import('./anything').then(() => { outcome = 'loaded'; }, e => { outcome = e.name; });",
            "test:vm-import-without-map");
        realm.DrainJobs();

        Assert.Equal("TypeError", realm.GetProperty(realm.Global, "outcome").AsString);
    }

    [Fact]
    public async Task AGraphThatKeepsGrowingStopsAtMaxModulesDuringTheLoad()
    {
        // Every module imports a new one, so the graph never finishes loading by itself.
        var host = new Host { OnLoad = key => $"import * as next from './{key.Value[5..]}x';" };
        using var realm = new VmEngineProvider { EnableModuleContract = true }.CreateRealm(JsRealmOptions.Default);
        var map = ((IJsModules)realm).OpenModuleMap(host, new JsModuleOptions { MaxModules = 5 });

        var failure = await Assert.ThrowsAsync<JsModuleException>(() => host.Load(realm, map, "./m"));
        Assert.Equal(JsModulePhase.Load, failure.Phase);
        Assert.Contains("maximum of 5 modules", failure.Message);
        Assert.Equal(5, host.Loads);
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
        var main = await host.Load(realm, map, "./main");
        Assert.Equal(JsModuleStatus.Linked, main.Status);
        Assert.Equal(1d, realm.EvaluateHostScript("1", "test:after-reentry").AsNumber);
    }

    [Fact]
    public async Task ARealmOpensAtMostOneMapAndARealmWithoutTheGateHasNone()
    {
        var host = new Host();
        using var realm = ModuleRealm(out _, host);
        Assert.Throws<InvalidOperationException>(() => ((IJsModules)realm).OpenModuleMap(host, new JsModuleOptions()));

        // A module and its namespace come from the realm's own module graph, linked on load.
        host["mem:/dep"] = "export const x = 41;";
        using var second = ModuleRealm(out var map, host);
        var dep = await host.Load(second, map, "./dep");
        Assert.True(dep.GetNamespace().IsObject);
        dep.Evaluate();
        host.RunUntilIdle(second);
        Assert.Equal(JsModuleStatus.Evaluated, dep.Status);
        Assert.Equal(41d, second.GetProperty(dep.GetNamespace(), "x").AsNumber);
    }

    private static IJsRealm ModuleRealm(out IJsModuleMap map, Host host)
    {
        var realm = new VmEngineProvider { EnableModuleContract = true }.CreateRealm(JsRealmOptions.Default);
        map = ((IJsModules)realm).OpenModuleMap(host, new JsModuleOptions());
        return realm;
    }
}
#endif
