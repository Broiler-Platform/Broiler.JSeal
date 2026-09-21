namespace Broiler.JSeal;

/// <summary>
/// Host-defined property lookup, for the six DOM objects whose members are not a fixed list: live
/// collections (indexed and named), a form's controls, <c>CSSStyleDeclaration</c>'s dashed properties,
/// and <c>Storage</c>'s keys.
/// </summary>
/// <remarks>
/// <para>
/// These are the objects the bridge used to express by subclassing <c>JSObject</c> and overriding
/// its property-lookup members â€” the deepest engine coupling in the whole binding layer, because it
/// depended not only on the engine's types but on its lookup <em>protocol</em>. Six classes did it;
/// handler classes in the bridge implement this instead, and the one subclass left is the provider's.
/// Declaring the hook rather than inheriting it is what lets an engine that dispatches lookups
/// differently â€” through a Proxy, through a C callback table â€” serve the same DOM object.
/// </para>
/// <para>
/// <b>Ordinary properties win, and the order is not negotiable.</b> The engine consults its own
/// property storage first and only asks a handler when it finds nothing. This is what WebIDL's
/// named-property semantics require and what <c>BroilerJsExoticObject</c> does â€” it asks the base
/// lookup first â€” and getting it backwards is silently wrong rather than loudly wrong: a collection
/// that happens to contain an element named <c>item</c> would start shadowing its own
/// <c>item()</c> method, and every ordinary member of a style declaration would be interceptable by a
/// CSS property of the same name.
/// </para>
/// <para>
/// A handler that also implements <see cref="IJsExoticDelete"/> completes deletions as well as
/// lookups. That is a separate contract because a provider has to know at mint time whether an
/// object ever deletes, and this one does not answer that question.
/// </para>
/// </remarks>
public interface IJsExotic
{
    /// <summary>
    /// Answers a named lookup the object's ordinary properties did not, or reports that there is no
    /// such property.
    /// </summary>
    /// <remarks>
    /// An existing own or inherited ordinary property takes precedence even when its value is
    /// <c>undefined</c>. An ordinary getter runs once per read with the original receiver; returning
    /// <c>undefined</c> does not cause a fallback to this hook.
    /// </remarks>
    bool TryGetNamed(string name, out JsValue value);

    /// <summary>
    /// Answers an integer-indexed lookup the object's ordinary properties did not.
    /// </summary>
    /// <remarks>
    /// Every index below <see cref="IndexedLength"/> must return true, including entries whose
    /// value is <c>undefined</c>. Holes within that dense range are invalid; providers report an
    /// <see cref="InvalidOperationException"/> when validation encounters one. Indices at or above
    /// the bound are absent from the handler. Ordinary own indexed properties retain precedence.
    /// Handler entries are enumerable, configurable and read-only; defining an ordinary indexed
    /// property explicitly overrides an entry without making that property part of the collection.
    /// </remarks>
    bool TryGetIndex(uint index, out JsValue value);

    /// <summary>
    /// Handles an assignment to a named property, or declines it so that the ordinary assignment
    /// happens.
    /// </summary>
    bool TrySetNamed(string name, JsValue value);

    /// <summary>
    /// The names this object supplies beyond its ordinary properties, for <c>Object.keys</c>,
    /// <c>forâ€¦in</c> and spread. Ordinary properties are added by the engine and must not be repeated
    /// here.
    /// </summary>
    IReadOnlyList<string> SupportedNames { get; }

    /// <summary>The exclusive upper bound of the handler's dense indexed range.</summary>
    /// <remarks>
    /// All indices from zero through length minus one must be supplied by TryGetIndex. To withdraw
    /// entries, shrink this bound; replacement and later growth are supported. The range must stay
    /// consistent during one operation, and may change between operations. Providers may validate
    /// entries while reading or enumerating. Shrinkage removes only handler-owned entries, never
    /// ordinary indexed properties installed by the host or guest.
    /// </remarks>
    uint IndexedLength { get; }
}

/// <summary>
/// The deletion half of a host-completed lookup, for the one kind of object whose behaviour
/// includes taking something away: a legacy platform object with a named deleter.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>Storage</c> is the only one of the six lookup-completing objects that needs this, and it is
/// the only one that could not move onto <see cref="IJsExotic"/> without it.</b> The other five
/// answer reads and take writes. A storage area also has <c>delete localStorage.foo</c> take the
/// item out of the area, so <c>getItem</c> stops answering for it and <c>length</c> and
/// <c>key(n)</c> stop counting it. Converting it with no delete hook would leave the ordinary
/// property deleted and the item still in the store, which is a wrong answer rather than a missing
/// feature.
/// </para>
/// <para>
/// <b>It is a second contract rather than a sixth member of <see cref="IJsExotic"/>, and the reason
/// is what a provider has to DO about it.</b> One engine dispatches a deletion through a virtual its
/// exotic object already overrides the neighbours of, and pays nothing. Another has no delete hook
/// on its host-object surface at all and has to express a deleting object differently, behind the
/// realm's own <c>Proxy</c>, where every operation costs a lookup on a trap object before it
/// forwards. A provider can pay that for the objects that need it and not for the rest only if it
/// can tell which those are at the moment it mints one. Implementing this interface IS that
/// declaration, and a handler that does not implement it is minted exactly as it is today. A
/// boolean property on <see cref="IJsExotic"/> would say the same thing and could contradict the
/// behaviour; a type is a declaration the compiler keeps honest.
/// </para>
/// <para>
/// <b>The hook runs BEFORE the ordinary deletion, which is the order
/// <see cref="IJsExotic.TrySetNamed"/> takes and the opposite of the order the reads take.</b> A
/// named deleter has to take the item out before the property mirroring it goes; a read may be
/// answered after ordinary storage has declined, because nothing has changed by then.
/// </para>
/// </remarks>
public interface IJsExoticDelete
{
    /// <summary>
    /// Takes the deletion of a named property, removing whatever the name stands for, or declines
    /// it so that only the ordinary deletion happens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Declining is a real answer and the common one.</b> A name the object does not own is a
    /// page deleting an expando it put there itself, and a hook that claimed everything would make
    /// that deletion look like the object's business.
    /// </para>
    /// <para>
    /// <b>The ordinary deletion runs either way, and this answer is not the deletion's answer.</b> A
    /// claimed deletion still has to take an own property of that name with it, because a mirror
    /// that outlived its item would answer for something the object no longer has. So the engine
    /// deletes as well, and what <c>delete</c> evaluates to is the engine's answer.
    /// </para>
    /// <para>
    /// <b>An integer-index key never arrives here, and neither does a symbol.</b> Indexed and named
    /// lookup are separate questions in this contract, and a provider offers this hook only the keys
    /// its engine treats as names. Nothing in the bridge deletes by index; an object that needed it
    /// would need an indexed hook of its own, which is the same gap that keeps a digit-only storage
    /// key from reaching its area at all.
    /// </para>
    /// </remarks>
    bool TryDeleteNamed(string name);
}


