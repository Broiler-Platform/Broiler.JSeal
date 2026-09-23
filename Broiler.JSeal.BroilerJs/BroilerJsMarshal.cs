using Broiler.JSeal.Providers;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;

namespace Broiler.JSeal.BroilerJs;

/// <summary>
/// The only place in this assembly where a <see cref="JsValue"/> becomes a <see cref="JSValue"/> or
/// the reverse.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the conversions are concentrated rather than inlined at their call sites.</b> Every one of
/// them is a decision, not a cast: which JSEAL kind an engine object is minted under is answered once
/// here and then carried in the handle (see <see cref="JsProviderValue"/>), and a second copy of that
/// decision somewhere else in the provider is a second place for it to be answered differently. The
/// two that would go wrong quietly are the ones this type exists to make unrepeatable â€” a CLR
/// <see langword="null"/> becoming <c>undefined</c> instead of <see cref="JsValue.Missing"/>, and a
/// <c>JSContext</c> failing to be recognised as an object because the type test that caught it was
/// written for <c>JSObject</c> in one file and for something narrower in another.
/// </para>
/// <para>
/// <b>Nothing is allocated to wrap an object.</b> <see cref="Wrap"/> hands the engine's own instance
/// to <see cref="JsProviderValue"/>, so the <c>JSObject</c> the bridge's wrapper tables are keyed on
/// is the same instance before and after a round trip, and <c>el === el</c> stays the question it
/// always was. Only the primitives are re-materialised, because Broiler.JS's are sealed classes a
/// handle cannot carry inline.
/// </para>
/// </remarks>
internal static class BroilerJsMarshal
{
    /// <summary>
    /// The engine value behind a JSEAL handle.
    /// </summary>
    /// <remarks>
    /// <see cref="JsValueKind.Missing"/> unwraps to <c>undefined</c>. That is not a contradiction of
    /// the distinction the contract draws: Missing means "the host never supplied a value", and the
    /// only thing that can be handed to an engine in place of a value it was promised is
    /// <c>undefined</c>. The distinction is preserved in the direction it is observed â€” reading an
    /// argument â€” and collapsed in the direction it cannot be.
    /// </remarks>
    internal static JSValue Unwrap(JsValue value) => value.Kind switch
    {
        JsValueKind.Missing or JsValueKind.Undefined => JSUndefined.Value,
        JsValueKind.Null => JSNull.Value,
        JsValueKind.Boolean => value.AsBoolean ? JSBoolean.True : JSBoolean.False,
        JsValueKind.Number => new JSNumber(value.AsNumber),
        JsValueKind.String => new JSString(value.AsString!),

        // Symbol, BigInt, Object, Function and Array are all handles over the engine's own value,
        // minted by Wrap below, so unwrapping them is the identity. A handle whose reference came
        // from another provider is a host bug rather than a page one; it is refused with the same
        // JsEngineException the VM provider raises for one (B06), where a bare cast used to raise an
        // InvalidCastException that no host catch block is written against.
        _ => JsProviderValue.ReferenceOf(value) as JSValue
            ?? throw new JsEngineException("the value was minted by a different JavaScript engine and means nothing here"),
    };

    /// <summary>
    /// A JSEAL handle over an engine value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A CLR <see langword="null"/> is <see cref="JsValue.Missing"/>, not <c>undefined</c>.</b>
    /// Broiler.JS's <c>Arguments</c> indexer answers null for an index past the end, and the bridge's
    /// argument reads are entitled to tell "not passed" from "passed undefined".
    /// This is the single line that keeps that true; every other reader of an argument goes through
    /// <c>JsCall</c>, which is filled from here.
    /// </para>
    /// <para>
    /// Callability is an engine predicate, not a CLR type test: callable proxies derive from
    /// <c>JSObject</c>, and retain callability after revocation. Store the proxy itself to preserve
    /// its identity. Array classification still means an actual <c>JSArray</c>, not a proxy to one.
    /// The general object arm also covers the realm's global <c>JSContext</c>.
    /// </para>
    /// </remarks>
    internal static JsValue Wrap(JSValue? value)
    {
        if (value is null)
            return JsValue.Missing;

        // IsNull/IsUndefined are reference comparisons against the engine's singletons, so they are
        // cheaper than the type tests below and are asked first for that reason alone.
        if (value.IsUndefined)
            return JsValue.Undefined;

        if (value.IsNull)
            return JsValue.Null;

        return value switch
        {
            _ when value.IsFunction => JsProviderValue.Function(value),
            JSArray array => JsProviderValue.Array(array),
            JSObject @object => JsProviderValue.Object(@object),
            JSString @string => JsValue.String(@string.ToString()),
            JSNumber number => JsValue.Number(number.DoubleValue),
            JSBoolean boolean => JsValue.Boolean(boolean.BooleanValue),

            // Symbols and BigInts stay opaque: neither has a lossless inline representation in a
            // handle. This used to add "and the bridge never reads one â€” it only forwards them",
            // which was false: every coercion a page can hand a BigInt to reads one, and the handle's
            // own truthiness read 0n as true until IJsValues.ToBoolean existed to ask. The last arm is
            // also wider than its name. It takes every engine primitive not matched above, and this
            // engine's decimal (the 0m literal) is one, so a BigInt-kind handle from this provider may
            // carry either type and anything that reads one asks the value rather than casting it.
            _ when value.IsSymbol => JsProviderValue.Symbol(value),
            _ => JsProviderValue.BigInt(value),
        };
    }

    /// <summary>
    /// The engine object behind a handle, for the operations that can only act on one.
    /// </summary>
    /// <remarks>
    /// Raises a JSEAL exception rather than the engine's, because a host that passed a number where
    /// an object was required made a host mistake, and <see cref="JsEngineException"/> is what a host
    /// catch block is written against.
    /// </remarks>
    internal static JSObject AsObject(JsValue value, string operation)
    {
        if (JsProviderValue.ReferenceOf(value) is JSObject @object)
            return @object;

        throw new JsEngineException($"{operation} requires an object; received {value.Kind}.");
    }
}

