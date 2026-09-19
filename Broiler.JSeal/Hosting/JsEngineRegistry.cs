using System.Collections.Concurrent;

namespace Broiler.JSeal;

/// <summary>
/// The engines this process can use, and which one it uses by default.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a registry rather than a <c>#if</c>.</b> Today one <c>#if BROILER_VM_JS</c> in
/// <c>BrowserApp.NewScriptEngine()</c> decides the engine, which means the decision is a property of
/// the binary: a build serves one engine and cannot be asked about another. Two things that matters
/// for are already wanted. A conformance suite that runs the same assertions against every registered
/// provider needs two in one process. And a bisect â€” "does this page render differently on the other
/// engine?" â€” is a question about a run, not about a build.
/// </para>
/// <para>
/// <b>What stays a build-time decision, and should.</b> Whether an engine's assemblies are <em>linked
/// at all</em> is still a <c>ProjectReference</c> under a configuration condition, because that is
/// what keeps <c>Debug</c> free of Broiler.VM and keeps the component-graph check meaningful. The
/// registry decides among the providers that are present; the build decides which are present. A
/// provider registers itself from the assembly that carries it, so an engine that was not linked
/// simply never appears here.
/// </para>
/// <para>
/// Registration is process-wide and thread-safe. Re-registering a name replaces the provider, which is
/// what a test that substitutes a recording provider needs; nothing else should.
/// </para>
/// </remarks>
public static class JsEngineRegistry
{
    private static readonly ConcurrentDictionary<string, IJsEngineProvider> Providers =
        new(StringComparer.OrdinalIgnoreCase);

    private static string? _defaultName;

    /// <summary>
    /// The environment variable a run may set to choose an engine by <see cref="IJsEngineProvider.Name"/>,
    /// overriding the registered default.
    /// </summary>
    public const string SelectionEnvironmentVariable = "BROILER_JS_ENGINE";

    /// <summary>
    /// Adds or replaces a provider. The first provider registered also becomes the default, so a
    /// process that links exactly one engine needs no further configuration.
    /// </summary>
    public static void Register(IJsEngineProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        Providers[provider.Name] = provider;
        Interlocked.CompareExchange(ref _defaultName, provider.Name, null);
    }

    /// <summary>Removes a provider, answering whether one was there.</summary>
    public static bool Unregister(string name) => Providers.TryRemove(name, out _);

    /// <summary>Every registered provider, in no particular order.</summary>
    public static IReadOnlyCollection<IJsEngineProvider> All => Providers.Values.ToArray();

    /// <summary>The provider registered under <paramref name="name"/>, or <see langword="null"/>.</summary>
    public static IJsEngineProvider? Find(string name) =>
        Providers.TryGetValue(name, out var provider) ? provider : null;

    /// <summary>
    /// Names the provider this process should use by default. Throws when nothing is registered under
    /// that name, because a typo in a configuration should be loud rather than silently served by
    /// whichever engine happened to register first.
    /// </summary>
    public static void SetDefault(string name)
    {
        if (!Providers.ContainsKey(name))
            throw new ArgumentException($"No JavaScript engine named '{name}' is registered.", nameof(name));

        _defaultName = name;
    }

    /// <summary>
    /// The provider a page load should use: the one named by
    /// <see cref="SelectionEnvironmentVariable"/> when it names a registered one, otherwise the
    /// registered default.
    /// </summary>
    /// <exception cref="InvalidOperationException">No provider is registered at all.</exception>
    public static IJsEngineProvider Default
    {
        get
        {
            var requested = Environment.GetEnvironmentVariable(SelectionEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(requested) && Providers.TryGetValue(requested.Trim(), out var chosen))
                return chosen;

            if (_defaultName is { } name && Providers.TryGetValue(name, out var provider))
                return provider;

            throw new InvalidOperationException(
                "No JavaScript engine provider is registered. A host must reference an engine provider " +
                "assembly and call its registration entry point before loading a page.");
        }
    }

    /// <summary>Whether any provider is registered.</summary>
    public static bool HasAny => !Providers.IsEmpty;

    /// <summary>
    /// Drops every registration. For test isolation only â€” a suite that registers a substitute
    /// provider has to be able to put the process back.
    /// </summary>
    public static void Reset()
    {
        Providers.Clear();
        _defaultName = null;
    }
}

