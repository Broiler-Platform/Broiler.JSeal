using Broiler.JSeal.Providers;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Array.Typed;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.Globals;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.JSeal.BroilerJs;

/// <summary>
/// <see cref="IJsClone"/>: structured clone, and the two-clone crossing a Worker message makes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The algorithm is the engine's own, called at its static entry point.</b> Broiler.JS implements
/// structured clone in <c>Broiler.JavaScript.Globals</c>, and this provider references that assembly
/// for this one method â€” see the argument in the project file, which is where a reference that moves
/// the neutrality budget belongs. Calling the realm's <c>structuredClone</c> <em>global</em> instead
/// would have needed no reference and does not work: the engine gates that export behind
/// <c>JavaScriptFeatureFlags.StructuredClone</c>, an experimental flag that is off by default, so
/// <c>typeof structuredClone</c> is <c>"undefined"</c> on a context this provider builds and on the
/// one the host hands it to adopt alike. The static entry point is not gated, which is why the DOM
/// bridge always called it directly â€” and calling it is all that moved.
/// </para>
/// <para>
/// <b>Reimplementing the algorithm here was the alternative, and it would have been a second
/// definition of which types survive a clone.</b> Date, RegExp, Map, Set, ArrayBuffer, the typed
/// arrays, Error and circular references are all decided by that one function today; a copy in this
/// file would have drifted from it, and the drift would have shown up as a value that crossed to a
/// Worker in a shape the same page could not produce at home.
/// </para>
/// <para>
/// <b>Operations that clone take the realm scope</b>, for the reason the class remarks give and with one
/// extra consequence that is the point of putting the clone on the contract at all: the engine's
/// clone mints its objects against the <em>current</em> context. Outside a scope it would mint into
/// whatever realm the thread last touched â€” on the worker's delivery path, plausibly the page's.
/// </para>
/// </remarks>
internal partial class BroilerJsRealm
{
    /// <inheritdoc />
    /// <remarks>
    /// <c>ArrayBuffer</c> is the whole of what this engine can transfer, and
    /// <c>JSArrayBuffer.Detached</c> is its <c>[[Detached]]</c> slot. No engine call is needed: both
    /// questions are answered by the object the handle already carries, which is what the contract
    /// says a host may assume of this member. It still requires a live realm.
    /// </remarks>
    public JsTransferKind ClassifyTransferable(JsValue value)
    {
        ThrowIfDisposed();
        return JsProviderValue.ReferenceOf(value) switch
        {
            JSArrayBuffer { Detached: true } => JsTransferKind.Detached,
            JSArrayBuffer => JsTransferKind.Transferable,
            _ => JsTransferKind.NotTransferable,
        };
    }

    /// <inheritdoc />
    public JsValue Clone(JsValue value, ReadOnlySpan<JsValue> transfer = default)
    {
        ThrowIfDisposed();
        using var scope = Enter();
        return BroilerJsMarshal.Wrap(CloneInRealm(BroilerJsMarshal.Unwrap(value), transfer));
    }

    /// <inheritdoc />
    public JsDetachedValue Detach(JsValue value, ReadOnlySpan<JsValue> transfer = default)
    {
        ThrowIfDisposed();
        using var scope = Enter();

        // The carrier holds the engine's clone rather than a JSEAL handle. A handle is only
        // meaningful to the realm that minted it, and this graph is about to be read by another one.
        return JsProviderClone.Detached(EngineName, CloneInRealm(BroilerJsMarshal.Unwrap(value), transfer));
    }

    /// <inheritdoc />
    public JsValue Adopt(JsDetachedValue detached)
    {
        ArgumentNullException.ThrowIfNull(detached);
        ThrowIfDisposed();

        // A process with two engines linked has both kinds of carrier in flight, and a graph from the
        // other one is not a graph this engine can walk. Refusing by name rather than by type test is
        // what lets a provider outside this repository be told apart from this one.
        if (!string.Equals(detached.EngineName, EngineName, StringComparison.Ordinal))
        {
            throw new JsEngineException(
                $"A structured clone from the '{detached.EngineName}' engine cannot be adopted into a " +
                $"'{EngineName}' realm.");
        }

        using var scope = Enter();

        // No transfer list on the second clone: a transfer detaches the SENDER's buffers, and the
        // sender already paid that on the way into the carrier. Detaching again here would detach the
        // intermediate, which nothing else will ever read.
        var payload = JsProviderClone.PayloadOf(detached) as JSValue ?? JSUndefined.Value;
        return BroilerJsMarshal.Wrap(CloneInRealm(payload, transfer: default));
    }

    /// <summary>
    /// One structured clone, with the realm already current.
    /// </summary>
    /// <remarks>
    /// The options object is built only when something is being transferred, so the common call is
    /// the one-argument one the engine's own fast path takes. <c>{ transfer: [...] }</c> is the
    /// spelling <c>structuredClone</c> itself defines; the bare-array spelling a page may write to
    /// <c>postMessage</c> is the host's to normalise, because it is <c>postMessage</c>'s signature
    /// rather than the clone's.
    /// </remarks>
    private static JSValue CloneInRealm(JSValue value, ReadOnlySpan<JsValue> transfer)
    {
        try
        {
            if (transfer.IsEmpty)
                return JSGlobalStatic.StructuredClone(new Arguments(JSUndefined.Value, value));

            return JSGlobalStatic.StructuredClone(
                new Arguments(JSUndefined.Value, value, BuildTransferOptions(transfer)));
        }
        catch (JSException engineException)
        {
            throw Translate(engineException);
        }
    }

    /// <summary>The <c>{ transfer: [...] }</c> options object for a non-empty transfer list.</summary>
    private static JSValue BuildTransferOptions(ReadOnlySpan<JsValue> transfer)
    {
        var buffers = UnwrapAll(transfer);

        var options = new JSObject();
        options.FastAddValue(
            (KeyString)"transfer", new JSArray(buffers), JSPropertyAttributes.EnumerableConfigurableValue);

        return options;
    }
}

