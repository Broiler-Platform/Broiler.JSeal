namespace Broiler.JSeal;

/// <summary>
/// What a value is, as far as a transfer list is concerned.
/// </summary>
/// <remarks>
/// This is the specification's vocabulary rather than an engine's: structured clone defines
/// <em>transferable objects</em> and gives each one a <c>[[Detached]]</c> internal slot, and the two
/// questions a host has to ask of a <c>postMessage</c> transfer list are exactly "may this appear
/// here" and "has it already been spent". A host that asked instead whether a value was an
/// <c>ArrayBuffer</c> would be naming an engine's type for a question the language already has words
/// for.
/// </remarks>
public enum JsTransferKind
{
    /// <summary>Not a transferable object; in a transfer list this is a <c>DataCloneError</c>.</summary>
    NotTransferable,

    /// <summary>A transferable object that has not been transferred yet.</summary>
    Transferable,

    /// <summary>A transferable object that is already detached; transferring it again is a <c>DataCloneError</c>.</summary>
    Detached,
}

/// <summary>
/// A structured clone that belongs to no realm: the intermediate a message occupies between the
/// thread that sent it and the thread that will receive it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type exists because a <see cref="JsValue"/> cannot do the job.</b> A handle is only
/// meaningful to the realm that minted it, and the clone's result may be a primitive, for which a
/// handle carries no engine instance at all. A worker's inbox therefore cannot be a queue of handles;
/// the contract has to name a third thing, say who may hold one, and say for how long. It is that
/// third thing.
/// </para>
/// <para>
/// <b>Nothing may read it but the provider that made it.</b> The payload is deliberately not exposed
/// on this type — <see cref="Providers.JsProviderClone"/> is the only way in and out, the way
/// <see cref="Providers.JsProviderValue"/> is for a handle — because a host that could unwrap one
/// would be holding one realm's object graph on another realm's thread, which is the race the second
/// clone exists to prevent. What a host may do with it is carry it, and hand it to
/// <see cref="IJsClone.Adopt"/>.
/// </para>
/// <para>
/// <b>It is engine-scoped, not realm-scoped.</b> <see cref="EngineName"/> is carried so that
/// <see cref="IJsClone.Adopt"/> can refuse a carrier another engine minted — a process with two
/// engines linked will have both kinds in flight, and a graph from one is not a graph the other can
/// walk. It names no realm because not belonging to one is the whole point.
/// </para>
/// </remarks>
public sealed class JsDetachedValue
{
    private JsDetachedValue(string engineName, object? payload)
    {
        EngineName = engineName;
        Payload = payload;
    }

    /// <summary>The engine whose structured clone produced this, as <see cref="IJsRealm.EngineName"/> spells it.</summary>
    public string EngineName { get; }

    /// <summary>The engine's own graph. Providers only; see <see cref="Providers.JsProviderClone"/>.</summary>
    internal object? Payload { get; }

    internal static JsDetachedValue Create(string engineName, object? payload) =>
        new(engineName ?? throw new ArgumentNullException(nameof(engineName)), payload);
}

