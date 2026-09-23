using Broiler.JavaScript.Ast.Misc;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Engine.Core;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.JSeal.BroilerJs;

/// <summary>
/// <see cref="IJsMembers"/> operations, including guest accessors and Proxy traps.
/// </summary>
internal partial class BroilerJsRealm
{
    /// <inheritdoc />
    public void DefineValue(JsValue target, string name, JsValue value, JsPropertyFlags flags = JsPropertyFlags.Default) =>
        Execute((target, name, value, flags), static s =>
            DefineMember(s.target, new JSString(s.name), DataDescriptor(s.value, s.flags), nameof(DefineValue)));

    /// <inheritdoc />
    public void DefineAccessor(JsValue target, string name, JsNativeFunction getter, JsNativeFunction? setter, JsPropertyFlags flags = JsPropertyFlags.Default)
    {
        ArgumentNullException.ThrowIfNull(getter);
        Execute((realm: this, target, name, getter, setter, flags), static s =>
        {
            // Accessors are non-constructable. An absent setter is undefined in a descriptor;
            // the Writable flag applies only to data properties.
            var descriptor = MemberDescriptor(s.flags);
            descriptor.FastAddValue(KeyStrings.get, new JSFunction(
                s.realm.MethodTrampoline(s.getter), $"get {s.name}", StringSpan.Empty, 0, createPrototype: false),
                JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue setterFunction = s.setter is null ? JSUndefined.Value : new JSFunction(
                s.realm.MethodTrampoline(s.setter), $"set {s.name}", StringSpan.Empty, 1, createPrototype: false);
            descriptor.FastAddValue(KeyStrings.set, setterFunction, JSPropertyAttributes.EnumerableConfigurableValue);
            DefineMember(s.target, new JSString(s.name), descriptor, nameof(DefineAccessor));
        });
    }

    /// <inheritdoc />
    public void DefineIndex(JsValue target, uint index, JsValue value, JsPropertyFlags flags = JsPropertyFlags.Default) =>
        Execute((target, index, value, flags), static s =>
            DefineMember(s.target, new JSNumber(s.index), DataDescriptor(s.value, s.flags), nameof(DefineIndex)));

    // A null prototype keeps guest Object.prototype properties out of these host descriptors.
    // FastAddValue is safe here: this is a new ordinary object owned by the provider.
    private static JSObject MemberDescriptor(JsPropertyFlags flags)
    {
        var descriptor = new JSObject { BasePrototypeObject = null! };
        descriptor.FastAddValue(KeyStrings.enumerable,
            flags.HasFlag(JsPropertyFlags.Enumerable) ? JSBoolean.True : JSBoolean.False,
            JSPropertyAttributes.EnumerableConfigurableValue);
        descriptor.FastAddValue(KeyStrings.configurable,
            flags.HasFlag(JsPropertyFlags.Configurable) ? JSBoolean.True : JSBoolean.False,
            JSPropertyAttributes.EnumerableConfigurableValue);
        return descriptor;
    }

    private static JSObject DataDescriptor(JsValue value, JsPropertyFlags flags)
    {
        var descriptor = MemberDescriptor(flags);
        descriptor.FastAddValue(KeyStrings.value, BroilerJsMarshal.Unwrap(value), JSPropertyAttributes.EnumerableConfigurableValue);
        descriptor.FastAddValue(KeyStrings.writable,
            flags.HasFlag(JsPropertyFlags.Writable) ? JSBoolean.True : JSBoolean.False,
            JSPropertyAttributes.EnumerableConfigurableValue);
        return descriptor;
    }

    private static void DefineMember(JsValue target, JSValue key, JSObject descriptor, string operation)
    {
        // The virtual engine operation dispatches Proxy traps and handles array length updates.
        // Its boolean false result is a refusal; ordinary success may return undefined.
        var result = BroilerJsMarshal.AsObject(target, operation).DefineProperty(key, descriptor);
        if (result.IsBoolean && !result.BooleanValue)
            throw JSEngine.NewTypeError("Cannot define property");
    }

    /// <inheritdoc />
    public JsValue GetProperty(JsValue target, string name) =>
        Execute((target, name), static s => BroilerJsMarshal.Wrap(BroilerJsMarshal.Unwrap(s.target)[(KeyString)s.name]));

    /// <inheritdoc />
    public JsValue GetIndex(JsValue target, uint index) =>
        Execute((target, index), static s => BroilerJsMarshal.Wrap(BroilerJsMarshal.Unwrap(s.target)[s.index]));

    /// <inheritdoc />
    public void SetProperty(JsValue target, string name, JsValue value) =>
        Execute((target, name, value), static s => { BroilerJsMarshal.Unwrap(s.target)[(KeyString)s.name] = BroilerJsMarshal.Unwrap(s.value); });

    /// <inheritdoc />
    public bool HasProperty(JsValue target, string name) =>
        Execute((target, name), static s => BroilerJsMarshal.Unwrap(s.target).HasProperty(new JSString(s.name)).BooleanValue);

    /// <inheritdoc />
    public bool DeleteProperty(JsValue target, string name) =>
        Execute((target, name), static s => BroilerJsMarshal.Unwrap(s.target).Delete((KeyString)s.name).BooleanValue);

    /// <inheritdoc />
    public IReadOnlyList<string> OwnPropertyNames(JsValue target) =>
        Execute(target, static value =>
        {
            var names = new List<string>();
            var keys = BroilerJsMarshal.Unwrap(value).GetAllKeys(showEnumerableOnly: true, inherited: false);
            // Keep traversal within the boundary too: enumeration can run more guest traps.
            while (keys.MoveNext(out var key))
            {
                if (key is not null && !key.IsSymbol)
                    names.Add(key.ToString());
            }
            return names;
        });

    /// <inheritdoc />
    public void SetPrototype(JsValue target, JsValue prototype) =>
        Execute((target, prototype), static s => BroilerJsMarshal.AsObject(s.target, nameof(SetPrototype))
            .SetPrototypeOf(s.prototype.IsNullish ? JSNull.Value : BroilerJsMarshal.Unwrap(s.prototype)));

    /// <inheritdoc />
    public JsValue GetPrototype(JsValue target) =>
        Execute(target, static value => BroilerJsMarshal.Wrap(BroilerJsMarshal.Unwrap(value).GetPrototypeOf()));
}

