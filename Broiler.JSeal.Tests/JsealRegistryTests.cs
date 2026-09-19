using Broiler.JSeal.BroilerJs;
using Broiler.JSeal;

namespace Broiler.JSeal.Tests;

/// <summary>
/// <see cref="JsEngineRegistry"/>: how a process finds out which JavaScript engines it has, and
/// which one it uses.
/// <para>
/// This is the half of JSEAL a conformance suite depends on before it can assert anything â€” the
/// theories in <see cref="JsealConformanceTests"/> are driven by <see cref="JsEngineRegistry.All"/>,
/// so a registry that lost a provider would report a green suite that ran no assertions at all. The
/// first test here is exactly that guard.
/// </para>
/// <para>
/// <b>These tests mutate process-wide state, and every one of them puts it back.</b> The registry is
/// static and xUnit runs test classes in parallel, so a page load in another class could be
/// enumerating <see cref="JsEngineRegistry.All"/> while these run. Two rules keep that safe and are
/// worth stating because breaking either is a flake rather than a failure: nothing here calls
/// <see cref="JsEngineRegistry.Reset"/>, which would unregister the real engine and leave a
/// concurrent <c>DomBridge</c> with no provider to adopt its context; and the substitute registered
/// below deliberately does not implement <see cref="IJsRealmAdoption"/>, so a concurrent adoption
/// that is offered it simply moves on to the next provider. Every write to the selection environment
/// variable also lives in this class, so the writes are serialised with each other by xUnit's
/// per-class collection.
/// </para>
/// </summary>
public class JsealRegistryTests
{
    /// <remarks>
    /// The same explicit registration the conformance suite performs, and for the same reason: this
    /// class names no engine type either, so nothing else would load the provider assembly whose
    /// module initializer registers it.
    /// </remarks>
    static JsealRegistryTests() => BroilerJsEngineProvider.Register();

    /// <summary>
    /// A provider that exists only to be looked up.
    /// </summary>
    /// <remarks>
    /// <see cref="CreateRealm"/> throws rather than returning something inert: nothing in these
    /// tests builds a realm from it, and a substitute that silently answered a realm request would
    /// be a way for a mistake to look like a pass. It implements no
    /// <see cref="IJsRealmAdoption"/>, which is what keeps it harmless to a page load running in
    /// another test class while it is registered.
    /// </remarks>
    private sealed class SubstituteProvider(string name, JsCapabilities capabilities = JsCapabilities.None)
        : IJsEngineProvider
    {
        public string Name => name;

        public string Description => "A test substitute; it builds no realms.";

        public JsCapabilities Capabilities => capabilities;

        public IJsRealm CreateRealm(JsRealmOptions options) =>
            throw new NotSupportedException("The substitute provider does not build realms.");
    }

    /// <summary>A name no real engine will ever take, so a leaked registration is obvious.</summary>
    private static string UniqueName([System.Runtime.CompilerServices.CallerMemberName] string caller = "") =>
        $"test-substitute-{caller.ToLowerInvariant()}";

