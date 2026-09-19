using System.Diagnostics.CodeAnalysis;

using Broiler.JavaScript.Ast.Misc;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Array.Typed;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;

namespace Broiler.JSeal.BroilerJs;

/// <summary>
/// <see cref="IJsValues"/>: minting the realm's own values, the two coercions that can run page
/// script, and the truthiness test a handle cannot perform for a BigInt.
/// </summary>
internal sealed partial class BroilerJsRealm
{
    /// <inheritdoc />
    public JsValue NewObject()
    {
        using var scope = Enter();

        // The realm scope is doing real work on this line: JSObject's parameterless constructor takes
        // its prototype from the current context, so the same expression outside the scope produces
        // an object with no Object.prototype.
        return BroilerJsMarshal.Wrap(new JSObject());
    }

    /// <inheritdoc />
    public JsValue NewArray(ReadOnlySpan<JsValue> elements = default)
    {
        using var scope = Enter();

        if (elements.IsEmpty)
            return BroilerJsMarshal.Wrap(new JSArray());

        var items = new JSValue[elements.Length];
        for (var i = 0; i < elements.Length; i++)
            items[i] = BroilerJsMarshal.Unwrap(elements[i]);

        return BroilerJsMarshal.Wrap(new JSArray(items));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The engine's own buffer type over a copy of the span. This provider pays nothing for the
    /// contract because the type is the engine's and the bytes are a field on it; the other one has
    /// no binary member on its host surface and reaches the realm's <c>ArrayBuffer</c> intrinsic
    /// instead.
    /// </remarks>
    public JsValue NewArrayBuffer(ReadOnlySpan<byte> bytes)
    {
        using var scope = Enter();

        return BroilerJsMarshal.Wrap(new JSArrayBuffer(bytes.ToArray()));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>The <c>SharedArrayBuffer</c> exclusion is load-bearing and is not defensive.</b> This
    /// engine declares <c>SharedArrayBuffer : JSArrayBuffer</c>, so a plain type test answers
    /// <see langword="true"/> for one - and a shared buffer is not a <c>BufferSource</c>, so
    /// <c>new Blob([sharedBuffer])</c> would silently produce the buffer's bytes where a browser
    /// produces the string <c>"[object SharedArrayBuffer]"</c>.
    /// </para>
    /// <para>
    /// A detached buffer answers <see langword="true"/> with no bytes, as the contract specifies:
    /// the field still holds the array after detachment, so the flag has to be read rather than the
    /// length.
    /// </para>
    /// </remarks>
    public bool TryGetArrayBufferBytes(JsValue value, [NotNullWhen(true)] out byte[]? bytes)
    {
        if (BroilerJsMarshal.Unwrap(value) is not JSArrayBuffer buffer || buffer is SharedArrayBuffer)
        {
            bytes = null;
            return false;
        }

        bytes = buffer.Detached ? [] : buffer.Buffer;
        return true;
    }

    /// <summary>
    /// A non-constructable host function.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>createPrototype: false</c> is not an optimisation dial; it is what makes the function
    /// non-constructable, which is what WebIDL requires of an operation or an attribute accessor.</b>
    /// The engine's <c>JSConstructorOperations.IsConstructor</c> tests
    /// <c>prototype != null || IsConstructable</c>, so a function built with a prototype object can be
    /// <c>new</c>-ed â€” and <c>el.setAttribute.prototype</c> would answer an object where a browser
    /// answers <c>undefined</c>.
    /// </para>
    /// <para>
    /// It is also the memory fix <c>DomBridge/DomFunction.cs</c> recorded, and the reason that file
    /// existed -- it is deleted, and in this repository's history, because every member the bridge
    /// installs now comes through here instead of through a bridge type the bridge had to remember
    /// to use. A node's wrapper was built eagerly with roughly 149 own members on 2026-09-08, each of
    /// which was allocating a <c>JSFunction</c> <em>plus</em> an unreachable prototype object plus
    /// that object's <c>constructor</c> back-reference. Dropping the prototype roughly halved the
    /// retained cost of a wrapper â€” the difference between a document of 10k script-created elements
    /// fitting in the WPT per-test memory budget and being aborted. Every member the bridge installs
    /// comes through here, so the same saving now belongs to the provider rather than to a bridge
    /// type the bridge had to remember to use.
    /// </para>
    /// </remarks>
    public JsValue NewMethod(string name, JsNativeFunction body, int length = 0)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var scope = Enter();

        var function = new JSFunction(
            MethodTrampoline(body), name ?? string.Empty, StringSpan.Empty, length, createPrototype: false);

        return BroilerJsMarshal.Wrap(function);
    }

    /// <summary>
    /// A constructable host function â€” an interface object a page may <c>new</c>.
    /// </summary>
    /// <remarks>
    /// The same constructor with <c>createPrototype: true</c>, which mints the <c>prototype</c> object
    /// an interface's members are installed on and makes the function pass the engine's constructor
    /// test. The bridge had sixteen of these against 1,246 members on 2026-09-08, which is why this
    /// is the method that has to be asked for by name and <see cref="NewMethod"/> is the default.
    /// </remarks>
    public JsValue NewConstructor(string name, JsNativeFunction body, int length = 0)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var scope = Enter();

        var function = new JSFunction(
            ConstructorTrampoline(body), name ?? string.Empty, StringSpan.Empty, length, createPrototype: true);

        return BroilerJsMarshal.Wrap(function);
    }

