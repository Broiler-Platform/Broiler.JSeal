namespace Broiler.JSeal;

/// <summary>A host callback's realm, receiver, arguments and construction target.</summary>
/// <remarks>
/// The realm travels with the callback so hosts need no ambient realm lookup. This ref struct borrows
/// a span valid only during the callback; copy any values that must be retained. The frame itself
/// needs no heap storage. Bundled providers use inline buffers through eight arguments and pooled
/// arrays beyond that; conversions, pool misses and engine crossings may still allocate.
/// An out-of-range argument is Missing, distinct from an explicitly supplied undefined.
/// </remarks>
public readonly ref struct JsCall
{
    private readonly ReadOnlySpan<JsValue> _arguments;

    /// <summary>Builds a call frame. Providers only.</summary>
    public JsCall(IJsRealm realm, JsValue thisValue, ReadOnlySpan<JsValue> arguments, JsValue newTarget = default)
    {
        Realm = realm ?? throw new ArgumentNullException(nameof(realm));
        This = thisValue;
        _arguments = arguments;
        NewTarget = newTarget;
    }

    /// <summary>The realm this call is running in. Never <see langword="null"/>.</summary>
    public IJsRealm Realm { get; }

    /// <summary>The receiver â€” <c>this</c> inside the called function.</summary>
    public JsValue This { get; }

    /// <summary>The construction target, or Missing for an ordinary call.</summary>
    public JsValue NewTarget { get; }

    /// <summary>How many arguments were supplied.</summary>
    public int Length => _arguments.Length;

    /// <summary>
    /// The argument at <paramref name="index"/>, or <see cref="JsValue.Missing"/> when fewer were
    /// supplied. Never throws for an out-of-range index; see the remarks on this type.
    /// </summary>
    public JsValue this[int index] =>
        (uint)index < (uint)_arguments.Length ? _arguments[index] : JsValue.Missing;

    /// <summary>Every supplied argument, for the operations that forward them on unchanged.</summary>
    public ReadOnlySpan<JsValue> Arguments => _arguments;
}

/// <summary>
/// A host function callable from JavaScript.
/// </summary>
/// <remarks>
/// The shape mirrors Broiler.JS's <c>JSFunctionDelegate</c> (<c>JSValue F(in Arguments a)</c>) closely
/// enough that migrating a callback body is a change of vocabulary rather than of structure, which is
/// what makes a 250-file port something parallel agents can do against a build gate. Returning
/// <see cref="JsValue.Missing"/> is treated as returning <c>undefined</c>, so a body that falls off
/// the end of a <c>void</c>-shaped operation does the right thing.
/// </remarks>
public delegate JsValue JsNativeFunction(in JsCall call);

