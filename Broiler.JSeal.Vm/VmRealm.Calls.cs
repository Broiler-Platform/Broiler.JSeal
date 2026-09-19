using Broiler.VM.Profile.JavaScript;

namespace Broiler.JSeal.Vm;

/// <summary>
/// <see cref="IJsCalls"/>: calling into JavaScript, and raising a JavaScript error from host code.
/// </summary>
internal sealed partial class VmRealm
{
    /// <inheritdoc />
    public JsValue Invoke(JsValue function, JsValue thisValue, ReadOnlySpan<JsValue> arguments = default)
    {
        var callee = VmMarshal.Unwrap(function);
        var receiver = VmMarshal.Unwrap(thisValue);
        var converted = VmMarshal.UnwrapAll(arguments);

        return InStep(realm => VmMarshal.Wrap(realm.Invoke(callee, receiver, converted)));
    }

    /// <inheritdoc />
    public JsValue Construct(JsValue constructor, ReadOnlySpan<JsValue> arguments = default)
    {
        var callee = VmMarshal.Unwrap(constructor);
        var converted = VmMarshal.UnwrapAll(arguments);

        return InStep(realm => VmMarshal.Wrap(realm.Construct(callee, converted)));
    }

    /// <inheritdoc />
    /// <remarks>
    /// It answers rather than throws, so a body reads <c>throw realm.Error(...)</c> and the compiler
    /// knows the path ends. The value inside it is built by the engine's own error constructor, so
    /// <c>e instanceof TypeError</c> holds for the page rather than only the name matching.
    /// </remarks>
    public Exception Error(JsErrorKind kind, string message)
    {
        var vmKind = kind switch
        {
            JsErrorKind.TypeError => JsHostErrorKind.TypeError,
            JsErrorKind.RangeError => JsHostErrorKind.RangeError,
            JsErrorKind.SyntaxError => JsHostErrorKind.SyntaxError,
            JsErrorKind.ReferenceError => JsHostErrorKind.ReferenceError,
            _ => JsHostErrorKind.Error,
        };

        return InStep<Exception>(realm =>
        {
            var raised = realm.Error(vmKind, message ?? string.Empty);
            return new JsEngineException(raised.Message, VmMarshal.Wrap(raised.Thrown), raised);
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>It constructs through the realm's own <c>DOMException</c>, resolved when the error is
    /// raised rather than when the realm was built.</b> That global is the bridge's to install, and
    /// a provider that captured it at construction would capture whatever was there before the
    /// bridge ran - which is nothing.
    /// </para>
    /// <para>
    /// <b>A realm with no <c>DOMException</c> gets a plain error carrying the name.</b> That is a
    /// degradation and it is the honest one: the alternatives are throwing a second exception while
    /// raising the first, which loses what the caller was trying to say, or silently answering a
    /// <c>TypeError</c>, which a page would branch on wrongly.
    /// </para>
    /// </remarks>
    public Exception DomError(string name, string message)
    {
        var wanted = name ?? "Error";
        var detail = message ?? string.Empty;

        return InStep<Exception>(realm =>
        {
            var constructor = realm.GetProperty(realm.Global, "DOMException");

            if (constructor.Kind is not JsHostValueKind.Function)
            {
                var fallback = realm.Error(JsHostErrorKind.Error, wanted + ": " + detail);
                return new JsEngineException(fallback.Message, VmMarshal.Wrap(fallback.Thrown), fallback);
            }

            JsHostValue[] arguments = [JsHostValue.String(detail), JsHostValue.String(wanted)];
            var raised = realm.Construct(constructor, arguments);

            return new JsEngineException(wanted + ": " + detail, VmMarshal.Wrap(raised));
        });
    }
}

