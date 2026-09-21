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

/// <summary>Options fixed when a realm is created or adopted.</summary>
/// <remarks>
/// AllowGuestEval narrows dynamic evaluation without disabling host or classic script APIs.
/// Each provider also enforces the restriction on guest eval and Function compilation. Host and
/// classic evaluations do not grant guest callbacks permission to compile additional source.
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
    /// Whether EvaluateHostScript forces strict mode. Classic scripts retain their supplied strictness;
    /// guest indirect eval and Function compilation use their own source's strictness on both providers.
    /// </summary>
    public bool ForceStrictMode { get; init; }

    /// <summary>
    /// Optional document URL metadata. Neither bundled provider currently uses this value for source
    /// labels, module resolution or caching; its intended behavior is tracked by roadmap slice J17.
    /// </summary>
    public string? DocumentUrl { get; init; }

    /// <summary>The default: an unrestricted realm with no document.</summary>
    public static JsRealmOptions Default { get; } = new();
}

