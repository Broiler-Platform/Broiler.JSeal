using System.Diagnostics.CodeAnalysis;

namespace Broiler.JSeal;

// The realm surface is split into six narrow capability contracts, the way IScriptEngine was split in
// this repository's Phase 8. IJsRealm (see IJsRealm.cs) aggregates them, and every binding depends on
// the aggregate, so nothing at a call site gets longer. The split exists for the other two readers:
// a provider, which implements them one at a time and can say in its own source which group a file
// serves; and a reviewer asking what an engine must be able to do, who gets six answerable questions
// instead of one surface of forty members.

/// <summary>
/// Creating values, and the conversions between a JavaScript value and a CLR one that a handle cannot
/// perform for itself.
/// </summary>
/// <remarks>
/// <para>
/// The cheap conversions are not here â€” they are on <see cref="JsValue"/> itself
/// (<see cref="JsValue.AsBoolean"/>, <see cref="JsValue.AsNumber"/>, <see cref="JsValue.AsString"/>),
/// because a host that has to enter the engine to ask whether a value is truthy will do it on every
/// branch of every callback.
/// </para>
/// <para>
/// <b>What is here is what the handle cannot answer, and that is two reasons rather than one.</b>
/// This paragraph used to call the cheap conversions "decidable from the handle" and the members here
/// "the set that can run user code". <see cref="ToJsString"/> and <see cref="ToNumber"/> are here for
/// that reason: <c>ToString</c> on an object may call a <c>toString</c> the page wrote.
/// <see cref="ToBoolean"/> is not, because ECMAScript's ToBoolean calls nothing for any value. It is
/// here because truthiness is NOT decidable from the handle for every kind: a
/// <see cref="JsValueKind.BigInt"/> is carried as an opaque provider reference, so whether it is zero
/// is a question only code entitled to name the engine's value can ask.
/// </para>
/// </remarks>
public interface IJsValues
{
    /// <summary>A new ordinary object with the realm's <c>Object.prototype</c>.</summary>
    JsValue NewObject();

    /// <summary>A new Array, optionally pre-filled.</summary>
    JsValue NewArray(ReadOnlySpan<JsValue> elements = default);

    /// <summary>
    /// A new non-constructable host function â€” a WebIDL operation or attribute accessor.
    /// </summary>
    /// <remarks>
    /// <b>Non-constructable is the default because WebIDL says so, and because it is what makes a
    /// wrapper affordable.</b> Only interface objects are constructors; <c>el.setAttribute.prototype</c>
    /// is <c>undefined</c> and <c>new el.setAttribute()</c> throws. Under Broiler.JS this maps to
    /// <c>createPrototype: false</c>, which the bridge adopted as a memory fix as much as a
    /// correctness one â€” an element wrapper's members were each allocating an unreachable prototype
    /// object plus its <c>constructor</c> back-reference. Anything a page may legitimately
    /// <c>new</c> asks for <see cref="NewConstructor"/> instead, and there are sixteen of those.
    /// </remarks>
    /// <param name="name">The function's <c>name</c>.</param>
    /// <param name="body">The host code to run.</param>
    /// <param name="length">The function's declared <c>length</c> â€” its count of required arguments.</param>
    JsValue NewMethod(string name, JsNativeFunction body, int length = 0);

    /// <summary>
    /// A new constructable host function â€” an interface object a page may <c>new</c>
    /// (<c>Headers</c>, <c>Request</c>, <c>Response</c>, <c>FormData</c>, <c>Worker</c>, â€¦).
    /// </summary>
    /// <remarks>
    /// The returned function carries a <c>prototype</c> object, reachable with
    /// <see cref="IJsMembers.GetProperty"/>, which is where an interface's members are installed.
    /// </remarks>
    JsValue NewConstructor(string name, JsNativeFunction body, int length = 0);

