namespace Broiler.JSeal;

/// <summary>
/// What a <see cref="JsValue"/> holds. This is the ECMAScript type of the value, with two additions
/// the language does not have but a host needs.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Missing"/> is zero, deliberately.</b> Broiler.JS's <c>Arguments</c> indexer returns a
/// CLR <see langword="null"/> — not <c>undefined</c> — for an index past the end, and 598 argument
/// reads in the DOM bridge depend on being able to tell those apart before coercing. A default-valued
/// <see cref="JsValue"/> is therefore "no value was passed", which is a different fact from "the value
/// passed was <c>undefined</c>" for anything that overload-resolves on arity. It is zero so that
/// <c>default(JsValue)</c> means it without a constructor running, and because Broiler.VM's own value
/// layout numbers its empty slot zero for the same reason.
/// </para>
/// <para>
/// <b><see cref="Function"/> and <see cref="Array"/> are not ECMAScript types</b> — both are Object.
/// They are split out because the bridge asks "is this callable" 59 times and "is this an array" 8,
/// and on an engine whose values are opaque handles that question costs a call back into the engine.
/// A provider knows the answer when it mints the handle, so it answers once, there.
/// </para>
/// </remarks>
public enum JsValueKind : byte
{
    /// <summary>No value at all — an argument that was not supplied. See the remarks on this enum.</summary>
    Missing = 0,

    /// <summary>The <c>undefined</c> value.</summary>
    Undefined = 1,

    /// <summary>The <c>null</c> value.</summary>
    Null = 2,

    /// <summary>A boolean.</summary>
    Boolean = 3,

    /// <summary>A number.</summary>
    Number = 4,

    /// <summary>A string.</summary>
    String = 5,

    /// <summary>A symbol.</summary>
    Symbol = 6,

    /// <summary>A BigInt.</summary>
    BigInt = 7,

    /// <summary>An ordinary object — anything not covered by <see cref="Function"/> or <see cref="Array"/>.</summary>
    Object = 8,

    /// <summary>A callable object.</summary>
    Function = 9,

    /// <summary>An Array exotic object.</summary>
    Array = 10,
}

