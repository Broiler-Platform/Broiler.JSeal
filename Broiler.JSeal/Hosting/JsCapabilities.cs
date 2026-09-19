namespace Broiler.JSeal;

/// <summary>
/// What an engine can do, declared rather than discovered.
/// </summary>
/// <remarks>
/// <para>
/// A host that needs a feature an engine lacks has two honest options â€” degrade, or refuse â€” and
/// exactly one dishonest one, which is to call and hope. The bridge already does the dishonest thing
/// once, in <c>EngineModuleSupport</c>: it probes for ES-module support by running a module and
/// checking the binding, guarded by a five-second timeout because on an engine without the fix the
/// probe <em>hangs</em> rather than failing. That is what discovery costs when a contract could have
/// said so.
/// </para>
/// <para>
/// <b>These are capabilities, not versions.</b> Nothing here names an engine or a release. A provider
/// answers for the engine it was built against, and a host branches on the answer.
/// </para>
/// </remarks>
[Flags]
public enum JsCapabilities : uint
{
    /// <summary>An engine that can do none of the below. Not useful; the zero value exists so a
    /// provider under construction has something to return.</summary>
    None = 0,

    /// <summary>
    /// Runs JavaScript this repository authored â€” the embedded polyfill assets and the source of
    /// every other <c>EvaluateHostScript</c> call in the bridge. An engine without a run-time
    /// compiler can still have this, by compiling that source when the engine is built. See
    /// <see cref="IJsSource"/>.
    /// </summary>
    HostScriptSource = 1 << 0,

    /// <summary>
    /// Runs JavaScript the page asked to evaluate AT RUN TIME â€” <c>eval</c> and <c>new Function</c>.
    /// A realm built for a page whose Content-Security-Policy forbids evaluation does not have this,
    /// on any engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is <c>'unsafe-eval'</c> and nothing else, and the negative half is worth stating
    /// because the tree inferred more from it than it says.</b> A realm without this still runs the
    /// page's script ELEMENTS â€” see <see cref="ClassicScriptSource"/> â€” and refuses only the page's
    /// <c>eval</c> and <c>new Function</c>. The two are separate CSP directives, decided by
    /// different code at different times: <c>script-src</c> is per script and satisfied by
    /// <c>'unsafe-inline'</c>, a nonce or a hash, while <c>'unsafe-eval'</c> is per realm. A page
    /// served <c>script-src 'unsafe-inline'</c> runs every one of its script elements and no
    /// <c>eval</c>; a page served <c>script-src 'nonce-x' 'unsafe-eval'</c> is the other way round.
    /// </para>
    /// <para>
    /// <b>It is enforced INSIDE the realm, on the page's own <c>eval</c> and <c>Function</c>, which is
    /// where a browser enforces it.</b> That is a change: it used to be enforced only at the host
    /// door â€” the member a host calls â€” which is not a door a page walks through, so on one of the two
    /// providers a page in a realm built without this could call <c>eval</c> and it worked.
    /// </para>
    /// <para>
    /// <b>This flag keeps the name <c>GuestEval</c> although the member it gates is now called
    /// <c>EvaluateDynamicSource</c>, and so does <c>JsRealmOptions.AllowGuestEval</c>.</b> The member
    /// was renamed because "guest source" described PROVENANCE, and provenance is the axis that
    /// turned out to be wrong -- a classic script is the guest's source too. These two name the
    /// PERMISSION, and the permission really is about the guest evaluating: <c>'unsafe-eval'</c>,
    /// <c>eval</c> and <c>new Function</c>. Renaming them would trade an accurate name for a
    /// consistent one.
    /// </para>
    /// </remarks>
    GuestEval = 1 << 1,

    /// <summary>
    /// Binds a static ES-module import end to end, so <c>import { x } from 'â€¦'</c> resolves to a
    /// value. This is what <c>EngineModuleSupport</c> probes for today.
    /// </summary>
    Modules = 1 << 2,

    /// <summary>Resolves a dynamic <c>import()</c> through a host-supplied module map.</summary>
    DynamicImport = 1 << 3,

    /// <summary>
    /// Exposes promises to the host: a pending promise can be created and settled from host code.
    /// Without it, <c>fetch</c>, <c>customElements.whenDefined</c> and the streams polyfill have no
    /// deferred result to hand back.
    /// </summary>
    Promises = 1 << 4,

    /// <summary>
    /// Supports exotic objects whose property lookup the host defines â€” live collections indexed by
    /// number and by name, <c>CSSStyleDeclaration</c>'s dashed properties, <c>Storage</c>'s keys.
    /// Six of the bridge's objects need it. See <see cref="IJsExotic"/>.
    /// </summary>
    ExoticObjects = 1 << 5,

    /// <summary>
    /// The global object doubles as the realm's variable scope, so a <c>var</c> or a function
    /// declaration at the top level of a script becomes a property of it.
    /// </summary>
    /// <remarks>
    /// The bridge depends on this and does not know it does: nested browsing contexts recover a
    /// frame's declarations by diffing <c>Object.getOwnPropertyNames(globalThis)</c> across the
    /// evaluation, which only finds anything on an engine where declarations land there.
    /// </remarks>
    GlobalIsVariableScope = 1 << 6,