    /// <summary>
    /// A new object whose property lookup the host completes â€” a live collection, a style
    /// declaration, a storage area. Requires <see cref="JsCapabilities.ExoticObjects"/>.
    /// </summary>
    /// <remarks>
    /// The handler answers only what the object's ordinary properties did not; see
    /// <see cref="IJsExotic"/> for why that order is not negotiable.
    /// </remarks>
    JsValue NewExotic(IJsExotic handler);

    /// <summary>
    /// A new <c>ArrayBuffer</c> holding a copy of <paramref name="bytes"/>. Requires
    /// <see cref="JsCapabilities.BinaryData"/>.
    /// </summary>
    /// <remarks>
    /// <b>A copy, and the span is what says so.</b> Every caller either clones first or hands over an
    /// array it has just built and will not touch again, so no caller wants aliasing - and a contract
    /// that permitted it would be one in which a page mutating <c>ImageData.data</c> could rewrite the
    /// blob it came from. A <see cref="ReadOnlySpan{T}"/> cannot be retained, which makes the copy the
    /// only implementable reading rather than a rule each provider has to remember.
    /// </remarks>
    JsValue NewArrayBuffer(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// The bytes of an <c>ArrayBuffer</c>, answering whether <paramref name="value"/> is one.
    /// Requires <see cref="JsCapabilities.BinaryData"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The test and the read are one member because every call site asks them together and because
    /// they are one question to an engine.</b> "Is this an <c>ArrayBuffer</c>" has no JS-visible
    /// answer - <c>byteLength</c> is answered by a <c>DataView</c> and by every typed array, a
    /// prototype is settable and a <c>Symbol.toStringTag</c> is writable - so a provider answers it by
    /// brand, and the brand check is the same operation that produces the bytes. A separate boolean
    /// would be a claim with nothing to do, paid for twice.
    /// </para>
    /// <para>
    /// <b>A <c>SharedArrayBuffer</c> is not one of these.</b> WebIDL's <c>BufferSource</c> is an
    /// <c>ArrayBuffer</c> or a view over one, and a shared buffer is neither. A provider whose engine
    /// implements the shared one as a subclass of the ordinary one has to exclude it explicitly, and
    /// one of them does.
    /// </para>
    /// <para>
    /// <b>A detached buffer answers <see langword="true"/> with no bytes.</b> That is the File API's
    /// own reading - a detached <c>BufferSource</c> contributes an empty byte sequence rather than
    /// failing - and it keeps a detached buffer a zero-length blob instead of a stringified
    /// <c>"[object ArrayBuffer]"</c>.
    /// </para>
    /// <para>
    /// <b>A view needs no member of its own.</b> A typed array's or a <c>DataView</c>'s
    /// <c>buffer</c>, <c>byteOffset</c> and <c>byteLength</c> are ordinary property reads, so a caller
    /// walks to the buffer with <see cref="IJsMembers.GetProperty"/> and asks this about what it
    /// finds.
    /// </para>
    /// </remarks>
    /// <param name="bytes">
    /// A snapshot the caller owns; empty when the answer is <see langword="false"/> or the buffer is
    /// detached.
    /// </param>
    bool TryGetArrayBufferBytes(JsValue value, [NotNullWhen(true)] out byte[]? bytes);

    /// <summary>
    /// ECMAScript <c>ToString</c>. Enters the engine, and may run page script or throw.
    /// </summary>
    string ToJsString(JsValue value);

    /// <summary>
    /// ECMAScript <c>ToNumber</c>. Enters the engine, and may run page script or throw.
    /// </summary>
    double ToNumber(JsValue value);

    /// <summary>
    /// ECMAScript <c>ToBoolean</c>. Runs no page script.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For every kind but one this is <see cref="JsValue.AsBoolean"/>, and a caller that knows its
    /// value cannot be that kind should keep using the handle.</b> The exception is
    /// <see cref="JsValueKind.BigInt"/>: <c>ToBoolean(0n)</c> is <see langword="false"/>, the handle's
    /// switch sends every kind above <see cref="JsValueKind.String"/> to <see langword="true"/>, and the
    /// handle carries a BigInt as a reference it cannot look inside. A site reading a value the page
    /// supplied â€” an argument, a member of a dictionary the page wrote â€” can be handed one.
    /// </para>
    /// <para>
    /// <b>A provider decides that kind however its engine lets it, and may answer every other kind
    /// from the handle without a crossing.</b> What it may not do is answer <see langword="true"/> for a
    /// zero BigInt. A provider whose engine has no BigInt cannot be handed one by its own realm, and
    /// refuses one as it refuses any handle another engine minted.
    /// </para>
    /// </remarks>
    bool ToBoolean(JsValue value);
}

/// <summary>
/// Installing, reading and removing an object's members, and its prototype link.
/// </summary>
public interface IJsMembers
{
    /// <summary>Installs a data property.</summary>
    void DefineValue(JsValue target, string name, JsValue value, JsPropertyFlags flags = JsPropertyFlags.Default);