/// <summary>
/// Structured clone: copying a value, and moving one from a realm on this thread to a realm on
/// another. <see cref="Clone"/> and <see cref="ClassifyTransferable"/> require
/// <see cref="JsCapabilities.StructuredClone"/>; <see cref="Detach"/> and <see cref="Adopt"/> require
/// <see cref="JsCapabilities.WorkerRealms"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three operations rather than one, because a browser does three different things.</b>
/// Same-document messaging (<c>window.postMessage</c>, a <c>MessagePort</c>) clones once, in one
/// realm, and delivers the copy — that is <see cref="Clone"/>. A message crossing to a Worker is
/// cloned <em>twice</em>, and the pair is the whole reason the crossing is safe: once on the sending
/// thread into a graph no script can reach (<see cref="Detach"/>), and again on the receiving thread
/// into the receiving realm (<see cref="Adopt"/>). Cloning once and handing the result over would put
/// one realm's object graph in another thread's hands; cloning once on the receiver, from the
/// sender's live value, is worse, because the sending script keeps running and can mutate that graph
/// while the receiver walks it.
/// </para>
/// <para>
/// <b>The sending side is where the errors belong</b>, and both <see cref="Clone"/> and
/// <see cref="Detach"/> are on it. A value that cannot be cloned fails there, where the page's own
/// <c>postMessage</c> call is still on the stack; a transfer list detaches its buffers there, so the
/// sender observes them detached immediately; and mutating the payload after the send is invisible to
/// the receiver, as the messaging model requires, because what the receiver will read is already a
/// copy.
/// </para>
/// <para>
/// <b>Which realm a clone is minted into is decided by which realm is asked.</b> That is why these are
/// realm members rather than a free function taking two realms: on an engine that resolves intrinsics
/// from ambient state — Broiler.JS reads a thread-static to decide whose <c>Object.prototype</c> a new
/// object gets — a clone taken outside a realm call mints into whatever realm happened to be current,
/// silently, on exactly the path where the two realms are supposed to stop touching. A provider
/// enters its realm for the duration of a contract call; putting the clone on the contract is what
/// brings it inside that bracket.
/// </para>
/// </remarks>
public interface IJsClone
{
    /// <summary>
    /// Whether <paramref name="value"/> may appear in a transfer list, and whether it is spent.
    /// </summary>
    /// <remarks>
    /// A host asks this because a transfer list is not all engine business: a <c>MessagePort</c> is
    /// transferable and is a thing the <em>bridge</em> owns, so the walk over the list has to be the
    /// host's and only the entries it does not recognise are the engine's to classify. Answering
    /// without entering the engine is permitted and expected; the question is decidable from the
    /// object. It answers rather than refusing: a realm without
    /// <see cref="JsCapabilities.StructuredClone"/> has nothing it can transfer, and answers
    /// <see cref="JsTransferKind.NotTransferable"/>.
    /// </remarks>
    JsTransferKind ClassifyTransferable(JsValue value);

    /// <summary>
    /// Structured-clones <paramref name="value"/> into this realm, detaching every object in
    /// <paramref name="transfer"/>.
    /// </summary>
    /// <param name="value">The value to clone. A primitive clones to itself.</param>
    /// <param name="transfer">
    /// The transfer list: objects whose contents move rather than being copied, and which the sender
    /// observes detached afterwards. Every entry must be <see cref="JsTransferKind.Transferable"/>;
    /// the host is expected to have validated the list, because the errors a page sees for a bad one
    /// are DOM errors with wording the host owns.
    /// </param>
    /// <exception cref="JsEngineException">The value is not cloneable, or an entry is not transferable.</exception>
    /// <exception cref="JsCapabilityUnavailableException">The realm lacks <see cref="JsCapabilities.StructuredClone"/>.</exception>
    JsValue Clone(JsValue value, ReadOnlySpan<JsValue> transfer = default);

    /// <summary>
    /// Structured-clones <paramref name="value"/> into a graph no script holds a reference to, for a
    /// realm on another thread to <see cref="Adopt"/>.
    /// </summary>
    /// <param name="value">The value to clone, in this realm's vocabulary.</param>
    /// <param name="transfer">The transfer list, as <see cref="Clone"/> takes it.</param>
    /// <remarks>
    /// Called on the <em>sending</em> thread, with the sending realm's values. The result is inert: it
    /// is not reachable from any realm's script, so nothing can mutate it while the receiving thread
    /// reads it, and it may cross threads.
    /// </remarks>
    /// <exception cref="JsEngineException">The value is not cloneable, or an entry is not transferable.</exception>
    /// <exception cref="JsCapabilityUnavailableException">The realm lacks <see cref="JsCapabilities.WorkerRealms"/>.</exception>
    JsDetachedValue Detach(JsValue value, ReadOnlySpan<JsValue> transfer = default);

    /// <summary>
    /// Materialises <paramref name="detached"/> into this realm — the second of the two clones.
    /// </summary>
    /// <remarks>
    /// Called on the <em>receiving</em> thread, so the objects it mints are the receiving realm's own
    /// and share no identity with the sender's. A carrier may be adopted more than once; each adoption
    /// is an independent copy, which is what one message delivered to two realms would need.
    /// </remarks>
    /// <exception cref="JsEngineException">
    /// The carrier was minted by a different engine, so this realm cannot walk its graph.
    /// </exception>
    /// <exception cref="JsCapabilityUnavailableException">The realm lacks <see cref="JsCapabilities.WorkerRealms"/>.</exception>
    JsValue Adopt(JsDetachedValue detached);
}