    /// <summary>
    /// A second realm can be created on another thread and values moved between the two by structured
    /// clone â€” what a Worker needs.
    /// </summary>
    WorkerRealms = 1 << 7,

    /// <summary>
    /// A host function may call back into JavaScript while the engine is inside a host call â€” what an
    /// event listener, a promise reaction and a <c>toString</c> coercion all are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the capability that decides whether an engine can host a DOM at all.</b> An engine
    /// without it can run a page's script; it cannot dispatch a <c>click</c>.
    /// </para>
    /// <para>
    /// <b>These remarks used to name Broiler.VM as the engine that does not have it, and the reason
    /// they gave was wrong in a way worth keeping.</b> The reason given was that every capability
    /// that profile imports is declared non-reentrant and its core refuses a re-entrant call for the
    /// duration of a host frame. The first half was true and the second was true only of a
    /// capability that DECLARES non-reentrance - the refusal is keyed on the declaration, and
    /// nothing there had ever declared the other mode. The deeper error was reading the capability
    /// channel as the only way host code can reach a guest: on that engine a host object is an
    /// ordinary object in the realm, so a listener call never crosses the core and never meets a
    /// gate. See <c>docs/jseal.md</c> for what a provider there would now cost.
    /// </para>
    /// </remarks>
    ReentrantHostCalls = 1 << 8,

    /// <summary>
    /// Minting an <c>ArrayBuffer</c> over host bytes and reading one back. See
    /// <see cref="IJsValues.NewArrayBuffer"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is a capability rather than an assumption because a realm can genuinely lack it.</b> One
    /// engine has <c>ArrayBuffer</c> unconditionally; the other builds the binary intrinsics only for
    /// a composition that admits its binary surface, so a host that named surfaces explicitly could
    /// get a realm with no <c>ArrayBuffer</c> on the global at all. Calling and hoping is what a
    /// capability exists to replace.
    /// </para>
    /// <para>
    /// <b>It is in <see cref="Document"/> because the bridge's own polyfills cannot install without
    /// it.</b> <c>Polyfills/streams.js</c> and <c>file-reader.js</c> name <c>Uint8Array</c>, and on an engine
    /// whose binary surface is optional an artifact naming a global of a declined surface is refused
    /// at verification rather than at the line that reads it. So a realm without this cannot carry
    /// the streams asset, and without that asset there is no <c>ReadableStream</c>, no
    /// <c>response.body</c>, no <c>blob.stream()</c> and no <c>FileReader</c>. That is not a page
    /// served in a degraded way; it is a page that does not load.
    /// </para>
    /// </remarks>
    BinaryData = 1 << 9,

    /// <summary>
    /// Compiles a classic script the page carries â€” a script element's text, a sub-document's, a
    /// worker's top-level script, an <c>importScripts</c> body, the wrapper compiled from an
    /// event-handler content attribute. Text this engine did not see when it was built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is separate from <see cref="HostScriptSource"/> because that one can be satisfied ahead
    /// of time and this one cannot.</b> An engine with no run-time compiler can carry the first by
    /// compiling THIS REPOSITORY's own JavaScript when the engine is built â€” that is the shape
    /// <see cref="IJsSource"/> describes as what makes a second engine possible at all. A page's
    /// script text is not knowable then, so the second does not come with it.
    /// </para>
    /// <para>
    /// <b>It is separate from <see cref="GuestEval"/> because the directives are separate.</b>
    /// Together the two split an ABILITY â€” a compiler over text nobody has seen â€” from a PERMISSION,
    /// <c>'unsafe-eval'</c>. The contract used to weld them into one member, so a host that read a
    /// restrictive policy and narrowed the realm would have refused the page's ordinary script
    /// elements, which every browser runs.
    /// </para>
    /// <para>
    /// <b>Declaring it says nothing about whether any particular script is allowed.</b> That decision
    /// is <c>script-src</c>'s, it is per script and content-dependent â€” a nonce matches this element
    /// and not the next â€” and the caller takes it before handing the text over, except on the
    /// worker path, which takes none today. This flag answers only "could this realm run a page's
    /// script at all".
    /// </para>
    /// </remarks>
    ClassicScriptSource = 1 << 10,

    /// <summary>Everything a document-bearing page load needs.</summary>
    /// <remarks>
    /// <b><see cref="ClassicScriptSource"/> belongs here for the reason <see cref="BinaryData"/>
    /// does, and its absence was an absurdity nobody could see while the situations were fused.</b>
    /// A realm that cannot run the document's own script elements is not a page served in a degraded
    /// way; it is a page that does not load. <see cref="GuestEval"/> stays out, as it always was â€”
    /// and that is now defensible rather than accidental, because a page whose policy forbids
    /// <c>eval</c> loads perfectly well.
    /// </remarks>
    Document = HostScriptSource | ClassicScriptSource | Promises | ExoticObjects |
               GlobalIsVariableScope | ReentrantHostCalls | BinaryData,
}

