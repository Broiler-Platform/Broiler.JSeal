namespace Broiler.JSeal.Providers;

/// <summary>
/// The mint side of <see cref="JsValue"/>: how a provider turns one of its engine's values into a
/// handle the bridge can hold.
/// </summary>
/// <remarks>
/// <para>
/// This is the one part of JSEAL a binding must never call. It is public rather than internal so that
/// a provider can be written outside this repository â€” a JSEAL whose only possible implementations are
/// the two assemblies next to it would be a naming exercise rather than an abstraction â€” and it lives
/// in its own <c>Providers</c> namespace so that a file which needs it says so in its usings, which is
/// what <c>scripts/check-engine-neutrality.sh</c> counts.
/// </para>
/// <para>
/// <b>The kind is the provider's answer, given once.</b> A provider knows whether the value it is
/// wrapping is callable or an Array exotic at the moment it wraps it, and answering then costs
/// nothing. Asking later â€” <c>JsValue.IsFunction</c> on a handle whose engine is behind a C API â€”
/// would cost a call across the boundary at each of the bridge's callability tests (59 on 2026-09-08).
/// </para>
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
    public static JsValue BigInt(object reference) =>
        JsValue.FromReference(JsValueKind.BigInt, reference ?? throw new ArgumentNullException(nameof(reference)));

    /// <summary>
    /// The engine value behind <paramref name="value"/>, or <see langword="null"/> when the handle
    /// carries a primitive inline (and so the provider must materialise one of its own).
    /// </summary>
    public static object? ReferenceOf(JsValue value) => value.Reference;
}

