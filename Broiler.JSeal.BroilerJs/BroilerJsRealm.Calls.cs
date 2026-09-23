using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Engine.Core;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.JSeal.BroilerJs;

/// <summary>
/// <see cref="IJsCalls"/>: calling into JavaScript, and raising a JavaScript error from host code.
/// </summary>
internal partial class BroilerJsRealm
{
    /// <inheritdoc />
    /// <remarks>
    /// A value whose handle is not a function is refused before the engine is asked, with the
    /// realm's <c>TypeError</c> carried by a <see cref="JsEngineException"/>.
    /// </remarks>
    public JsValue Invoke(JsValue function, JsValue thisValue, ReadOnlySpan<JsValue> arguments = default)
    {
        using var scope = Enter();

        var target = BroilerJsMarshal.Unwrap(function);
        var call = new Arguments(BroilerJsMarshal.Unwrap(thisValue), UnwrapAll(arguments));

        try
        {
            // The handle's kind is the engine's callability predicate (BroilerJsMarshal.Wrap), and it
            // is checked before the engine sees the call: InvokeFunction runs a Proxy's apply trap
            // whether or not its target is callable, so a noncallable Proxy with one answered the
            // trap's value instead of the TypeError ECMAScript's Call raises.
            if (!function.IsFunction)
                throw JSEngine.NewTypeError("the value passed to Invoke is not a function");

            return BroilerJsMarshal.Wrap(target.InvokeFunction(in call));
        }
        catch (JSException engineException)
        {
            throw Translate(engineException);
        }
    }

    /// <summary>
    /// Calls a constructor with <c>new</c>.
    /// </summary>
    /// <remarks>
    /// The receiver of a construct call is the constructor itself under this engine — that is what
    /// <c>CreateInstance</c> reads to derive <c>new.target</c>, and what the bridge already passes at
    /// its own construct sites (<c>domExCtor.CreateInstance(new Arguments(domExCtor, …))</c>).
    /// </remarks>
    public JsValue Construct(JsValue constructor, ReadOnlySpan<JsValue> arguments = default)
    {
        using var scope = Enter();

        var target = BroilerJsMarshal.Unwrap(constructor);
        var call = new Arguments(target, UnwrapAll(arguments));

        try
        {
            // A noncallable value is never a constructor; refused the way Invoke refuses it.
            if (!constructor.IsFunction)
                throw JSEngine.NewTypeError("the value passed to Construct is not a constructor");

            return BroilerJsMarshal.Wrap(target.CreateInstance(in call));
        }
        catch (JSException engineException)
        {
            throw Translate(engineException);
        }
    }

    /// <summary>
    /// The exception to <see langword="throw"/> so that JavaScript sees an error of
    /// <paramref name="kind"/>.
    /// </summary>
    /// <remarks>
    /// The engine's own factories, not a hand-built object: each one mints the error against the
    /// realm's intrinsic constructor, so <c>e instanceof TypeError</c> holds for the page and the
    /// message carries the host call site the engine's callers already annotate. <c>URIError</c> has
    /// no <see cref="JsErrorKind"/> because nothing in the bridge raises one; a kind that is not in
    /// the enum falls to plain <c>Error</c> rather than throwing, since a host asking for an error is
    /// already on a failure path and a second failure there loses the first.
    /// </remarks>
    public Exception Error(JsErrorKind kind, string message)
    {
        using var scope = Enter();

        return kind switch
        {
            JsErrorKind.TypeError => JSEngine.NewTypeError(message),
            JsErrorKind.RangeError => JSEngine.NewRangeError(message),
            JsErrorKind.SyntaxError => JSEngine.NewSyntaxError(message),
            JsErrorKind.ReferenceError => JSEngine.NewReferenceError(message),
            _ => JSEngine.NewError(message),
        };
    }

    /// <summary>
    /// The DOM exception to <see langword="throw"/> so that JavaScript sees a <c>DOMException</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Constructed through the realm's own <c>DOMException</c> global, so the object the page catches
    /// carries the <c>name</c>, <c>code</c> and <c>message</c> it branches on and is
    /// <c>instanceof DOMException</c>. That constructor is installed by the bridge's registration
    /// pass, not by the engine, so this is one of the few places a provider depends on the host having
    /// built its globals — and the fallback is what happens before it has, or in a realm that never
    /// will.
    /// </para>
    /// <para>
    /// The fallback throws a string rather than an <c>Error</c> on purpose: it matches what the
    /// bridge's <c>ThrowDOMException</c> did before it became a call to this member, and a page's
    /// <c>catch (e) { e.name }</c> reading <see langword="undefined"/> is a clearer signal that the
    /// globals are missing than an <c>Error</c> whose <c>name</c> is plausible-looking but wrong.
    /// </para>
    /// </remarks>
    public Exception DomError(string name, string message)
    {
        using var scope = Enter();

        if (_context[(KeyString)"DOMException"] is JSFunction constructor)
        {
            var error = constructor.CreateInstance(
                new Arguments(constructor, new JSString(message), new JSString(name)));

            return new JSException(error);
        }

        return new JSException(new JSString($"DOMException: {message} ({name})"));
    }

    /// <summary>
    /// Every handle in <paramref name="arguments"/> as the engine's values.
    /// </summary>
    /// <remarks>
    /// This one does allocate, and unlike the trampoline it has to: <c>Arguments</c> is a struct with
    /// four inline slots and no constructor that takes a span, so anything wider than four is going to
    /// become an array inside it anyway. Renting would not help — the array is handed to the engine
    /// and may outlive the call through a captured <c>arguments</c> object.
    /// </remarks>
    private static JSValue[] UnwrapAll(ReadOnlySpan<JsValue> arguments)
    {
        if (arguments.IsEmpty)
            return [];

        var unwrapped = new JSValue[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
            unwrapped[i] = BroilerJsMarshal.Unwrap(arguments[i]);

        return unwrapped;
    }

    /// <summary>
    /// A JavaScript exception as the one type a host catch block is written against.
    /// </summary>
    /// <remarks>
    /// The value the page threw is carried across rather than flattened to its message, because
    /// <c>throw 42</c> and <c>throw {code: 5}</c> are both legal and a host that reports only a
    /// message loses what the page said. The engine exception stays as the inner one, so a diagnostic
    /// that wants the CLR frames can still reach them.
    /// </remarks>
    /// <param name="engineException">The engine's exception.</param>
    /// <param name="sourceLabel">The selected identity when the throw escaped a source member.</param>
    /// <param name="source">The text that member evaluated, which a reported line must refer to.</param>
    internal static JsEngineException Translate(
        JSException engineException, string? sourceLabel = null, string? source = null)
    {
        var trace = engineException.JSStackTrace?.ToString();

        return new(engineException.Message, BroilerJsMarshal.Wrap(engineException.Error), engineException)
        {
            ScriptStackTrace = trace,
            SourceLabel = sourceLabel,
            SourceLine = sourceLabel is null || source is null
                ? null
                : CompileFailureLine(engineException.Message, trace, sourceLabel, source),
        };
    }
}