    /// <inheritdoc />
    public JsValue NewExotic(IJsExotic handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        using var scope = Enter();

        return BroilerJsMarshal.Wrap(new BroilerJsExoticObject(this, handler));
    }

    /// <summary>
    /// ECMAScript <c>ToString</c>.
    /// </summary>
    /// <remarks>
    /// Broiler.JS spells this <c>JSValue.ToString()</c>, and on a <c>JSObject</c> that override runs
    /// the object's own <c>toString</c> â€” page script, which may throw or re-enter the realm. That is
    /// exactly why <see cref="JsValue.ToString"/> on the handle refuses to do it and answers
    /// <c>[object]</c> instead: the cheap rendering and the observable coercion are different
    /// questions, and only this one is entitled to run code.
    /// </remarks>
    public string ToJsString(JsValue value)
    {
        using var scope = Enter();
        return BroilerJsMarshal.Unwrap(value).ToString();
    }

    /// <summary>
    /// ECMAScript <c>ToNumber</c>.
    /// </summary>
    /// <remarks>
    /// <c>DoubleValue</c> is the engine's coercion, not a field read: on an object it runs
    /// <c>ToPrimitive</c> with a number hint, which may reach a <c>valueOf</c> the page wrote, and on
    /// a Symbol it throws â€” as the specification says it must.
    /// </remarks>
    public double ToNumber(JsValue value)
    {
        using var scope = Enter();
        return BroilerJsMarshal.Unwrap(value).DoubleValue;
    }

    /// <summary>
    /// ECMAScript <c>ToBoolean</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only the BigInt kind reaches the engine.</b> Every other kind is answered by the handle, which
    /// agrees with this engine's own <c>BooleanValue</c> for each of them, and the bridge asks on
    /// argument reads, where materialising an engine string or number just to ask would be the cost of
    /// the abstraction rather than of the work. A disposed realm is refused on both paths, as it is by
    /// every other member here.
    /// </para>
    /// <para>
    /// <b>The engine arm asks the value, not a type, because the kind covers two types.</b>
    /// <see cref="BroilerJsMarshal.Wrap"/> mints under <see cref="JsValueKind.BigInt"/> every engine
    /// primitive it does not otherwise recognise, and this engine has a decimal literal (<c>0m</c>)
    /// besides its BigInt. Both answer <c>BooleanValue</c> with a comparison against zero and run no
    /// page script; a cast to the BigInt type would have thrown for the decimal.
    /// </para>
    /// </remarks>
    public bool ToBoolean(JsValue value)
    {
        if (value.Kind is not JsValueKind.BigInt)
        {
            ThrowIfDisposed();
            return value.AsBoolean;
        }

        using var scope = Enter();
        return BroilerJsMarshal.Unwrap(value).BooleanValue;
    }
}

