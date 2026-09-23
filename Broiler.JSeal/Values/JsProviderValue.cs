namespace Broiler.JSeal.Providers;

/// <summary>The provider-facing factories and storage access for opaque engine handles.</summary>
/// <remarks>
/// These members are public so providers can be implemented outside this repository. Host bindings
/// should use JsValue and IJsRealm instead of inspecting engine storage. The Providers namespace
/// makes that boundary visible in source review; no namespace-ratchet script exists in this checkout.
/// Providers classify callability and array identity when wrapping a value, so inspecting a handle
/// does not need another engine crossing.
/// </remarks>
public static class JsProviderValue
{
    /// <summary>Wraps an engine object that is neither callable nor an Array exotic.</summary>
    public static JsValue Object(object reference) =>
        JsValue.FromReference(JsValueKind.Object, reference ?? throw new ArgumentNullException(nameof(reference)));

    /// <summary>Wraps a callable engine object.</summary>
    public static JsValue Function(object reference) =>
        JsValue.FromReference(JsValueKind.Function, reference ?? throw new ArgumentNullException(nameof(reference)));

    /// <summary>Wraps an Array exotic engine object.</summary>
    public static JsValue Array(object reference) =>
        JsValue.FromReference(JsValueKind.Array, reference ?? throw new ArgumentNullException(nameof(reference)));

    /// <summary>Wraps an engine symbol.</summary>
    public static JsValue Symbol(object reference) =>
        JsValue.FromReference(JsValueKind.Symbol, reference ?? throw new ArgumentNullException(nameof(reference)));

    /// <summary>Wraps an engine BigInt.</summary>
    /// <remarks>
    /// The reference must denote one integer for as long as the handle lives, because
    /// <see cref="JsValue"/>'s operator treats two handles with one reference as one value; it need
    /// not be canonical. A provider implements <see cref="IJsValues.ToBoolean"/> and
    /// <see cref="IJsValues.IsStrictlyEqual"/> for the handles it mints and refuses any other
    /// engine's with <see cref="JsEngineException"/>.
    /// </remarks>
    public static JsValue BigInt(object reference) =>
        JsValue.FromReference(JsValueKind.BigInt, reference ?? throw new ArgumentNullException(nameof(reference)));

    /// <summary>
    /// The engine value behind <paramref name="value"/>, or <see langword="null"/> when the handle
    /// carries a primitive inline (and so the provider must materialise one of its own).
    /// </summary>
    public static object? ReferenceOf(JsValue value) => value.Reference;
}

