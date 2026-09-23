namespace Broiler.JSeal.Providers;

/// <summary>
/// The mint side of <see cref="JsDetachedValue"/>: how a provider wraps the graph its own structured
/// clone produced, and reads it back.
/// </summary>
/// <remarks>
/// The same rule as <see cref="JsProviderValue"/>, for the same reasons — public so that a provider
/// can be written outside this repository, in the <c>Providers</c> namespace so that a file which
/// needs it says so in its usings, and never called by a binding. A host that could unwrap a carrier
/// would be holding one realm's object graph on another realm's thread, which is precisely what
/// <see cref="IJsClone.Detach"/> and <see cref="IJsClone.Adopt"/> exist to keep it from doing.
/// </remarks>
public static class JsProviderClone
{
    /// <summary>
    /// Wraps a detached clone.
    /// </summary>
    /// <param name="engineName">
    /// The engine that produced it, as <see cref="IJsRealm.EngineName"/> spells it.
    /// <see cref="IJsClone.Adopt"/> refuses a carrier from another engine on the strength of this.
    /// </param>
    /// <param name="payload">
    /// The engine's own graph, or <see langword="null"/> when the clone is a value the provider can
    /// re-materialise without one.
    /// </param>
    public static JsDetachedValue Detached(string engineName, object? payload) =>
        JsDetachedValue.Create(engineName, payload);

    /// <summary>
    /// The graph behind <paramref name="detached"/>, for the provider that minted it.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="detached"/> is <see langword="null"/>.</exception>
    public static object? PayloadOf(JsDetachedValue detached) =>
        (detached ?? throw new ArgumentNullException(nameof(detached))).Payload;
}

