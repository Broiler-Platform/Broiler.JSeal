using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.JSeal.BroilerJs;

/// <summary>
/// A <c>JSObject</c> whose property lookup an <see cref="IJsExotic"/> completes — the one object in
/// this provider that has to know the engine's lookup <em>protocol</em> rather than only its types.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class is the whole of what six bridge classes used to be.</b> <c>DomCollectionBinding</c>'s
/// collection, <c>FormBinding</c>'s controls collection and form object, <c>StyleDeclarationBinding</c>'s
/// two declarations and <c>WebStorageBinding</c>'s storage area each subclassed <c>JSObject</c> and
/// overrode the same members, which made them the deepest engine coupling in the binding layer. The
/// members overridden here are exactly the ones they overrode, because those are the ones the engine
/// dispatches lookup through: <c>GetValue(KeyString, …)</c> for a named read,
/// <c>GetValue(uint, …)</c> for an indexed one, <c>SetValue(KeyString, …)</c> for a named write,
/// <c>HasProperty</c> for <c>in</c>, and <c>GetAllKeys</c> for enumeration.
/// </para>
/// <para>
/// <b>ORDERING IS LOAD-BEARING: the base lookup runs FIRST, and the handler answers only what it did
/// not.</b> Every one of the six classes did this — the class remarks of <c>DomCollectionBinding</c> and
/// <c>StyleDeclarationBinding</c> state it for their replacement handlers — and it is what
/// WebIDL's named-property semantics require. Getting it backwards is silently wrong rather than
/// loudly wrong: a collection that happens to contain an element named <c>item</c> would start
/// shadowing its own <c>item()</c> method, and every ordinary member of a style declaration would
/// become interceptable by a CSS property of the same name. There is no test that fails at the moment
/// the order is inverted; there is only a page, later, whose search box stops working.
/// </para>
/// <para>
/// <b>A write is the exception, and deliberately so.</b> <see cref="IJsExotic.TrySetNamed"/> is
/// consulted before the ordinary assignment because a legacy platform object with a named setter —
/// <c>Storage</c>, <c>CSSStyleDeclaration</c> — has to see the value before it becomes an ordinary
/// property, or the property it did not intercept shadows the item it was supposed to store. The
/// handler declines by answering <see langword="false"/>, and the ordinary assignment then happens.
/// </para>
/// <para>
/// <b>A deletion takes the same order as a write and costs this provider nothing.</b> The engine
/// dispatches <c>delete obj.name</c> to <c>Delete(in KeyString)</c>, a virtual beside the four this
/// class already overrides, so a handler that implements <see cref="IJsExoticDelete"/> is served by
/// one more override rather than by a different kind of object. The other provider has no such hook
/// and reaches a deletion another way, which is why the declaration is a separate interface.
/// </para>
/// </remarks>
internal sealed class BroilerJsExoticObject : JSObject
{
    private readonly IJsExotic _handler;
    private readonly IJsExoticDelete? _deleter;
    private readonly HashSet<uint> _materialized = [];
    private uint _indexedLength;

    internal BroilerJsExoticObject(BroilerJsRealm realm, IJsExotic handler)
    {
        Realm = realm;
        _handler = handler;

        // Resolved once rather than tested per deletion: the answer cannot change for the life of
        // the object, and null here is what makes the override below free for the five handlers
        // that never delete.
        _deleter = handler as IJsExoticDelete;
    }

    /// <summary>
    /// The realm this object belongs to.
    /// </summary>
    /// <remarks>
    /// Carried so that a value crossing this object's boundary can be attributed to a realm without a
    /// thread-static being consulted — the same reason <c>JsCall</c> carries one, and the reason the
    /// engine's lookup overrides below never need to ask which realm they are in.
    /// </remarks>
    internal BroilerJsRealm Realm { get; }

