namespace Broiler.JSeal;

/// <summary>
/// One JavaScript realm: a global object, its intrinsics, and everything reachable from them.
/// </summary>
/// <remarks>
/// <para>
/// This is the type the DOM bridge binds a document against, and the reason JSEAL exists. The
/// bridge named <c>JSContext</c> in fourteen places on 2026-09-08 and still takes one as a
/// parameter on <c>IDomBridgeRuntime.Attach</c>, which is why an engine that is not Broiler.JS
/// cannot serve a page no matter what <c>IScriptEngine</c> says: the seam is one level too high. A
/// realm is the right level, because a realm is what a document has.
/// </para>
/// <para>
/// <b>The global object may or may not be the realm object.</b> Under Broiler.JS it is â€” <c>window</c>
/// <em>is</em> the <c>JSContext</c>, which is itself a <c>JSValue</c> â€” and the bridge relies on that
/// in the sweep that mirrors window members onto the global. <see cref="Global"/> is a separate
/// member here so a provider whose engine separates them can answer honestly; see
/// <see cref="JsCapabilities.GlobalIsVariableScope"/>.
/// </para>
/// <para>
/// <b>Realms are not thread-affine by contract, but a provider may be.</b> A Worker gets a realm of
/// its own on its own thread. Nothing here promises that two threads may touch one realm at once, and
/// the Broiler.JS provider does not permit it; a host that wants to must serialise. What the two
/// realms may exchange is structured clones, and <see cref="IJsClone"/> is the whole of how â€” it is
/// the second half of <see cref="JsCapabilities.WorkerRealms"/>, the first half being that a provider
/// can build the second realm at all.
/// </para>
/// </remarks>
public interface IJsRealm : IJsValues, IJsMembers, IJsCalls, IJsJobs, IJsSource, IJsClone, IDisposable
{
    /// <summary>The global object â€” <c>globalThis</c>.</summary>
    JsValue Global { get; }

    /// <summary>What this realm's engine can do. Fixed for the life of the realm.</summary>
    JsCapabilities Capabilities { get; }

    /// <summary>
    /// The engine that built this realm, for a host that needs to name it in a log line or a
    /// diagnostic.
    /// </summary>
    string EngineName { get; }
}