    [Fact]
    public void ThisBuildHasAnEngineForTheConformanceSuiteToRun()
    {
        // Without this, JsealConformanceTests would enumerate nothing, every theory in it would be
        // skipped for want of data, and the suite would report green having asserted nothing.
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

            // The name is what a configuration or an environment variable spells, so it has to be a
            // stable identifier rather than a display string.
            Assert.Equal(provider.Name.Trim(), provider.Name);
            Assert.Equal(provider.Name.ToLowerInvariant(), provider.Name);
        }
    }

    [Fact]
    public void TheDefaultProviderIsOneOfTheRegisteredOnes()
    {
        var chosen = JsEngineRegistry.Default;

        Assert.Same(chosen, JsEngineRegistry.Find(chosen.Name));
        Assert.Contains(chosen, JsEngineRegistry.All);
    }

    [Fact]
    public void FindAnswersNullForANameNobodyRegistered()
    {
        Assert.Null(JsEngineRegistry.Find("no-such-engine"));
    }

    [Fact]
    public void RegisteringAProviderMakesItFindableAndUnregisteringRemovesIt()
    {
        var name = UniqueName();
        var provider = new SubstituteProvider(name, JsCapabilities.HostScriptSource);

        try
        {
            JsEngineRegistry.Register(provider);

            Assert.Same(provider, JsEngineRegistry.Find(name));
            Assert.Contains(provider, JsEngineRegistry.All);

            // Names are matched case-insensitively, because the thing that spells one is a human
            // editing a configuration file or an environment variable.
            Assert.Same(provider, JsEngineRegistry.Find(name.ToUpperInvariant()));
        }
        finally
        {
            Assert.True(JsEngineRegistry.Unregister(name));
        }

        Assert.Null(JsEngineRegistry.Find(name));
        Assert.DoesNotContain(provider, JsEngineRegistry.All);

        // Unregistering what is not there answers false rather than throwing: a teardown that runs
        // twice is not a failure.
        Assert.False(JsEngineRegistry.Unregister(name));
    }

    [Fact]
    public void RegisteringTheSameNameTwiceReplacesTheProvider()
    {
        var name = UniqueName();
        var first = new SubstituteProvider(name);
        var second = new SubstituteProvider(name, JsCapabilities.Promises);

        try
        {
            JsEngineRegistry.Register(first);
            JsEngineRegistry.Register(second);

            // Replacement is what a suite that substitutes a recording provider needs, and it is why
            // the registry is keyed by name rather than appended to.
            Assert.Same(second, JsEngineRegistry.Find(name));
            Assert.Equal(JsCapabilities.Promises, JsEngineRegistry.Find(name)!.Capabilities);
            Assert.Single(JsEngineRegistry.All.Where(p => p.Name == name));
        }
        finally
        {
            JsEngineRegistry.Unregister(name);
        }
    }

    [Fact]
    public void SetDefaultRefusesANameNobodyRegistered()
    {
        var before = JsEngineRegistry.Default;

        // A typo in a configuration should be loud rather than silently served by whichever engine
        // happened to register first â€” which is the failure mode the argument check exists to stop.
        var refusal = Assert.Throws<ArgumentException>(() => JsEngineRegistry.SetDefault("no-such-engine"));
        Assert.Equal("name", refusal.ParamName);

        Assert.Same(before, JsEngineRegistry.Default);
    }

    [Fact]
    public void SetDefaultChoosesAmongTheRegisteredProviders()
    {
        var name = UniqueName();
        var provider = new SubstituteProvider(name);
        var previousSelection = Environment.GetEnvironmentVariable(JsEngineRegistry.SelectionEnvironmentVariable);
        Environment.SetEnvironmentVariable(JsEngineRegistry.SelectionEnvironmentVariable, null);

        var previousDefault = JsEngineRegistry.Default.Name;

        try
        {
            JsEngineRegistry.Register(provider);
            JsEngineRegistry.SetDefault(name);

            Assert.Same(provider, JsEngineRegistry.Default);
        }
        finally
        {
            JsEngineRegistry.SetDefault(previousDefault);
            JsEngineRegistry.Unregister(name);
            Environment.SetEnvironmentVariable(JsEngineRegistry.SelectionEnvironmentVariable, previousSelection);
        }

        Assert.Equal(previousDefault, JsEngineRegistry.Default.Name);
    }

    [Fact]
    public void TheEnvironmentVariableOverridesTheDefaultAndAnUnknownOneDoesNot()
    {
        var name = UniqueName();
        var provider = new SubstituteProvider(name);
        var previousSelection = Environment.GetEnvironmentVariable(JsEngineRegistry.SelectionEnvironmentVariable);

        try
        {
            JsEngineRegistry.Register(provider);
            Environment.SetEnvironmentVariable(JsEngineRegistry.SelectionEnvironmentVariable, null);
            var registeredDefault = JsEngineRegistry.Default;

            // "Does this page render differently on the other engine?" is a question about a run,
            // which is why the selection is read per call rather than captured at registration.
            Environment.SetEnvironmentVariable(JsEngineRegistry.SelectionEnvironmentVariable, name);
            Assert.Same(provider, JsEngineRegistry.Default);

            // Surrounding whitespace is trimmed, because an environment variable set from a shell
            // script picks it up more often than not.
            Environment.SetEnvironmentVariable(JsEngineRegistry.SelectionEnvironmentVariable, $"  {name}  ");
            Assert.Same(provider, JsEngineRegistry.Default);

            // A name nobody registered falls back to the registered default rather than throwing:
            // the variable selects among the engines this build linked, and one it did not link is
            // not a reason to refuse to load a page.
            Environment.SetEnvironmentVariable(JsEngineRegistry.SelectionEnvironmentVariable, "no-such-engine");
            Assert.Same(registeredDefault, JsEngineRegistry.Default);

            Environment.SetEnvironmentVariable(JsEngineRegistry.SelectionEnvironmentVariable, "   ");
            Assert.Same(registeredDefault, JsEngineRegistry.Default);
        }
        finally
        {
            Environment.SetEnvironmentVariable(JsEngineRegistry.SelectionEnvironmentVariable, previousSelection);
            JsEngineRegistry.Unregister(name);
        }
    }

    [Fact]
    public void RegisteringNothingIsRefusedRatherThanRecorded()
    {
        Assert.Throws<ArgumentNullException>(() => JsEngineRegistry.Register(null!));
    }

    [Fact]
    public void AProviderThatCannotAdoptAForeignRealmSaysSo()
    {
        // The bridge offers the context the host built to every registered provider in turn, so the
        // type test in TryAdopt is what makes that safe: a provider must answer false for an object
        // that is not its engine's rather than wrap something it cannot drive.
        var adopters = JsEngineRegistry.All.OfType<IJsRealmAdoption>().ToArray();
        Assert.NotEmpty(adopters);

        foreach (var adopter in adopters)
        {
            // The options are immaterial to the type test and are passed as the default, which is
            // also the shape a caller uses when it has no policy to impose.
            Assert.False(adopter.TryAdopt(new object(), JsRealmOptions.Default, out var realm));
            Assert.Null(realm);

            Assert.False(adopter.TryAdopt("not a realm", JsRealmOptions.Default, out realm));
            Assert.Null(realm);
        }
    }
}

