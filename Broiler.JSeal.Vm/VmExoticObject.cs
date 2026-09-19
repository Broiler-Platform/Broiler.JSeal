using Broiler.VM.Profile.JavaScript;

namespace Broiler.JSeal.Vm;

/// <summary>
/// A JSEAL host-completed lookup, in the shape the Broiler.VM realm asks for one.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a translation and deliberately not a policy.</b> The ordering rule that matters -
/// ordinary properties beat named ones, and a handler is asked only about what storage did not hold
/// - lives in the engine, where the property lookup is, and the VM's own exotic object enforces it.
/// Re-implementing it here would put the rule in two places, and the copy that drifts is the one
/// nobody is looking at.
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

    /// <inheritdoc />
    public bool TryGetNamed(JsHostRealm realm, string name, out JsHostValue value)
    {
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
        if (!_handler.TryGetIndex(index, out var answered))
        {
            value = JsHostValue.Missing;
            return false;
        }

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
    public uint IndexedLength(JsHostRealm realm) => _handler.IndexedLength;
}

