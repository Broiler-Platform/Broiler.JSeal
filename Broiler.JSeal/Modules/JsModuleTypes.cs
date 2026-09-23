namespace Broiler.JSeal;

/// <summary>A canonical module key: non-empty, without U+0000, compared by ordinal equality.</summary>
/// <remarks>Only the host's <see cref="IJsModuleHost.Resolve"/> produces keys; providers never derive or normalize them.</remarks>
public readonly record struct JsModuleKey
{
    /// <summary>Validates and wraps a key.</summary>
    /// <exception cref="ArgumentException">The value is empty or contains U+0000.</exception>
    public JsModuleKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value.Contains('\0'))
            throw new ArgumentException("A module key is non-empty and contains no U+0000.", nameof(value));

        Value = value;
    }

    /// <summary>The key text. Null only for <c>default(JsModuleKey)</c>, which no operation accepts.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>What asked for a module.</summary>
public enum JsModuleRequestKind
{
    /// <summary>A static <c>import</c> or <c>export ... from</c> declaration in a module.</summary>
    Static,

    /// <summary>A guest <c>import()</c> call.</summary>
    Dynamic,

    /// <summary>A host call to <see cref="IJsModuleMap.LoadAsync"/>.</summary>
    HostRoot,
}

/// <summary>Which kind of code a request came from.</summary>
public enum JsModuleReferrerKind
{
    /// <summary>A module, identified by its key.</summary>
    Module,

    /// <summary>A classic or host script, identified by its label.</summary>
    Script,

    /// <summary>The host itself, optionally with a base key.</summary>
    Host,
}

/// <summary>The referrer of a module request: <see cref="Module"/>, <see cref="Script"/> or <see cref="Host"/>.</summary>
public readonly record struct JsModuleReferrer
{
    private JsModuleReferrer(JsModuleReferrerKind kind, JsModuleKey? key, string? label)
    {
        Kind = kind;
        Key = key;
        Label = label;
    }

    /// <summary>Which kind of referrer this is.</summary>
    public JsModuleReferrerKind Kind { get; }

    /// <summary>The referring module's key, or the host's base key; otherwise null.</summary>
    public JsModuleKey? Key { get; }

    /// <summary>The referring script's label; otherwise null.</summary>
    public string? Label { get; }

    /// <summary>A request from the module with <paramref name="key"/>.</summary>
    public static JsModuleReferrer Module(JsModuleKey key) =>
        key.Value is null ? throw new ArgumentException("A module referrer needs a key.", nameof(key)) : new(JsModuleReferrerKind.Module, key, null);

    /// <summary>A request from the classic or host script labelled <paramref name="label"/>.</summary>
    public static JsModuleReferrer Script(string label) =>
        new(JsModuleReferrerKind.Script, null, label ?? throw new ArgumentNullException(nameof(label)));

    /// <summary>A request from the host, optionally relative to <paramref name="baseKey"/>.</summary>
    public static JsModuleReferrer Host(JsModuleKey? baseKey) => new(JsModuleReferrerKind.Host, baseKey, null);

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        JsModuleReferrerKind.Module => $"module '{Key}'",
        JsModuleReferrerKind.Script => $"script '{Label}'",
        _ => Key is { } key ? $"host (base '{key}')" : "host",
    };
}

/// <summary>One request the host resolves.</summary>
public readonly struct JsModuleRequest
{
    /// <summary>Describes a request.</summary>
    public JsModuleRequest(
        JsModuleReferrer referrer,
        string specifier,
        IReadOnlyList<KeyValuePair<string, string>>? attributes,
        JsModuleRequestKind kind)
    {
        Referrer = referrer;
        Specifier = specifier ?? throw new ArgumentNullException(nameof(specifier));
        Attributes = attributes ?? [];
        Kind = kind;
    }

    /// <summary>Who asked.</summary>
    public JsModuleReferrer Referrer { get; }

    /// <summary>The specifier text exactly as written.</summary>
    public string Specifier { get; }

    /// <summary>Import attributes in source order; empty when there are none.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Attributes { get; }

    /// <summary>Static import, dynamic import or host root.</summary>
    public JsModuleRequestKind Kind { get; }
}

/// <summary>Why a request or a load failed.</summary>
public enum JsModuleFailure
{
    /// <summary>Nothing exists under the specifier or key.</summary>
    NotFound,

    /// <summary>The host's policy refuses it.</summary>
    Refused,

    /// <summary>The specifier's form is not one the host resolves.</summary>
    UnsupportedSpecifier,

    /// <summary>The request carries an import attribute the provider or host does not support.</summary>
    UnsupportedAttributes,
}

