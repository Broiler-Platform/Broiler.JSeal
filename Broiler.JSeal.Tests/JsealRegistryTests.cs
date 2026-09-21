using Broiler.JSeal.BroilerJs;

namespace Broiler.JSeal.Tests;

/// <summary>Read-only checks of the process registry, and isolated tests of its shared implementation.</summary>
public class JsealRegistryTests
{
    static JsealRegistryTests() => BroilerJsEngineProvider.Register();

    private sealed class SubstituteProvider(string name) : IJsEngineProvider
    {
        public string Name => name;
        public string Description => "Registry test provider";
        public JsCapabilities Capabilities => JsCapabilities.None;
        public IJsRealm CreateRealm(JsRealmOptions options) => throw new NotSupportedException();
    }

    [Fact]
    public void ThisBuildHasAnEngineForTheConformanceSuiteToRun()
    {
        Assert.True(JsEngineRegistry.HasAny);
        Assert.NotEmpty(JsEngineRegistry.All);
        Assert.NotEmpty(JsealConformanceTests.Engines);
    }

    [Fact]
    public void EveryRegisteredProviderIsFindableUnderItsOwnName()
    {
        foreach (var provider in JsEngineRegistry.All)
        {
            Assert.Same(provider, JsEngineRegistry.Find(provider.Name));
            Assert.Same(provider, JsEngineRegistry.Find(provider.Name.ToUpperInvariant()));
            Assert.Equal(provider.Name.Trim(), provider.Name);
            Assert.Equal(provider.Name.ToLowerInvariant(), provider.Name);
        }
        Assert.Null(JsEngineRegistry.Find("no-such-engine"));
    }

    [Fact]
    public void TheDefaultProviderIsOneOfTheRegisteredOnes()
    {
        var chosen = JsEngineRegistry.Default;
        Assert.Same(chosen, JsEngineRegistry.Find(chosen.Name));
        Assert.Contains(chosen, JsEngineRegistry.All);
    }

    [Fact]
    public void RegisteringAProviderMakesItFindableAndUnregisteringRemovesIt()
    {
        var registry = new JsEngineRegistryState();
        var provider = new SubstituteProvider("alpha");
        registry.Register(provider);
        Assert.True(registry.HasAny);
        Assert.Same(provider, registry.Find("ALPHA"));
        Assert.Same(provider, registry.GetDefault());
        Assert.Same(provider, Assert.Single(registry.All));
        Assert.True(registry.Unregister("ALPHA"));
        Assert.Null(registry.Find("alpha"));
        Assert.Empty(registry.All);
        Assert.False(registry.HasAny);
        Assert.False(registry.Unregister("alpha"));
    }

    [Fact]
    public void RegisteringTheSameNameTwiceReplacesTheSelectedProvider()
    {
        var registry = new JsEngineRegistryState();
        var first = new SubstituteProvider("beta");
        var other = new SubstituteProvider("alpha");
        var replacement = new SubstituteProvider("BETA");
        registry.Register(first);
        registry.Register(other);
        registry.SetDefault("BeTa");
        registry.Register(replacement);
        Assert.Equal(2, registry.All.Count);
        Assert.Same(replacement, registry.Find("beta"));
        Assert.Same(replacement, registry.GetDefault());
        Assert.Same(replacement, registry.GetDefault(" beta "));
        Assert.True(registry.Unregister("beta"));
        Assert.Same(other, registry.GetDefault());
    }

    [Fact]
    public void RemovingTheDefaultChoosesTheFirstRemainingNameAndEventuallyBecomesEmpty()
    {
        var registry = new JsEngineRegistryState();
        var zulu = new SubstituteProvider("zulu");
        var beta = new SubstituteProvider("Beta");
        var alpha = new SubstituteProvider("alpha");
        registry.Register(zulu);
        registry.Register(beta);
        registry.Register(alpha);
        Assert.Same(zulu, registry.GetDefault()); // First registration, not alphabetical selection.
        registry.SetDefault("ZULU");
        Assert.True(registry.Unregister("zulu"));
        Assert.Same(alpha, registry.GetDefault());
        Assert.True(registry.Unregister("alpha"));
        Assert.Same(beta, registry.GetDefault());
        Assert.False(registry.Unregister("missing"));
        Assert.Same(beta, registry.GetDefault());
        Assert.True(registry.Unregister("BETA"));
        Assert.False(registry.HasAny);
        Assert.Empty(registry.All);
        var failure = Assert.Throws<InvalidOperationException>(() => registry.GetDefault());
        Assert.StartsWith("No JavaScript engine provider is registered.", failure.Message);
        Assert.Throws<InvalidOperationException>(() => registry.GetDefault("zulu"));
        registry.Register(alpha);
        Assert.Same(alpha, registry.GetDefault());
    }