    /// <summary>
    /// Installs an accessor property. A <see langword="null"/> <paramref name="setter"/> makes it
    /// read-only, which is how the bridge expresses a read-only IDL attribute (216 sites on 2026-09-08).
    /// </summary>
    void DefineAccessor(JsValue target, string name, JsNativeFunction getter, JsNativeFunction? setter, JsPropertyFlags flags = JsPropertyFlags.Default);

    /// <summary>Installs an integer-indexed data property.</summary>
    void DefineIndex(JsValue target, uint index, JsValue value, JsPropertyFlags flags = JsPropertyFlags.Default);

    /// <summary>Reads a property, following the prototype chain. May run a getter the page wrote.</summary>
    JsValue GetProperty(JsValue target, string name);

    /// <summary>Reads an integer-indexed property, following the prototype chain.</summary>
    JsValue GetIndex(JsValue target, uint index);

    /// <summary>Writes a property. May run a setter the page wrote.</summary>
    void SetProperty(JsValue target, string name, JsValue value);

    /// <summary>Whether the property exists, own or inherited.</summary>
    bool HasProperty(JsValue target, string name);

    /// <summary>Deletes an own property, answering whether it is now absent.</summary>
    bool DeleteProperty(JsValue target, string name);

    /// <summary>The object's own enumerable string-keyed property names, in property-creation order.</summary>
    IReadOnlyList<string> OwnPropertyNames(JsValue target);

    /// <summary>
    /// Points <paramref name="target"/>'s prototype chain at <paramref name="prototype"/> â€” how a DOM
    /// wrapper is linked to its interface so that <c>Object.getPrototypeOf(el) === Element.prototype</c>
    /// and <c>el.constructor.name</c> answer the interface rather than <c>Object</c>.
    /// </summary>
    void SetPrototype(JsValue target, JsValue prototype);

    /// <summary>The object's prototype, or <see cref="JsValue.Null"/>.</summary>
    JsValue GetPrototype(JsValue target);
}

/// <summary>
/// Calling into JavaScript, and raising a JavaScript error from host code.
/// </summary>
public interface IJsCalls
{
    /// <summary>Calls a function.</summary>
    JsValue Invoke(JsValue function, JsValue thisValue, ReadOnlySpan<JsValue> arguments = default);

    /// <summary>Calls a constructor with <c>new</c>.</summary>
    JsValue Construct(JsValue constructor, ReadOnlySpan<JsValue> arguments = default);

    /// <summary>
    /// The exception to <see langword="throw"/> so that JavaScript sees an error of
    /// <paramref name="kind"/> with <paramref name="message"/>.
    /// </summary>
    /// <remarks>
    /// It returns rather than throws so that a callback body reads <c>throw realm.Error(â€¦)</c>, which
    /// tells the compiler the path ends and the reader that the throw is deliberate â€” a helper that
    /// threw would leave the compiler thinking control continued.
    /// </remarks>
    Exception Error(JsErrorKind kind, string message);

