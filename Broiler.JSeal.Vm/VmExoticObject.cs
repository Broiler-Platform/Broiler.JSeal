using Broiler.VM.Profile.JavaScript;

namespace Broiler.JSeal.Vm;

/// <summary>
/// A JSEAL host-completed lookup, in the shape the Broiler.VM realm asks for one.
/// </summary>
/// <remarks>
/// <para>
/// The VM checks ordinary own storage before asking this adapter. JSEAL also gives inherited
/// properties precedence over named handler values, so the adapter declines names present on the
/// prototype chain. The engine then reads that property with the original receiver.
/// </para>
/// <para>
/// <b>The two contracts differ in one respect and it is worth naming.</b> JSEAL's
/// <c>SupportedNames</c> and <c>IndexedLength</c> are properties; the VM's take the realm, because
/// a live collection may need to ask the realm something to answer them. Nothing is lost in the
/// direction that matters - a property can always answer a method - and the realm argument is
/// dropped here rather than being invented on the JSEAL side.
/// </para>
/// </remarks>
internal sealed class VmExoticObject : IJsHostExotic
{
    private readonly IJsExotic _handler;

    internal VmExoticObject(IJsExotic handler) => _handler = handler;

    // Attached immediately after creation, before the object is exposed to callers.
    internal JsHostValue Target { get; set; }

    /// <inheritdoc />
    public bool TryGetNamed(JsHostRealm realm, string name, out JsHostValue value)
    {
        var prototype = realm.GetPrototype(Target);
        if (prototype.IsObject && realm.HasProperty(prototype, name))
        {
            value = JsHostValue.Missing;
            return false;
        }

        if (!_handler.TryGetNamed(name, out var answered))
        {
            value = JsHostValue.Missing;
            return false;
        }

        value = VmMarshal.Unwrap(answered);
        return true;
    }

    /// <inheritdoc />
    public bool TryGetIndex(JsHostRealm realm, uint index, out JsHostValue value)
    {
        var length = _handler.IndexedLength;
        if (index >= length)
        {
            value = JsHostValue.Missing;
            return false;
        }
        if (!_handler.TryGetIndex(index, out var answered))
            throw new InvalidOperationException($"IJsExotic must supply index {index} below IndexedLength ({length}).");

        value = VmMarshal.Unwrap(answered);
        return true;
    }

    /// <inheritdoc />
    public bool TrySetNamed(JsHostRealm realm, string name, JsHostValue value) =>
        _handler.TrySetNamed(name, VmMarshal.Wrap(value));

    /// <inheritdoc />
    public System.Collections.Generic.IReadOnlyList<string> SupportedNames(JsHostRealm realm) =>
        _handler.SupportedNames;

    /// <inheritdoc />
    public uint IndexedLength(JsHostRealm realm)
    {
        var length = _handler.IndexedLength;
        for (uint index = 0; index < length; index++)
        {
            if (!_handler.TryGetIndex(index, out _))
                throw new InvalidOperationException($"IJsExotic must supply index {index} below IndexedLength ({length}).");
        }
        return length;
    }
}