    /// <summary>Refreshes handler-owned slots for the current dense range.</summary>
    /// <remarks>
    /// Indexed storage serves the engine's presence and enumeration paths. Ownership is tracked
    /// separately so growth cannot overwrite ordinary properties and shrinkage removes only
    /// handler slots. Explicit definitions and successful writes relinquish handler ownership.
    /// </remarks>
    private uint Sync()
    {
        // A handler can re-enter the object while answering a lookup. The outer refresh owns
        // synchronization; an inner read sees the slots already established rather than recursing.
        if (_syncing)
            return _indexedLength;

        _syncing = true;
        try
        {
            var length = _handler.IndexedLength;

            for (uint i = 0; i < length; i++)
            {
                if (!_handler.TryGetIndex(i, out var element))
                    throw new InvalidOperationException($"IJsExotic must supply index {i} below IndexedLength ({length}).");

                // Only refresh slots owned by the handler. Ordinary indexed properties, including
                // undefined and accessors, retain precedence when the collection grows over them.
                if (_materialized.Contains(i) || !GetElements(false).HasKey(i))
                {
                    FastAddValue(i, BroilerJsMarshal.Unwrap(element), JSPropertyAttributes.EnumerableConfigurableReadonlyValue);
                    _materialized.Add(i);
                }
            }

            foreach (var index in _materialized.Where(index => index >= length).ToArray())
            {
                base.Delete(index);
                _materialized.Remove(index);
            }

            _indexedLength = length;
            return length;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Whether a <see cref="Sync"/> is already in progress on this object; see its remarks.</summary>
    private bool _syncing;

    /// <inheritdoc />
    protected override JSValue GetValue(KeyString key, JSValue receiver, bool throwError = true)
    {
        Sync();

        // Presence is independent of the value: an ordinary property may be undefined, or
        // a getter may return undefined (and even delete itself). Check before reading it,
        // then perform exactly one read with the original receiver and error behavior.
        if (base.HasProperty(key.ToJSValue()).BooleanValue)
            return base.GetValue(key, receiver, throwError);

        if (_handler.TryGetNamed(key.ToString(), out var named))
            return BroilerJsMarshal.Unwrap(named);

        // Keep the caller's error behavior for a name neither ordinary storage nor the handler has.
        return base.GetValue(key, receiver, throwError);
    }

    /// <inheritdoc />
    public override JSValue GetValue(uint key, JSValue receiver, bool throwError = true)
    {
        Sync();
        // Sync has installed every supplied index. An undefined ordinary value is still present.
        return base.GetValue(key, receiver, throwError);
    }

    /// <inheritdoc />
    public override JSValue DefineProperty(uint key, JSObject descriptor)
    {
        var result = base.DefineProperty(key, descriptor);
        if (!result.IsBoolean || result.BooleanValue)
            _materialized.Remove(key);
        return result;
    }

    /// <inheritdoc />
    public override bool SetValue(uint key, JSValue value, JSValue receiver, bool throwError = true)
    {
        var written = base.SetValue(key, value, receiver, throwError);
        if (written && ReferenceEquals(receiver, this))
            _materialized.Remove(key);
        return written;
    }

    /// <inheritdoc />
    public override JSValue Delete(uint key)
    {
        var result = base.Delete(key);
        if (result.BooleanValue)
            _materialized.Remove(key);
        return result;
    }

    /// <inheritdoc />
    protected override bool SetValue(KeyString key, JSValue value, JSValue receiver, bool throwError = true)
    {
        if (_handler.TrySetNamed(key.ToString(), BroilerJsMarshal.Wrap(value)))
            return true;

        return base.SetValue(key, value, receiver, throwError);
    }

    /// <summary>
    /// Offers a named deletion to the handler, then deletes the ordinary property as it would
    /// anyway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The handler is asked first and the base deletes regardless</b>, which is the order
    /// <see cref="IJsExoticDelete.TryDeleteNamed"/> specifies: the item goes before the property
    /// mirroring it, and what <c>delete</c> evaluates to stays the engine's answer rather than the
    /// handler's. A handler that declines has cost one virtual call on an object that had none.
    /// </para>
    /// <para>
    /// Indexed deletion updates slot ownership but never calls the named deletion hook. A handler
    /// entry still inside the dense range is supplied again on the next lookup; deleting an ordinary
    /// indexed override exposes that handler entry.
    /// </para>
    /// </remarks>
    public override JSValue Delete(in KeyString key)
    {
        // Host DeleteProperty supplies a KeyString even for a numeric name. Use the indexed
        // storage path, just as a guest numeric deletion does, without invoking the named hook.
        var metadata = key.Metadata;
        if (metadata.IsArrayIndex)
            return Delete(metadata.ArrayIndex);

        _deleter?.TryDeleteNamed(key.ToString());

        return base.Delete(in key);
    }

    /// <inheritdoc />
    public override JSValue HasProperty(JSValue propertyKey)
    {
        Sync();

        if (base.HasProperty(propertyKey) is JSBoolean { BooleanValue: true } present)
            return present;

        return _handler.TryGetNamed(propertyKey.ToString(), out _)
            ? JSBoolean.True
            : JSBoolean.False;
    }

    /// <summary>
    /// The object's keys: its ordinary ones, then the names the handler supplies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The supported names are appended to the enumeration rather than installed as properties.</b>
    /// Installing them would be the shorter code and would break the lookup order this class exists to
    /// preserve: an installed name is an ordinary property, so the next read would find the stale
    /// value in property storage and never reach the handler — a live object that stopped being live
    /// the first time anything enumerated it. Indices are materialised because the engine's presence
    /// and enumeration hooks give no alternative (see <see cref="Sync"/>); names have an alternative,
    /// so they take it.
    /// </para>
    /// <para>
    /// The handler is asked not to repeat ordinary properties, which
    /// <see cref="IJsExotic.SupportedNames"/> states, so no de-duplication happens here — doing it
    /// would hide a handler that violated the contract instead of letting the duplicate be seen.
    /// </para>
    /// </remarks>
    public override IElementEnumerator GetAllKeys(bool showEnumerableOnly = true, bool inherited = true)
    {
        Sync();
        return new SupportedNameEnumerator(base.GetAllKeys(showEnumerableOnly, inherited), _handler.SupportedNames);
    }

    /// <summary>
    /// The descriptor for a key, including one the handler supplies and property storage does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Appending to <see cref="GetAllKeys"/> is necessary and is not sufficient, which the JSEAL
    /// conformance suite caught.</b> <c>for…in</c> and <c>Object.getOwnPropertyNames</c> read the
    /// enumeration and were already right. <c>Object.keys</c> does not: it implements
    /// EnumerableOwnProperties, which snapshots the own keys and then asks <c>[[GetOwnProperty]]</c>
    /// for each one, keeping only the enumerable ones
    /// (<c>JSObjectStatic.Introspection.cs:219-232</c>). A supported name had no own descriptor, so it
    /// was snapshotted and then dropped. <c>Object.assign</c> filters the same way, so an object
    /// spread lost them too — meaning <c>Object.keys(form.elements)</c> and
    /// <c>{...form.elements}</c> saw the indices and the interface's own members but none of the
    /// named controls.
    /// </para>
    /// <para>
    /// The descriptor is synthesised on each ask rather than installed, for the same reason the names
    /// are not installed: an installed property is a stale answer the next read would find before it
    /// reached the handler. Enumerable and configurable, and writable exactly when the handler accepts
    /// a write to that name — which is what WebIDL says a named property is.
    /// </para>
    /// </remarks>
    public override JSValue GetOwnPropertyDescriptor(JSValue name)
    {
        Sync();

        var ordinary = base.GetOwnPropertyDescriptor(name);
        if (ordinary is JSObject)
            return ordinary;

        if (!_handler.TryGetNamed(name.ToString(), out var supplied))
            return ordinary;

        var descriptor = new JSObject();
        descriptor.FastAddValue(KeyStrings.value, BroilerJsMarshal.Unwrap(supplied), JSPropertyAttributes.EnumerableConfigurableValue);
        descriptor.FastAddValue(KeyStrings.writable, JSBoolean.True, JSPropertyAttributes.EnumerableConfigurableValue);
        descriptor.FastAddValue(KeyStrings.enumerable, JSBoolean.True, JSPropertyAttributes.EnumerableConfigurableValue);
        descriptor.FastAddValue(KeyStrings.configurable, JSBoolean.True, JSPropertyAttributes.EnumerableConfigurableValue);
        return descriptor;
    }

    /// <summary>
    /// The object's own keys followed by the handler's supported names.
    /// </summary>
    private sealed class SupportedNameEnumerator(IElementEnumerator inner, IReadOnlyList<string> names) : IElementEnumerator
    {
        private bool _innerDone;
        private int _position = -1;

        private bool TryNext(out JSValue value)
        {
            if (!_innerDone)
            {
                if (inner.MoveNext(out value))
                    return true;

                _innerDone = true;
            }

            if (++_position < names.Count)
            {
                value = new JSString(names[_position]);
                return true;
            }

            value = JSUndefined.Value;
            return false;
        }

        public bool MoveNext(out bool hasValue, out JSValue value, out uint index)
        {
            // Only the inner enumerator can report a real index; a supported name is never one, so it
            // reports zero the way the engine's own enumerator does for a string-keyed property.
            if (!_innerDone && inner.MoveNext(out hasValue, out value, out index))
                return true;

            _innerDone = true;
            index = 0;
            hasValue = TryNext(out value);
            return hasValue;
        }

        public bool MoveNext(out JSValue value) => TryNext(out value);

        public bool MoveNextOrDefault(out JSValue value, JSValue @default)
        {
            if (TryNext(out value))
                return true;

            value = @default;
            return false;
        }

        public JSValue NextOrDefault(JSValue @default) => TryNext(out var value) ? value : @default;
    }
}