    [Fact]
    public void RemovingAnotherProviderPreservesTheExplicitDefault()
    {
        var registry = new JsEngineRegistryState();
        var first = new SubstituteProvider("alpha");
        var selected = new SubstituteProvider("zulu");
        registry.Register(first);
        registry.Register(selected);
        registry.SetDefault("zulu");
        Assert.True(registry.Unregister("alpha"));
        Assert.Same(selected, registry.GetDefault());
    }

    [Fact]
    public void SetDefaultRefusesAnUnknownNameWithoutChangingSelection()
    {
        var registry = new JsEngineRegistryState();
        var provider = new SubstituteProvider("alpha");
        registry.Register(provider);
        var failure = Assert.Throws<ArgumentException>(() => registry.SetDefault("missing"));
        Assert.Equal("name", failure.ParamName);
        Assert.Same(provider, registry.GetDefault());
    }

    [Fact]
    public void EnvironmentSelectionIsReadPerCallAndDoesNotChangeTheRegisteredDefault()
    {
        var registry = new JsEngineRegistryState();
        var first = new SubstituteProvider("alpha");
        var selected = new SubstituteProvider("beta");
        registry.Register(first);
        registry.Register(selected);
        // Supply the per-call environment value without mutating this test process's environment.
        Assert.Same(selected, registry.GetDefault("  BeTa  "));
        foreach (var selection in new[] { null, "", "   ", "missing" })
            Assert.Same(first, registry.GetDefault(selection));
        Assert.True(registry.Unregister("beta"));
        Assert.Same(first, registry.GetDefault("beta"));
        registry.Register(selected);
        Assert.Same(selected, registry.GetDefault("beta"));
        Assert.Same(first, registry.GetDefault());
    }

    [Fact]
    public void ResetClearsRegistrationsAndSelectionAndSnapshotsRemainStable()
    {
        var registry = new JsEngineRegistryState();
        var first = new SubstituteProvider("alpha");
        var second = new SubstituteProvider("beta");
        registry.Register(first);
        registry.Register(second);
        registry.SetDefault("beta");
        var snapshot = registry.All;
        registry.Reset();
        registry.Reset();
        Assert.False(registry.HasAny);
        Assert.Empty(registry.All);
        Assert.Null(registry.Find("beta"));
        Assert.Throws<InvalidOperationException>(() => registry.GetDefault("beta"));
        Assert.Equal(2, snapshot.Count);
        Assert.Contains(first, snapshot);
        Assert.Contains(second, snapshot);
        registry.Register(first);
        Assert.Same(first, registry.GetDefault("beta"));
    }

    [Fact]
    public async Task DefaultReadsStayValidWhileTheSelectedProviderIsRemovedAndReplaced()
    {
        var registry = new JsEngineRegistryState();
        var permanent = new SubstituteProvider("beta");
        var transient = new SubstituteProvider("alpha");
        var replacement = new SubstituteProvider("ALPHA");
        registry.Register(permanent);
        using var start = new Barrier(2);
        var writer = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
            for (var i = 0; i < 2_000; i++)
            {
                registry.Register(transient);
                registry.SetDefault("alpha");
                registry.Register(replacement);
                Assert.True(registry.Unregister("alpha"));
                Assert.Same(permanent, registry.GetDefault());
            }
        });
        var reader = Task.Run(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
            for (var i = 0; i < 4_000; i++)
            {
                var selected = registry.GetDefault(i % 2 == 0 ? "alpha" : null);
                Assert.True(ReferenceEquals(selected, permanent) || ReferenceEquals(selected, transient) || ReferenceEquals(selected, replacement));
                Assert.True(registry.HasAny);
                Assert.Contains(permanent, registry.All);
                Assert.Same(permanent, registry.Find("beta"));
            }
        });
        await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Same(permanent, registry.GetDefault());
    }

    [Fact]
    public void RegisteringNothingIsRefusedRatherThanRecorded()
    {
        var registry = new JsEngineRegistryState();
        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
        Assert.False(registry.HasAny);
    }

    [Fact]
    public void AProviderThatCannotAdoptAForeignRealmSaysSo()
    {
        var adopters = JsEngineRegistry.All.OfType<IJsRealmAdoption>().ToArray();
        Assert.NotEmpty(adopters);
        foreach (var adopter in adopters)
        {
            Assert.False(adopter.TryAdopt(new object(), JsRealmOptions.Default, out var realm));
            Assert.Null(realm);
            Assert.False(adopter.TryAdopt("not a realm", JsRealmOptions.Default, out realm));
            Assert.Null(realm);
        }
    }
}
