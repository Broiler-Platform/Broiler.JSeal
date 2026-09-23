namespace Broiler.JSeal;

/// <summary>One JavaScript realm: its global object, intrinsics and reachable values.</summary>
/// <remarks>
/// <para>
/// The realm aggregates value, member, call, job, source and clone operations. Global is separate
/// from the realm object so providers can represent their engine's global-object model accurately.
/// JsCapabilities.GlobalIsVariableScope describes whether top-level declarations are global properties.
/// </para>
/// <para>
/// Hosts serialize operations on a realm; providers may impose thread affinity. WorkerRealms means
/// separate realms can be created on separate threads and exchange structured clones. It does not
/// permit two threads to execute one realm concurrently.
/// </para>
/// <para>
/// Dispose is idempotent and does not run queued jobs. EngineName and Capabilities remain readable.
/// Every other realm operation throws ObjectDisposedException after disposal for valid arguments,
/// including primitive inputs and unavailable capabilities. Invalid-argument validation order is
/// unspecified. Retained promise settlers also throw, even for settled promises, without reading
/// thenables or scheduling reactions. Handle-only JsValue inspections remain available.
/// </para>
/// <para>
/// An adopted wrapper discards its own jobs but does not own the underlying context or host queue;
/// see IJsRealmAdoption. Hosts must serialize settlement and disposal with other operations and
/// dispose only after an active operation returns.
/// </para>
/// </remarks>
public interface IJsRealm : IJsValues, IJsMembers, IJsCalls, IJsJobs, IJsSource, IJsClone, IDisposable
{
    /// <summary>The global object — <c>globalThis</c>.</summary>
    JsValue Global { get; }

    /// <summary>What this realm's engine can do. Fixed for the life of the realm.</summary>
    JsCapabilities Capabilities { get; }

    /// <summary>
    /// The engine that built this realm, for a host that needs to name it in a log line or a
    /// diagnostic.
    /// </summary>
    string EngineName { get; }
}

