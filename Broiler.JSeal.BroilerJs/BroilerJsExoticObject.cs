using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.JSeal.BroilerJs;

/// <summary>
/// A <c>JSObject</c> whose property lookup an <see cref="IJsExotic"/> completes â€” the one object in
/// this provider that has to know the engine's lookup <em>protocol</em> rather than only its types.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class is the whole of what six bridge classes used to be.</b> <c>DomCollectionBinding</c>'s
/// collection, <c>FormBinding</c>'s controls collection and form object, <c>StyleDeclarationBinding</c>'s
/// two declarations and <c>WebStorageBinding</c>'s storage area each subclassed <c>JSObject</c> and
/// overrode the same members, which made them the deepest engine coupling in the binding layer. The
/// members overridden here are exactly the ones they overrode, because those are the ones the engine
/// dispatches lookup through: <c>GetValue(KeyString, â€¦)</c> for a named read,
/// <c>GetValue(uint, â€¦)</c> for an indexed one, <c>SetValue(KeyString, â€¦)</c> for a named write,
/// <c>HasProperty</c> for <c>in</c>, and <c>GetAllKeys</c> for enumeration.
/// </para>
/// <para>
/// <b>ORDERING IS LOAD-BEARING: the base lookup runs FIRST, and the handler answers only what it did
/// not.</b> Every one of the six classes did this â€” the class remarks of <c>DomCollectionBinding</c> and
/// <c>StyleDeclarationBinding</c> state it for their replacement handlers â€” and it is what
/// WebIDL's named-property semantics require. Getting it backwards is silently wrong rather than
/// loudly wrong: a collection that happens to contain an element named <c>item</c> would start
/// shadowing its own <c>item()</c> method, and every ordinary member of a style declaration would
/// become interceptable by a CSS property of the same name. There is no test that fails at the moment
/// the order is inverted; there is only a page, later, whose search box stops working.
/// </para>
/// <para>
/// <b>A write is the exception, and deliberately so.</b> <see cref="IJsExotic.TrySetNamed"/> is
/// consulted before the ordinary assignment because a legacy platform object with a named setter â€”
/// <c>Storage</c>, <c>CSSStyleDeclaration</c> â€” has to see the value before it becomes an ordinary
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
    private uint _materialized;

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
    /// thread-static being consulted â€” the same reason <c>JsCall</c> carries one, and the reason the
    /// engine's lookup overrides below never need to ask which realm they are in.
    /// </remarks>
    internal BroilerJsRealm Realm { get; }

    /// <summary>
    /// Brings the object's own indexed properties up to date with the handler, and returns how many
    /// there now are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The indices are made real rather than intercepted, and that is a fact about this engine's
    /// property storage rather than about the DOM.</b> Answering indexed reads purely by overriding
    /// the read hook works for everything written against <c>this[i]</c> and fails for everything
    /// written against the object: <c>Array.prototype.map.call(list, â€¦)</c> reads <c>length</c>
    /// correctly and then produces a hole per element, because an array generic asks whether index
    /// <c>i</c> is <em>present</em> before reading it and an object with no own indexed properties
    /// answers no. <c>Object.keys</c>, <c>forâ€¦in</c> and spread ask the same way. Presence,
    /// enumeration and retrieval are separate entry points with no single hook between them, so the
    /// indices are materialised instead and every generic algorithm then works without knowing what
    /// the object is.
    /// </para>
    /// <para>
    /// Called from each read entry point rather than on mutation, because a live collection has no
    /// mutation of its own to hook â€” what it reflects is the tree, and the read is the only moment it
    /// is known to matter. Shrinking matters as much as growing: an index whose element went away has
    /// to stop being offered, not keep a stale handle at it.
    /// </para>
    /// <para>
    /// This is what <see cref="IJsExotic.IndexedLength"/> exists for, and its documentation says so:
    /// the length is asked for immediately before it is used, so a live collection reports what it
    /// holds now rather than what it held when it was minted.
    /// </para>
    /// </remarks>
    private uint Sync()
    {
        // RE-ENTRANCY GUARD, and it is not defensive programming â€” it closes a real stack overflow.
        // Materialising an index is `this[i] = â€¦`, which the engine routes through SetIndexOnReceiver,
        // which asks this object for the index's own descriptor. GetOwnPropertyDescriptor syncs
        // first, as every read entry point must, so the write re-enters the sync that issued it and
        // the recursion is unbounded. It aborted the whole test run rather than failing a test.
        //
        // A flag rather than dropping the sync from the descriptor path, because the descriptor path
        // genuinely needs it when it is entered from outside â€” Object.keys asks for a descriptor per
        // key â€” and a guard here protects every entry point, including ones added later, instead of
        // making each one remember. Re-entering returns the length the outer call is establishing:
        // the writes it has already done are visible, which is all the inner ask needs.
        if (_syncing)
            return _materialized;

        _syncing = true;
        try
        {
            var length = _handler.IndexedLength;

            for (uint i = 0; i < length; i++)
            {
                if (_handler.TryGetIndex(i, out var element))
                    this[i] = BroilerJsMarshal.Unwrap(element);
            }

            for (var i = length; i < _materialized; i++)
                GetElements().RemoveAt(i);

            _materialized = length;
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

        // Ordinary properties and the prototype chain FIRST. See the remarks on this class.
        var resolved = base.GetValue(key, receiver, false);
        if (resolved is not null && !resolved.IsUndefined)
            return resolved;

        if (_handler.TryGetNamed(key.ToString(), out var named))
            return BroilerJsMarshal.Unwrap(named);

        // Re-entering the base with the caller's throwError so that a miss fails the way an ordinary
        // miss on this object would, rather than being flattened to undefined here.
        return base.GetValue(key, receiver, throwError);
    }

    /// <inheritdoc />
    public override JSValue GetValue(uint key, JSValue receiver, bool throwError = true)
    {
        Sync();

        var resolved = base.GetValue(key, receiver, false);
        if (resolved is not null && !resolved.IsUndefined)
            return resolved;

        if (_handler.TryGetIndex(key, out var indexed))
            return BroilerJsMarshal.Unwrap(indexed);

        return base.GetValue(key, receiver, throwError);
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
    /// <b><c>Delete(uint)</c> is deliberately NOT overridden.</b> The engine routes a digit-only key
    /// there instead of here, and the other provider's engine routes it away from the named hook
    /// too. Answering it on one side alone would make <c>delete storage[7]</c> remove an item under
    /// one engine and not the other, which is worse than the gap both share.
    /// </para>
    /// </remarks>
    public override JSValue Delete(in KeyString key)
    {
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
    /// value in property storage and never reach the handler â€” a live object that stopped being live
    /// the first time anything enumerated it. Indices are materialised because the engine's presence
    /// and enumeration hooks give no alternative (see <see cref="Sync"/>); names have an alternative,
    /// so they take it.
    /// </para>
    /// <para>
    /// The handler is asked not to repeat ordinary properties, which
    /// <see cref="IJsExotic.SupportedNames"/> states, so no de-duplication happens here â€” doing it
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
    /// conformance suite caught.</b> <c>forâ€¦in</c> and <c>Object.getOwnPropertyNames</c> read the
    /// enumeration and were already right. <c>Object.keys</c> does not: it implements
    /// EnumerableOwnProperties, which snapshots the own keys and then asks <c>[[GetOwnProperty]]</c>
    /// for each one, keeping only the enumerable ones
    /// (<c>JSObjectStatic.Introspection.cs:219-232</c>). A supported name had no own descriptor, so it
    /// was snapshotted and then dropped. <c>Object.assign</c> filters the same way, so an object
    /// spread lost them too â€” meaning <c>Object.keys(form.elements)</c> and
    /// <c>{...form.elements}</c> saw the indices and the interface's own members but none of the
    /// named controls.
    /// </para>
    /// <para>
    /// The descriptor is synthesised on each ask rather than installed, for the same reason the names
    /// are not installed: an installed property is a stale answer the next read would find before it
    /// reached the handler. Enumerable and configurable, and writable exactly when the handler accepts
    /// a write to that name â€” which is what WebIDL says a named property is.
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