    /// <summary>
    /// The DOM exception to <see langword="throw"/> so that JavaScript sees a <c>DOMException</c> with
    /// the given <c>name</c> â€” <c>NotFoundError</c>, <c>HierarchyRequestError</c>, and the rest.
    /// </summary>
    Exception DomError(string name, string message);
}

/// <summary>
/// The realm's job queue: the promise reactions and <c>queueMicrotask</c> callbacks that run between
/// one piece of script and the next.
/// </summary>
/// <remarks>
/// <b>Pull, not push.</b> The host drives this â€” it decides when a microtask checkpoint happens,
/// because in a browser that decision belongs to the event loop and not to the engine. Broiler.JS
/// pushes instead, through a <c>SynchronizationContext</c> captured when the realm is built, and the
/// provider is what turns that into the pull shape here. An engine with an explicit
/// "drain the job queue" entry point implements this directly.
/// </remarks>
public interface IJsJobs
{
    /// <summary>Queues a host callback as a microtask.</summary>
    void EnqueueJob(Action job);

    /// <summary>
    /// Runs queued jobs until the queue is empty or <paramref name="limit"/> have run, answering how
    /// many ran. A job that queues another is followed, which is why there is a limit at all.
    /// </summary>
    int DrainJobs(int limit = 10_000);

    /// <summary>Whether any job is queued.</summary>
    bool HasPendingJobs { get; }

    /// <summary>
    /// A new pending promise, with the two functions that settle it.
    /// </summary>
    /// <remarks>
    /// Handing back <paramref name="resolve"/> and <paramref name="reject"/> rather than taking an
    /// executor is the shape the bridge actually needs: its one deferred promise
    /// (<c>customElements.whenDefined</c>) captures the resolve function out of the executor and
    /// stores it, which only works because Broiler.JS happens to run the executor synchronously.
    /// Depending on that is depending on an engine's scheduling; returning the pair does not.
    /// </remarks>
    JsValue NewPromise(out Action<JsValue> resolve, out Action<JsValue> reject);
}

/// <summary>
/// Turning JavaScript source into something that runs â€” and the distinction between the three
/// different reasons a browser does that.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE AXIS IS WHICH CONTENT-SECURITY-POLICY DIRECTIVE GOVERNS THE SOURCE, NOT WHOSE TEXT IT IS.</b>
/// That is the correction this interface's three members exist to carry, and it was learned the hard
/// way: two call sites in the bridge reached opposite conclusions about the same kind of text, one
/// arguing from the directive and one from provenance, and each was half right. A script element is
/// governed by <c>script-src</c> â€” per script, satisfied by <c>'unsafe-inline'</c>, a matching nonce
/// or a matching hash. <c>eval</c> and <c>new Function</c> are governed by <c>'unsafe-eval'</c> â€” per
/// realm. Those are different decisions, taken by different code, at different times. A page served
/// <c>script-src 'unsafe-inline'</c> runs every one of its script elements and no <c>eval</c>; a page
/// served <c>script-src 'nonce-x' 'unsafe-eval'</c> is the other way round. A contract with one
/// member for both cannot express either page, which is why there are three.
/// </para>
/// <para>
/// <b>Host script is not the page's source, and conflating the two is what makes a second engine
/// look impossible.</b> The bridge itself authors JavaScript, and <c>EvaluateHostScript</c> runs it
/// throughout <c>Broiler.HtmlBridge.Dom</c>: two calls run the embedded <c>.js</c> assets, 1,891
/// lines between them, and most of the rest install a polyfill or an interface object, or probe for
/// a global. That source is written by this repository, ships with it, and is not subject to the
/// page's Content-Security-Policy. A dynamic <c>import()</c> is none of the three members: it is
/// <see cref="JsCapabilities.DynamicImport"/>, and <see cref="JsCapabilities.GuestEval"/> does not
/// gate it.
/// </para>
/// <para>
/// An engine with no run-time compiler could support host script by compiling the bridge's own
/// JavaScript when the engine is built, and lack the other two, because a page's text is not
/// knowable then. Declaring them separately is what lets a provider say so.
/// <c>VmEngineProvider</c> is not that engine: all three members compile at run time, through the
/// artifact provider it registers for every realm, and a forbidden <c>eval</c> is refused inside
/// that provider rather than by registering none. (This used to say Broiler.VM refuses by
/// registering no artifact provider; that is <c>VmScriptEngine</c>'s shape, not this provider's.)
/// </para>
/// </remarks>
public interface IJsSource
{
    /// <summary>
    /// Runs JavaScript this repository authored. Not subject to the page's content policy.
    /// </summary>
    /// <param name="source">The script text.</param>
    /// <param name="label">
    /// A name for the evaluation â€” <c>polyfill:streams</c>, <c>probe:global-this</c>. Broiler.JS
    /// compiles it as the script's file path, which stack frames report and its code cache keys on;
    /// Broiler.VM names it only in the error thrown when the realm has no <c>eval</c> intrinsic.
    /// </param>
    JsValue EvaluateHostScript(string source, string label);

