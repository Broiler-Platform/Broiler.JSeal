namespace Broiler.JSeal;

/// <summary>
/// One JavaScript engine, as something the browser can choose.
/// </summary>
/// <remarks>
/// A provider is the whole of what "adding an engine" means: an implementation of this interface, an
/// <see cref="IJsRealm"/> over the engine's own realm, and a registration. Nothing in the bridge, the
/// rendering pipeline or the browser shell changes.
/// </remarks>
public interface IJsEngineProvider
{
    /// <summary>
    /// A stable identifier, lower-case and hyphenated â€” <c>broiler-js</c>, <c>broiler-vm</c>. This is
    /// what a configuration or an environment variable names, so it does not change with a release.
    /// </summary>
    string Name { get; }

    /// <summary>A one-line description for a diagnostic or an about page.</summary>
    string Description { get; }

    /// <summary>
    /// What realms from this provider will be able to do, before one is built.
    /// </summary>
    /// <remarks>
    /// A realm's own <see cref="IJsRealm.Capabilities"/> may be <em>narrower</em> â€” a page whose
    /// Content-Security-Policy forbids evaluation gets a realm without
    /// <see cref="JsCapabilities.GuestEval"/> from an engine that has it â€” but never wider. That is
    /// what lets a host decide whether an engine can serve a page at all without paying to build a
    /// realm and find out.
    /// </remarks>
    JsCapabilities Capabilities { get; }

    /// <summary>Builds a realm.</summary>
    IJsRealm CreateRealm(JsRealmOptions options);
}

/// <summary>
/// What a host asks of a realm at the moment it is built â€” options a realm cannot be given
/// afterwards.
/// </summary>
/// <remarks>
/// <see cref="AllowGuestEval"/> is the one a host derives from the page's policy. Both providers
/// fix it when they build the realm, and a page's <c>eval</c> in a realm built without it meets a
/// refusal the page may catch. Broiler.VM's embedding contract expresses that policy by registering
/// no artifact-provider capability, and <c>VmScriptEngine</c> does; <c>VmEngineProvider</c>
/// cannot, because this repository's script and the page's classic scripts compile through the
/// same artifact provider, so it registers one for every realm and that provider refuses the
/// page's <c>eval</c> and <c>new Function</c> instead, though not while a host script is running.
/// </remarks>
public sealed class JsRealmOptions
{
    /// <summary>
    /// Whether the page's <c>eval</c> and <c>new Function</c> may compile â€” <c>'unsafe-eval'</c>.
    /// Default <see langword="true"/>; a host whose page's policy withholds <c>'unsafe-eval'</c>
    /// passes <see langword="false"/>, and the realm is built without
    /// <see cref="JsCapabilities.GuestEval"/>. The page's script elements and this repository's own
    /// script still run in it.
    /// </summary>
    public bool AllowGuestEval { get; init; } = true;

    /// <summary>
    /// Whether the source this repository hands over through
    /// <see cref="IJsSource.EvaluateHostScript"/> is run in strict mode regardless of what it says.
    /// Neither provider forces a classic script. Broiler.VM does force a page's <c>eval</c> or
    /// <c>new Function</c> compiled while that source is still running; Broiler.JS never does.
    /// </summary>
    public bool ForceStrictMode { get; init; }

    /// <summary>
    /// The document's URL, when there is one. A provider that resolves module specifiers or caches
    /// compiled programs needs it as part of the identity of what it compiled.
    /// </summary>
    public string? DocumentUrl { get; init; }

    /// <summary>The default: an unrestricted realm with no document.</summary>
    public static JsRealmOptions Default { get; } = new();
}

