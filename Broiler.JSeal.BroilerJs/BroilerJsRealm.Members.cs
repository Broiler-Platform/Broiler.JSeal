using Broiler.JavaScript.Ast.Misc;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.JSeal.BroilerJs;

/// <summary>
/// <see cref="IJsMembers"/>: installing, reading and removing an object's members, and its prototype
/// link.
/// </summary>
internal sealed partial class BroilerJsRealm
{
    /// <summary>
    /// <see cref="JsPropertyFlags"/> as Broiler.JS spells it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole mapping, in one place because it is one decision:
    /// </para>
    /// <code>
    ///   flags                    kind        JSPropertyAttributes
    ///   â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€  â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€  â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    ///   Default                  value       EnumerableConfigurableValue      (908 sites on 2026-09-08)
    ///   Default                  accessor    EnumerableConfigurableProperty   (331)
    ///   NonEnumerable            value       ConfigurableValue                (6)
    ///   NonEnumerable            accessor    ConfigurableProperty             (1)
    ///   â”€â”€ anything else, by rule â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    ///   Enumerable               either      + Enumerable
    ///   Configurable             either      + Configurable
    ///   no Writable              value       + Readonly
    ///   no Writable              accessor    (ignored â€” see below)
    /// </code>
    /// <para>
    /// The first four are every combination the DOM bridge uses; the rule below them is what the rest
    /// of the enum means, so a caller outside the bridge gets an answer rather than an exception.
    /// </para>
    /// <para>
    /// <b><see cref="JsPropertyFlags.Writable"/> is deliberately ignored for an accessor.</b> The
    /// contract says an accessor's writability is whether it has a setter, and the engine agrees â€”
    /// <c>Readonly</c> on a <c>Property</c> is a different assertion, and setting it on the read-only
    /// IDL attributes that already express themselves with a null setter (216 on 2026-09-08) would say
    /// the same thing twice in two vocabularies. Whichever one a future reader believed would be the
    /// one that was wrong somewhere.
    /// </para>
    /// <para>
    /// The value/accessor split is a parameter here and not a flag for the reason
    /// <c>JsPropertyFlags.cs</c> gives: it is which method the caller called, so it cannot disagree
    /// with itself.
    /// </para>
    /// </remarks>
    private static JSPropertyAttributes ToEngineAttributes(JsPropertyFlags flags, bool accessor)
    {
        var attributes = accessor ? JSPropertyAttributes.Property : JSPropertyAttributes.Value;

        if (flags.HasFlag(JsPropertyFlags.Enumerable))
            attributes |= JSPropertyAttributes.Enumerable;

        if (flags.HasFlag(JsPropertyFlags.Configurable))
            attributes |= JSPropertyAttributes.Configurable;

        if (!accessor && !flags.HasFlag(JsPropertyFlags.Writable))
            attributes |= JSPropertyAttributes.Readonly;

        return attributes;
    }

    /// <inheritdoc />
    public void DefineValue(JsValue target, string name, JsValue value, JsPropertyFlags flags = JsPropertyFlags.Default)
    {
        using var scope = Enter();

        BroilerJsMarshal.AsObject(target, nameof(DefineValue))
            .FastAddValue((KeyString)name, BroilerJsMarshal.Unwrap(value), ToEngineAttributes(flags, accessor: false));
    }

    /// <summary>
    /// Installs an accessor property.
    /// </summary>
    /// <remarks>
    /// <b>A read-only attribute is a CLR <see langword="null"/> setter, not a flag.</b> That is what
    /// the engine expects â€” <c>FastAddProperty</c> stores the pair as given and the property is
    /// writable exactly when the setter is there â€” and it is what the bridge already passed at 216
    /// read-only sites on 2026-09-08 (<c>sheet.FastAddProperty("href", NullFunction("get href"), null,
    /// â€¦)</c>). So a null <paramref name="setter"/> here becomes a null there, unmediated.
    /// </remarks>
    public void DefineAccessor(JsValue target, string name, JsNativeFunction getter, JsNativeFunction? setter, JsPropertyFlags flags = JsPropertyFlags.Default)
    {
        ArgumentNullException.ThrowIfNull(getter);
        using var scope = Enter();

        // Both accessors are non-constructable for the same reason a method is: `new el.__lookupGetter__('id')()`
        // is a TypeError in a browser, and a prototype object per accessor is the allocation
        // DomFunction was introduced to stop.
        var getterFunction = new JSFunction(
            MethodTrampoline(getter), $"get {name}", StringSpan.Empty, 0, createPrototype: false);

        var setterFunction = setter is null
            ? null
            : new JSFunction(
                MethodTrampoline(setter), $"set {name}", StringSpan.Empty, 1, createPrototype: false);

        BroilerJsMarshal.AsObject(target, nameof(DefineAccessor))
            .FastAddProperty((KeyString)name, getterFunction, setterFunction, ToEngineAttributes(flags, accessor: true));
    }

