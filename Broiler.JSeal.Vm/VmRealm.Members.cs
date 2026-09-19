using Broiler.VM.Profile.JavaScript;

namespace Broiler.JSeal.Vm;

/// <summary>
/// <see cref="IJsMembers"/>: installing, reading and removing members, and the prototype link.
/// </summary>
/// <remarks>
/// <b>The flags are a permutation and nothing else.</b> Both sides carry the same three questions -
/// is it walked by an enumeration, may it be redefined, may it be assigned to - in a different bit
/// order, so this maps them one for one rather than approximating. Which of value or accessor a
/// member is stays out of the flags on both sides, because it is decided by which method installs
/// it.
/// </remarks>
internal sealed partial class VmRealm
{
    /// <inheritdoc />
    public void DefineValue(
        JsValue target, string name, JsValue value, JsPropertyFlags flags = JsPropertyFlags.Default)
    {
        var host = VmMarshal.Unwrap(target);
        var converted = VmMarshal.Unwrap(value);

        InStep(realm => realm.DefineValue(host, name, converted, Attributes(flags)));
    }

    /// <inheritdoc />
    public void DefineAccessor(
        JsValue target,
        string name,
        JsNativeFunction getter,
        JsNativeFunction? setter,
        JsPropertyFlags flags = JsPropertyFlags.Default)
    {
        ArgumentNullException.ThrowIfNull(getter);

        var host = VmMarshal.Unwrap(target);

        InStep(realm => realm.DefineAccessor(
            host,
            name,
            Trampoline(getter),
            setter is null ? null : Trampoline(setter),
            Attributes(flags)));
    }

    /// <inheritdoc />
    public void DefineIndex(
        JsValue target, uint index, JsValue value, JsPropertyFlags flags = JsPropertyFlags.Default)
    {
        var host = VmMarshal.Unwrap(target);
        var converted = VmMarshal.Unwrap(value);

        InStep(realm => realm.DefineIndex(host, index, converted, Attributes(flags)));
    }

    /// <inheritdoc />
    public JsValue GetProperty(JsValue target, string name)
    {
        var host = VmMarshal.Unwrap(target);

        return InStep(realm => VmMarshal.Wrap(realm.GetProperty(host, name)));
    }

    /// <inheritdoc />
    public JsValue GetIndex(JsValue target, uint index)
    {
        var host = VmMarshal.Unwrap(target);

        return InStep(realm => VmMarshal.Wrap(realm.GetIndex(host, index)));
    }

    /// <inheritdoc />
    public void SetProperty(JsValue target, string name, JsValue value)
    {
        var host = VmMarshal.Unwrap(target);
        var converted = VmMarshal.Unwrap(value);

        InStep(realm => realm.SetProperty(host, name, converted));
    }

    /// <inheritdoc />
    public bool HasProperty(JsValue target, string name)
    {
        var host = VmMarshal.Unwrap(target);

        return InStep(realm => realm.HasProperty(host, name));
    }

    /// <inheritdoc />
    public bool DeleteProperty(JsValue target, string name)
    {
        var host = VmMarshal.Unwrap(target);

        return InStep(realm => realm.DeleteProperty(host, name));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> OwnPropertyNames(JsValue target)
    {
        var host = VmMarshal.Unwrap(target);

        return InStep(realm => realm.OwnPropertyNames(host));
    }

    /// <inheritdoc />
    public void SetPrototype(JsValue target, JsValue prototype)
    {
        var host = VmMarshal.Unwrap(target);
        var link = VmMarshal.Unwrap(prototype);

        InStep(realm => realm.SetPrototype(host, link));
    }

    /// <inheritdoc />
    public JsValue GetPrototype(JsValue target)
    {
        var host = VmMarshal.Unwrap(target);

        return InStep(realm => VmMarshal.Wrap(realm.GetPrototype(host)));
    }

    /// <summary>JSEAL's attribute bits as the VM realm spells them.</summary>
    private static JsHostPropertyFlags Attributes(JsPropertyFlags flags)
    {
        var mapped = JsHostPropertyFlags.None;

        if ((flags & JsPropertyFlags.Enumerable) != 0)
            mapped |= JsHostPropertyFlags.Enumerable;

        if ((flags & JsPropertyFlags.Configurable) != 0)
            mapped |= JsHostPropertyFlags.Configurable;

        if ((flags & JsPropertyFlags.Writable) != 0)
            mapped |= JsHostPropertyFlags.Writable;

        return mapped;
    }
}

