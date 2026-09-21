using Broiler.VM.Profile.JavaScript;

namespace Broiler.JSeal.Vm;

/// <summary>
/// What the composition registers so the profile hands its realm over, and how a turn is taken.
/// </summary>
/// <remarks>
/// <para>
/// <b>It does nothing at realm creation, which is the opposite of what an embedder usually does
/// with this callback.</b> The profile's host surface is designed for a composition that installs
/// its own object model the moment a realm exists; a JSEAL provider installs nothing, because what
/// goes into the realm is the DOM bridge's business and it has not been asked yet. So this captures
/// the realm and returns, and every later crossing arrives either inside a step already or through
/// a turn.
/// </para>
/// <para>
/// <b>The pending action is one deep and cleared before it runs.</b> A turn runs one crossing,
/// because a crossing is what asked for the turn; clearing first means an action that itself asks
/// for a turn - which it cannot, being already in a step - could not re-enter this one.
/// </para>
/// </remarks>
internal sealed class VmHostBridge : IJsHostSurface
{
    /// <summary>The realm the profile handed over, or null before instantiation completed.</summary>
    internal JsHostRealm? Realm { get; private set; }

    /// <summary>
    /// The realm's <c>Promise</c>, taken before any page script could replace it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is captured here because <c>Promise</c> is an ordinary writable global and a page may
    /// assign over it.</b> The profile installs it <c>Writable | Configurable</c>, as the language
    /// requires â€” so a provider that read <c>globalThis.Promise</c> at the moment the bridge wanted
    /// a promise would hand a page's own constructor whatever <c>fetch</c> was about to resolve,
    /// and a page that had replaced it would receive every deferred result the bridge produces.
    /// Reading it once, before any guest program has run, is what makes <c>NewPromise</c> answer
    /// with the intrinsic.
    /// </para>
    /// <para>
    /// <b>This is deliberately the opposite of what <c>DomError</c> does with <c>DOMException</c>,
    /// and the difference is who owns the global.</b> <c>DOMException</c> is the bridge's to install
    /// and does not exist yet when a realm is created, so resolving it late is the only way to find
    /// it. <c>Promise</c> is the realm's own and exists before anyone else can touch it, so
    /// resolving it late is the only way to lose it.
    /// </para>
    /// </remarks>
    internal JsHostValue Promise { get; private set; }

    /// <summary>
    /// The realm's <c>Proxy</c>, taken at the same moment and for the same reason as
    /// <see cref="Promise"/>.
    /// </summary>
    /// <remarks>
    /// The profile's host-object surface has no delete hook, so a handler that completes deletions
    /// is expressed as a proxy over the host exotic; see <c>VmRealm.Deleting</c>. <c>Proxy</c> is an
    /// ordinary writable global like <c>Promise</c>, so reading it when a storage area is minted -
    /// which is after the bridge has installed a document, and can be after page script has run -
    /// would let a page hand itself every exotic object the bridge builds from then on.
    /// </remarks>
    internal JsHostValue Proxy { get; private set; }

    /// <summary>
    /// The realm's <c>eval</c>, taken before any page script could replace it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the capture with the most on it, because reading it late was a
    /// Content-Security-Policy bypass and not only an interception.</b> <c>eval</c> is an ordinary
    /// writable global - <c>JsRealm.Dynamic.cs</c> installs it <c>Writable | Configurable</c>, as the
    /// language requires - so <c>globalThis.eval = f</c> is a thing a page may legally do. Reading it
    /// when a host asked the realm to run a script therefore invoked the page's function, handing it
    /// every polyfill the bridge installs and taking whatever it returned as the result.
    /// </para>
    /// <para>
    /// <see cref="VmSourceProvider"/> authorizes one compilation for a host or classic script.
    /// Capturing the intrinsic ensures that the authorized source reaches the compiler directly:
    /// a page-supplied replacement must not run first and spend that permit on its own source.
    /// </para>
    /// <para>
    /// The permit is consumed before the script executes. If host script subsequently invokes a
    /// page callback, that callback's eval and Function requests still meet the realm's guest
    /// policy. Both this capture and consumption before execution are required for the boundary.
    /// </para>
    /// </remarks>
    internal JsHostValue Eval { get; private set; }

    /// <summary>
    /// The realm's <c>Reflect.deleteProperty</c>, taken at the same moment and for the same reason.
    /// </summary>
    /// <remarks>
    /// <b>It is the forwarder a <c>deleteProperty</c> trap needs and the host surface cannot be.</b>
    /// A trap is handed the key the guest used, which may be a Symbol, and
    /// <c>JsHostRealm.DeleteProperty</c> deletes by string name only - so a symbol-keyed deletion
    /// routed through the host surface would be dropped in silence, and the proxy's own invariant
    /// check would not catch it because the property is configurable. The intrinsic takes both
    /// kinds of key.
    /// </remarks>
    internal JsHostValue ReflectDelete { get; private set; }

