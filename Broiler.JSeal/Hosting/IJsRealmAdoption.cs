namespace Broiler.JSeal;

/// <summary>An optional provider interface for wrapping an existing engine realm.</summary>
/// <remarks>
/// Adoption lets a host use JSEAL with a context it created through an engine-specific API.
/// Broiler.JS implements this interface for JSContext; the VM provider does not implement it.
/// The wrapper borrows the context. Hosts must retain ownership and dispose the wrapper before
/// disposing the underlying context. See TryAdopt for policy and queue ownership rules.
/// </remarks>
public interface IJsRealmAdoption
{
    /// <summary>Wrap an engine realm of a supported type, or return false with a null result.</summary>
    /// <remarks>
    /// The wrapper borrows the underlying realm and must not dispose it. It applies the supplied policy,
    /// obeys the same lifetime rules as a created realm, discards its own queued jobs on disposal and
    /// removes only its own policy subscriptions. The original host retains its context and job queue.
    /// Adopting the same context twice may return distinct wrappers; callers retain the wrapper they need.
    /// </remarks>
    bool TryAdopt(object engineRealm, JsRealmOptions options, out IJsRealm? realm);
}

