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
    /// A stable identifier, lower-case and hyphenated — <c>broiler-js</c>, <c>broiler-vm</c>. This is
    /// what a configuration or an environment variable names, so it does not change with a release.
    /// </summary>
    string Name { get; }

    /// <summary>A one-line description for a diagnostic or an about page.</summary>
    string Description { get; }

    /// <summary>
    /// What realms from this provider will be able to do, before one is built.
    /// </summary>
    /// <remarks>
    /// A realm's own <see cref="IJsRealm.Capabilities"/> may be <em>narrower</em> — a page whose
    /// Content-Security-Policy forbids evaluation gets a realm without
    /// <see cref="JsCapabilities.GuestEval"/> from an engine that has it — but never wider. That is
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
    /// Whether the page's <c>eval</c> and <c>new Function</c> may compile — <c>'unsafe-eval'</c>.
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
    /// Optional document URL, used only as the fallback source identity for an evaluation whose
    /// label is blank. See <see cref="SourceLabelFor"/>.
    /// </summary>
    /// <remarks>
    /// Diagnostic metadata only. Neither bundled provider uses it for module or import resolution,
    /// caching, origin checks or evaluation permission, and it is not validated as a URL.
    /// </remarks>
    public string? DocumentUrl { get; init; }

    /// <summary>The source identity used when neither a label nor a document URL is supplied.</summary>
    public const string AnonymousSourceLabel = "anonymous";

    /// <summary>
    /// Selects the source identity for one evaluation: a non-blank <paramref name="label"/> first,
    /// then a non-blank <see cref="DocumentUrl"/>, then <see cref="AnonymousSourceLabel"/>.
    /// </summary>
    /// <remarks>
    /// The selected value is returned unchanged and is diagnostic only. Providers report it through
    /// <see cref="JsEngineException.SourceLabel"/>; it never selects an evaluation member, strictness
    /// or permission, so a label cannot authorize source that its member would refuse.
    /// </remarks>
    public string SourceLabelFor(string? label) =>
        !string.IsNullOrWhiteSpace(label) ? label
        : !string.IsNullOrWhiteSpace(DocumentUrl) ? DocumentUrl
        : AnonymousSourceLabel;

    /// <summary>The default: an unrestricted realm with no document.</summary>
    public static JsRealmOptions Default { get; } = new();
}