    /// <summary>
    /// The realm's binary intrinsics, taken at the same moment and for the same reason as
    /// <see cref="Promise"/>: <c>ArrayBuffer</c>, <c>Uint8Array</c>, the <c>byteLength</c> getter off
    /// <c>ArrayBuffer.prototype</c>, and the four functions the bulk transfer uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The profile's host surface has no binary member at all, so a buffer is reached through the
    /// realm's own intrinsics</b> - the route <c>NewPromise</c> took, applied to a second family.
    /// Every one of these is an ordinary writable global or a writable prototype member, so all of
    /// them are read once here rather than when a buffer is wanted.
    /// </para>
    /// <para>
    /// <b>The <c>byteLength</c> getter is the brand check and there is no other.</b> Its body is
    /// <c>BinaryThisBuffer</c>, which is <c>value.AsObjectOrNull() is JsArrayBuffer</c> and throws a
    /// TypeError for anything else - so invoking it with a candidate as <c>this</c> answers "is this
    /// an ArrayBuffer" and the length in one crossing, and answers it by CLR type rather than by
    /// anything a page can write. No JS-visible property can do that: a <c>DataView</c> and every
    /// typed array answer <c>byteLength</c>, a prototype is settable, a <c>Symbol.toStringTag</c> is
    /// writable.
    /// </para>
    /// <para>
    /// <b>They may all be absent, and that is not an error here.</b> The profile builds its binary
    /// intrinsics only for a composition that admits its binary surface. When they are missing
    /// <see cref="HasBinary"/> is false and the provider narrows
    /// <see cref="JsCapabilities.BinaryData"/> out of the realm it hands back, which is what a
    /// capability is for.
    /// </para>
    /// </remarks>
    internal JsHostValue ArrayBuffer { get; private set; }

    /// <inheritdoc cref="ArrayBuffer"/>
    internal JsHostValue Uint8Array { get; private set; }

    /// <inheritdoc cref="ArrayBuffer"/>
    internal JsHostValue ArrayBufferByteLength { get; private set; }

    /// <inheritdoc cref="ArrayBuffer"/>
    internal JsHostValue TypedArraySet { get; private set; }

    /// <inheritdoc cref="ArrayBuffer"/>
    internal JsHostValue TypedArraySubarray { get; private set; }

    /// <inheritdoc cref="ArrayBuffer"/>
    internal JsHostValue TypedArrayJoin { get; private set; }

    /// <summary>Whether every binary intrinsic the provider needs was on the realm.</summary>
    internal bool HasBinary =>
        ArrayBuffer.Kind is JsHostValueKind.Function &&
        Uint8Array.Kind is JsHostValueKind.Function &&
        ArrayBufferByteLength.Kind is JsHostValueKind.Function &&
        TypedArraySet.Kind is JsHostValueKind.Function &&
        TypedArraySubarray.Kind is JsHostValueKind.Function &&
        TypedArrayJoin.Kind is JsHostValueKind.Function;

    /// <summary>The one crossing waiting for a step.</summary>
    internal Action<JsHostRealm>? Pending { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// This runs inside a step the profile opens at instantiation, so the crossing below is legal
    /// here and would not be from anywhere else this class is reachable from.
    /// </remarks>
    public void OnRealmCreated(JsHostRealm realm)
    {
        Realm = realm;
        Promise = realm.GetProperty(realm.Global, "Promise");
        Proxy = realm.GetProperty(realm.Global, "Proxy");
        Eval = realm.GetProperty(realm.Global, "eval");
        ReflectDelete = realm.GetProperty(realm.GetProperty(realm.Global, "Reflect"), "deleteProperty");
        CaptureBinary(realm);
    }

    /// <summary>
    /// Reads the binary intrinsics off the realm, or leaves them missing when the composition
    /// declined the profile's binary surface. See <see cref="ArrayBuffer"/>.
    /// </summary>
    private void CaptureBinary(JsHostRealm realm)
    {
        ArrayBuffer = realm.GetProperty(realm.Global, "ArrayBuffer");
        Uint8Array = realm.GetProperty(realm.Global, "Uint8Array");

        if (ArrayBuffer.Kind is not JsHostValueKind.Function ||
            Uint8Array.Kind is not JsHostValueKind.Function)
        {
            return;
        }

        var viewPrototype = realm.GetProperty(Uint8Array, "prototype");
        TypedArraySet = realm.GetProperty(viewPrototype, "set");
        TypedArraySubarray = realm.GetProperty(viewPrototype, "subarray");
        TypedArrayJoin = realm.GetProperty(viewPrototype, "join");

        // The getter itself, not the property: reading `ArrayBuffer.prototype.byteLength` would
        // INVOKE it with the prototype as `this`, which is exactly the case its brand check throws
        // for. A descriptor read is the only way to hold the function.
        var descriptor = realm.Invoke(
            realm.GetProperty(realm.GetProperty(realm.Global, "Object"), "getOwnPropertyDescriptor"),
            JsHostValue.Undefined,
            [realm.GetProperty(ArrayBuffer, "prototype"), JsHostValue.String("byteLength")]);

        if (descriptor.Kind is JsHostValueKind.Object)
            ArrayBufferByteLength = realm.GetProperty(descriptor, "get");
    }

    /// <inheritdoc />
    public void OnTurn(JsHostRealm realm)
    {
        var work = Pending;
        Pending = null;
        work?.Invoke(realm);
    }
}

