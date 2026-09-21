namespace Broiler.JSeal;

/// <summary>The registered engines and default selection for this process.</summary>
/// <remarks>
/// Hosts choose which provider packages to reference and register; the registry selects among
/// providers already loaded. Registration and selection are synchronized, and enumeration returns
/// a snapshot. This does not make realm execution concurrent. Register providers explicitly when
/// selection must be available before their assemblies would otherwise load.
/// </remarks>
public static class JsEngineRegistry
{
    private static readonly JsEngineRegistryState State = new();

    /// <summary>The environment variable that selects a registered engine for a run.</summary>
    public const string SelectionEnvironmentVariable = "BROILER_JS_ENGINE";

    /// <summary>
    /// Adds or replaces a provider. The first registration becomes the default. Names are matched
    /// case-insensitively; replacing a name preserves its selection as default.
    /// </summary>
    public static void Register(IJsEngineProvider provider) => State.Register(provider);

    /// <summary>
    /// Removes a provider, answering whether one was there. Removing the selected default chooses
    /// the remaining name first in ordinal, case-insensitive order, or clears the default if empty.
    /// </summary>
    public static bool Unregister(string name) => State.Unregister(name);

    /// <summary>A snapshot of registered providers, in no particular order.</summary>
    public static IReadOnlyCollection<IJsEngineProvider> All => State.All;

    /// <summary>The provider registered under the name, or null.</summary>
    public static IJsEngineProvider? Find(string name) => State.Find(name);

    /// <summary>Selects a registered default; an unknown name raises ArgumentException.</summary>
    public static void SetDefault(string name) => State.SetDefault(name);

    /// <summary>
    /// The provider selected by BROILER_JS_ENGINE, read on each call, or the registered default.
    /// Selection trims whitespace and ignores case. Unknown or blank environment names use the
    /// registered default. A returned provider may subsequently be unregistered by another thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">No provider is registered.</exception>
    public static IJsEngineProvider Default =>
        State.GetDefault(Environment.GetEnvironmentVariable(SelectionEnvironmentVariable));

    /// <summary>Whether any provider is registered.</summary>
    public static bool HasAny => State.HasAny;

    /// <summary>Drops all registrations and default selection. Intended for isolated host/test setup.</summary>
    public static void Reset() => State.Reset();
}
