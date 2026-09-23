using Broiler.JSeal.Providers;
using Broiler.VM.Profile.JavaScript;

namespace Broiler.JSeal.Vm;

/// <summary>Converts between VM host values and JSEAL handles.</summary>
/// <remarks>
/// Object identity uses one boxed JsHostValue per canonical JsHostRef, cached in a weak table.
/// This preserves handle identity without retaining every object for the realm's lifetime.
/// Unwrap refuses values belonging to another engine before accessing their storage.
/// </remarks>
internal static class VmMarshal
{
    /// <summary>A VM value as a JSEAL handle.</summary>
    internal static JsValue Wrap(JsHostValue value) => value.Kind switch
    {
        JsHostValueKind.Missing => JsValue.Missing,
        JsHostValueKind.Undefined => JsValue.Undefined,
        JsHostValueKind.Null => JsValue.Null,
        JsHostValueKind.Boolean => JsValue.Boolean(value.AsBoolean()),
        JsHostValueKind.Number => JsValue.Number(value.AsNumber()),
        JsHostValueKind.String => JsValue.String(value.AsString()),
        JsHostValueKind.Symbol => JsProviderValue.Symbol(Identity(value)),
        JsHostValueKind.Function => JsProviderValue.Function(Identity(value)),
        JsHostValueKind.Array => JsProviderValue.Array(Identity(value)),
        _ => JsProviderValue.Object(Identity(value)),
    };

    /// <summary>A JSEAL handle as a VM value.</summary>
    /// <remarks>
    /// <para>
    /// <b>A handle from another engine is refused here rather than dereferenced.</b> A host with two
    /// providers linked will hand the same <see cref="JsValue"/> to both, and the reference behind
    /// an object handle is whatever the engine that minted it put there - so a cast without a test
    /// is an invalid cast waiting for the second engine to exist. The realm's own foreign-realm
    /// check sits behind this one and catches the narrower case of a handle from a different realm
    /// of this same engine.
    /// </para>
    /// <para>
    /// <b><see cref="JsValueKind.Missing"/> unwraps to <c>undefined</c></b>, as on the Broiler.JS
    /// provider: Missing means the host supplied no value, and the only thing an engine can be handed
    /// in place of a value it needs is <c>undefined</c>. Passed on as <see cref="JsHostValue.Missing"/>,
    /// the profile resolves it to its uninitialised-binding marker, and a guest reading the argument,
    /// property or callback result throws a ReferenceError instead of seeing <c>undefined</c>.
    /// </para>
    /// </remarks>
    internal static JsHostValue Unwrap(JsValue value) => value.Kind switch
    {
        JsValueKind.Missing => JsHostValue.Undefined,
        JsValueKind.Undefined => JsHostValue.Undefined,
        JsValueKind.Null => JsHostValue.Null,
        JsValueKind.Boolean => JsHostValue.Boolean(value.AsBoolean),
        JsValueKind.Number => JsHostValue.Number(value.AsNumber),
        JsValueKind.String => JsHostValue.String(value.AsString!),
        JsValueKind.BigInt => throw new JsEngineException(
            "the Broiler.VM JavaScript profile has no BigInt, so a BigInt handle cannot have come "
                + "from one of its realms"),
        _ => JsProviderValue.ReferenceOf(value) is JsHostValue carried
            ? carried
            : throw new JsEngineException(
                "the value was minted by a different JavaScript engine and means nothing here"),
    };

    /// <summary>Every argument in a span, converted once.</summary>
    internal static JsHostValue[] UnwrapAll(ReadOnlySpan<JsValue> arguments)
    {
        if (arguments.Length == 0)
            return [];

        var converted = new JsHostValue[arguments.Length];

        for (var at = 0; at < arguments.Length; at++)
            converted[at] = Unwrap(arguments[at]);

        return converted;
    }

    /// <summary>
    /// The identity object a handle carries for a value the engine owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It boxes the whole <see cref="JsHostValue"/> rather than the bare <c>JsHostRef</c>, because
    /// the kind and the identity have to travel together: the VM answers <c>Unwrap</c> with a value,
    /// not with a reference, and reconstructing the kind from the reference on the way back would be
    /// asking the engine a question the handle was invented to avoid. The box is allocated once per
    /// object per realm - the profile's own weak table makes the inner identity canonical, and this
    /// caches the box against it - so handle equality stays reference equality.
    /// </para>
    /// <para>
    /// <b>This box is what <c>JsValue.ObjectIdentity</c> hands a host, so do not make it a strong
    /// table.</b> The bridge keys its per-object registries on that member, and their weakness is
    /// this table's weakness: the box lives exactly as long as the <c>JsHostRef</c>, which lives
    /// exactly as long as the guest object. A strong table here would pin every object the bridge
    /// has ever seen for the life of the realm.
    /// </para>
    /// </remarks>
    private static object Identity(JsHostValue value)
    {
        var reference = value.AsRef()
            ?? throw new JsEngineException("an object-kinded VM value carried no identity");

        return Boxes.GetValue(reference, _ => value);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsHostRef, object>
        Boxes = new();
}