    /// <summary>
    /// Runs a CLASSIC SCRIPT the page carries â€” a script element's text, a sub-document's, a worker's
    /// top-level script, an <c>importScripts</c> body, the wrapper compiled from an event-handler
    /// content attribute.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"Classic script" is the specification's term and it is chosen for what it EXCLUDES.</b> Its
    /// definition does not cover <c>eval</c> or <c>new Function</c>, so an implementer reading the
    /// name alone cannot route those here. <c>EvaluatePageScript</c> was considered and rejected: "the
    /// page's source" is equally true of the text handed to <c>eval</c>, which is the exact confusion
    /// these three members exist to remove.
    /// </para>
    /// <para>
    /// <b>THE CALLER HAS ALREADY TAKEN THE <c>script-src</c> DECISION, AND THIS CONTRACT CANNOT CHECK
    /// THAT IT DID.</b> Stated as an obligation because it cannot be made a parameter:
    /// <c>Broiler.JSeal</c> has no <c>ProjectReference</c> and no <c>PackageReference</c>
    /// at all â€” its engine-neutrality is a compiler outcome rather than a convention â€” so it cannot
    /// name a policy type; and the decision is per script and content-dependent, so it could not be a
    /// realm-shaped option even if the type were reachable. A token minted by the policy layer was
    /// considered and rejected: anything that can call the minter can forge one, so it buys ceremony
    /// rather than enforcement.
    /// </para>
    /// <para>
    /// <b>The worker path does not meet that obligation today.</b> <c>JSWorker</c> hands over a
    /// worker's top-level script and each <c>importScripts</c> body with no
    /// Content-Security-Policy consulted, and a worker's top-level script is governed by
    /// <c>worker-src</c>, which falls back through <c>child-src</c> and <c>script-src</c> to
    /// <c>default-src</c>; this repository's policy parser reads neither of the first two.
    /// </para>
    /// <para>
    /// Throws when the realm was not built with <see cref="JsCapabilities.ClassicScriptSource"/>.
    /// That is an ABILITY and not a permission: it says the engine can compile text it did not see
    /// when it was built, which an ahead-of-time engine may honestly lack.
    /// </para>
    /// </remarks>
    JsValue EvaluateClassicScript(string source, string label);

    /// <summary>
    /// Runs JavaScript the page asked to evaluate AT RUN TIME, on the page's behalf â€” what
    /// <c>eval</c> and <c>new Function</c> ask for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Throws when the realm was not built with <see cref="JsCapabilities.GuestEval"/> â€” which is what
    /// a page whose policy forbids evaluation gets, and is a contract outcome the page may catch
    /// rather than a check the engine performs.
    /// </para>
    /// <para>
    /// <b>It is narrower than its name once suggested.</b> A page's script ELEMENT is not this: it is
    /// <see cref="EvaluateClassicScript"/>, governed by a different directive, and routing one here
    /// would refuse a page that every browser runs. The provider also enforces this permission where
    /// a browser does â€” inside the realm, on the page's own <c>eval</c> and <c>Function</c> â€” so a
    /// host that never calls this member still gets the policy it asked for.
    /// </para>
    /// </remarks>
    JsValue EvaluateDynamicSource(string source, string label);
}