    /// <summary>
    /// Installs an integer-indexed data property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>uint</c> overload of <c>FastAddValue</c>, not the string one with the index rendered:
    /// the engine keeps indexed properties in a separate element array, and a property installed under
    /// the <em>string</em> "0" is not the one an array generic finds when it asks whether index 0 is
    /// present. That difference is what made a live collection produce a hole per element under
    /// <c>Array.prototype.map.call</c>.
    /// </para>
    /// <para>
    /// <b>On an Array, <c>length</c> follows the index, because the language says it does.</b> An
    /// Array exotic object grows its <c>length</c> when <c>[[DefineOwnProperty]]</c> installs a higher
    /// index; <c>FastAddValue</c>&apos;s indexed overload writes the element array without going
    /// through that, so an array built by this method reported the length it had before and was
    /// invisible to every generic that reads one â€” <c>for</c>, <c>join</c>, <c>forEach</c>, spread.
    /// Nothing in the bridge hit it because its indexed targets are ordinary objects carrying a
    /// <c>length</c> they manage themselves; the worker global&apos;s listener array is the first
    /// Array to reach here, and it found the gap immediately.
    /// </para>
    /// </remarks>
    public void DefineIndex(JsValue target, uint index, JsValue value, JsPropertyFlags flags = JsPropertyFlags.Default)
    {
        using var scope = Enter();

        var engineTarget = BroilerJsMarshal.AsObject(target, nameof(DefineIndex));
        engineTarget.FastAddValue(index, BroilerJsMarshal.Unwrap(value), ToEngineAttributes(flags, accessor: false));

        // index + 1 cannot overflow for a valid array index: 2^32-1 is not one, and the engine's own
        // element writer refuses it for the same reason.
        if (engineTarget is JSArray array && index != uint.MaxValue && array.ArrayLength <= index)
            array.ArrayLength = index + 1d;
    }

    /// <inheritdoc />
    public JsValue GetProperty(JsValue target, string name)
    {
        using var scope = Enter();
        return BroilerJsMarshal.Wrap(BroilerJsMarshal.Unwrap(target)[(KeyString)name]);
    }

    /// <inheritdoc />
    public JsValue GetIndex(JsValue target, uint index)
    {
        using var scope = Enter();
        return BroilerJsMarshal.Wrap(BroilerJsMarshal.Unwrap(target)[index]);
    }

    /// <inheritdoc />
    public void SetProperty(JsValue target, string name, JsValue value)
    {
        using var scope = Enter();
        BroilerJsMarshal.Unwrap(target)[(KeyString)name] = BroilerJsMarshal.Unwrap(value);
    }

    /// <inheritdoc />
    public bool HasProperty(JsValue target, string name)
    {
        using var scope = Enter();

        // HasProperty takes the key as a value rather than a KeyString because it is the `in`
        // operator, and `in` accepts anything coercible to a property key.
        return BroilerJsMarshal.Unwrap(target).HasProperty(new JSString(name)).BooleanValue;
    }

    /// <inheritdoc />
    public bool DeleteProperty(JsValue target, string name)
    {
        using var scope = Enter();
        return BroilerJsMarshal.Unwrap(target).Delete((KeyString)name).BooleanValue;
    }

    /// <summary>
    /// The object's own enumerable string-keyed property names, in property-creation order.
    /// </summary>
    /// <remarks>
    /// <c>inherited: false</c> is the whole difference between this and a <c>forâ€¦in</c>: the engine's
    /// key enumerator walks the prototype chain when asked to, and the bridge's two callers want own
    /// names only â€” <c>FetchBinding</c> reading an init object and <c>WorkerTransfer</c> a transfer
    /// list â€” so a walk would hand both enumerable inherited names. (This named the frame-globals
    /// sweep as the one caller; it reads <c>Object.getOwnPropertyNames</c> through host script.)
    /// </remarks>
    public IReadOnlyList<string> OwnPropertyNames(JsValue target)
    {
        using var scope = Enter();

        var names = new List<string>();
        var keys = BroilerJsMarshal.Unwrap(target).GetAllKeys(showEnumerableOnly: true, inherited: false);
        while (keys.MoveNext(out var key))
        {
            // The enumerator yields indexed keys as strings and skips symbols, which is what a
            // string-keyed contract wants; the guard is for the value-typed keys a future engine
            // change could start producing.
            if (key is not null && !key.IsSymbol)
                names.Add(key.ToString());
        }

        return names;
    }

    /// <summary>
    /// Points <paramref name="target"/>'s prototype chain at <paramref name="prototype"/>.
    /// </summary>
    /// <remarks>
    /// A plain assignment to <c>BasePrototypeObject</c>, as <c>DomBridge/ElementInterface.cs</c> does
    /// â€” the engine's setter is the [[SetPrototypeOf]] path, and it is what publishes the
    /// prototype-chain mutation that retires the caches keyed on the old chain. Reaching past it to
    /// the <c>prototypeChain</c> field would link the object and leave every one of those caches
    /// answering for a chain the object no longer has.
    /// </remarks>
    public void SetPrototype(JsValue target, JsValue prototype)
    {
        using var scope = Enter();

        BroilerJsMarshal.AsObject(target, nameof(SetPrototype)).BasePrototypeObject =
            prototype.IsNullish ? null! : BroilerJsMarshal.Unwrap(prototype);
    }

    /// <inheritdoc />
    public JsValue GetPrototype(JsValue target)
    {
        using var scope = Enter();
        return BroilerJsMarshal.Wrap(BroilerJsMarshal.Unwrap(target).GetPrototypeOf());
    }
}