/// <summary>The host's answer to <see cref="IJsModuleHost.Resolve"/>.</summary>
public readonly struct JsModuleResolution
{
    private JsModuleResolution(JsModuleKey key, JsModuleFailure failure, string? message, bool resolved)
    {
        Key = key;
        Failure = failure;
        Message = message;
        IsResolved = resolved;
    }

    /// <summary>Whether a key was produced.</summary>
    public bool IsResolved { get; }

    /// <summary>The canonical key when <see cref="IsResolved"/>.</summary>
    public JsModuleKey Key { get; }

    /// <summary>The failure when not <see cref="IsResolved"/>.</summary>
    public JsModuleFailure Failure { get; }

    /// <summary>The host's message for a failure.</summary>
    public string? Message { get; }

    /// <summary>The request names <paramref name="key"/>.</summary>
    public static JsModuleResolution Resolved(JsModuleKey key) =>
        key.Value is null ? throw new ArgumentException("A resolution needs a key.", nameof(key)) : new(key, default, null, true);

    /// <summary>The request cannot be resolved.</summary>
    public static JsModuleResolution Failed(JsModuleFailure failure, string message) => new(default, failure, message, false);
}

/// <summary>The host's answer to <see cref="IJsModuleHost.LoadAsync"/>.</summary>
public readonly struct JsModuleSource
{
    /// <summary>Module source text, with an optional label (the key when omitted).</summary>
    public JsModuleSource(string text, string? label = null)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Label = label;
        IsFailed = false;
        Failure = default;
        Message = null;
    }

    private JsModuleSource(JsModuleFailure failure, string message)
    {
        Text = null;
        Label = null;
        IsFailed = true;
        Failure = failure;
        Message = message;
    }

    /// <summary>The source text, or null for a failure.</summary>
    public string? Text { get; }

    /// <summary>The label used in failures and stacks; the key when null. Never affects identity or permission.</summary>
    public string? Label { get; }

    /// <summary>Whether the load failed.</summary>
    public bool IsFailed { get; }

    /// <summary>The failure when <see cref="IsFailed"/>.</summary>
    public JsModuleFailure Failure { get; }

    /// <summary>The host's message for a failure.</summary>
    public string? Message { get; }

    /// <summary>The load failed.</summary>
    public static JsModuleSource Failed(JsModuleFailure failure, string message) => new(failure, message);
}

/// <summary>Options fixed when a module map is opened.</summary>
public sealed class JsModuleOptions
{
    /// <summary>Whether guest <c>import()</c> may use the map. Default true.</summary>
    public bool AllowDynamicImport { get; init; } = true;

    /// <summary>Whether <c>import()</c> from classic or host script may use the map. Default true.</summary>
    public bool AllowImportFromScripts { get; init; } = true;

    /// <summary>The most module records the map may hold. Exceeding it is a load failure. Default 10,000.</summary>
    public int MaxModules { get; init; } = 10_000;
}

/// <summary>Where a module record is in evaluation.</summary>
public enum JsModuleStatus
{
    /// <summary>Linked and not yet evaluated.</summary>
    Linked,

    /// <summary>Its body is running.</summary>
    Evaluating,

    /// <summary>Waiting for top-level await or an asynchronous dependency.</summary>
    EvaluatingAsync,

    /// <summary>Its evaluation promise fulfilled.</summary>
    Evaluated,

    /// <summary>Its evaluation threw; see <see cref="IJsModule.EvaluationError"/>.</summary>
    Errored,
}

/// <summary>The phase a module failure happened in. Evaluation errors are guest errors instead.</summary>
public enum JsModulePhase
{
    /// <summary>The host's <see cref="IJsModuleHost.Resolve"/> failed or threw.</summary>
    Resolve,

    /// <summary>The host's <see cref="IJsModuleHost.LoadAsync"/> failed or threw, or the map is full.</summary>
    Load,

    /// <summary>The source is not a valid module.</summary>
    Parse,

    /// <summary>The graph could not be linked.</summary>
    Link,
}

/// <summary>A module graph failed before evaluation.</summary>
/// <remarks>
/// Evaluation errors are not reported this way: they are the guest's, so they reject the evaluation
/// promise and, where they escape a host call, use <see cref="JsEngineException"/>. A parse failure
/// carries the <see cref="JsEngineException"/> the provider's shared boundary produced as its
/// <see cref="Exception.InnerException"/>.
/// </remarks>
public sealed class JsModuleException : Exception
{
    /// <summary>Describes a failure.</summary>
    public JsModuleException(
        string message,
        JsModulePhase phase,
        string specifier,
        JsModuleReferrer referrer,
        JsModuleKey? key = null,
        string? sourceLabel = null,
        JsValue thrown = default,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Phase = phase;
        Specifier = specifier;
        Referrer = referrer;
        Key = key;
        SourceLabel = sourceLabel;
        Thrown = thrown;
    }

    /// <summary>Where the graph failed.</summary>
    public JsModulePhase Phase { get; }

    /// <summary>The specifier of the request that failed.</summary>
    public string Specifier { get; }

    /// <summary>Who made that request.</summary>
    public JsModuleReferrer Referrer { get; }

    /// <summary>The key, once resolved.</summary>
    public JsModuleKey? Key { get; }

    /// <summary>The failing module's label, once its source is known.</summary>
    public string? SourceLabel { get; }

    /// <summary>The guest value the failure corresponds to, such as a SyntaxError, or <see cref="JsValue.Missing"/>.</summary>
    public JsValue Thrown { get; }
}
