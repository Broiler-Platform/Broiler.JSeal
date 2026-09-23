using System.Diagnostics.CodeAnalysis;

namespace Broiler.JSeal;

// IJsRealm aggregates these operation interfaces; providers implement each over their engine.

/// <summary>Create realm values and perform conversions requiring a provider.</summary>
/// <remarks>
/// JsValue exposes cheap handle inspections. ToJsString and ToNumber may execute guest coercion;
/// ToBoolean and IsStrictlyEqual need the provider for an opaque BigInt's truthiness and value but do
/// not execute guest code.
/// </remarks>
public interface IJsValues
{
    /// <summary>A new ordinary object with the realm's <c>Object.prototype</c>.</summary>
    JsValue NewObject();

    /// <summary>A new Array, optionally pre-filled.</summary>
    JsValue NewArray(ReadOnlySpan<JsValue> elements = default);

    /// <summary>
    /// A new non-constructable host function — a WebIDL operation or attribute accessor.
    /// </summary>
    /// <remarks>
    /// <b>Non-constructable is the default because WebIDL says so, and because it is what makes a
    /// wrapper affordable.</b> Only interface objects are constructors; <c>el.setAttribute.prototype</c>
    /// is <c>undefined</c> and <c>new el.setAttribute()</c> throws. Under Broiler.JS this maps to
    /// <c>createPrototype: false</c>, which the bridge adopted as a memory fix as much as a
    /// correctness one — an element wrapper's members were each allocating an unreachable prototype
    /// object plus its <c>constructor</c> back-reference. Anything a page may legitimately
    /// <c>new</c> asks for <see cref="NewConstructor"/> instead, and there are sixteen of those.
    /// </remarks>
    /// <param name="name">The function's <c>name</c>.</param>
    /// <param name="body">The host code to run.</param>
    /// <param name="length">The function's declared <c>length</c> — its count of required arguments.</param>
    JsValue NewMethod(string name, JsNativeFunction body, int length = 0);

    /// <summary>
    /// A new constructable host function — an interface object a page may <c>new</c>
    /// (<c>Headers</c>, <c>Request</c>, <c>Response</c>, <c>FormData</c>, <c>Worker</c>, …).
    /// </summary>
    /// <remarks>
    /// The returned function carries a <c>prototype</c> object, reachable with
    /// <see cref="IJsMembers.GetProperty"/>, which is where an interface's members are installed.
    /// </remarks>
    JsValue NewConstructor(string name, JsNativeFunction body, int length = 0);

    /// <summary>
    /// A new object whose property lookup the host completes — a live collection, a style
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
    /// <param name="value">The value to test; any kind is accepted.</param>
    /// <param name="bytes">
    /// A snapshot the caller owns when the answer is <see langword="true"/>, empty when the buffer is
    /// detached; <see langword="null"/> when the answer is <see langword="false"/>.
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
    /// supplied — an argument, a member of a dictionary the page wrote — can be handed one.
    /// </para>
    /// <para>
    /// <b>A provider decides that kind however its engine lets it, and may answer every other kind
    /// from the handle without a crossing.</b> What it may not do is answer <see langword="true"/> for a
    /// zero BigInt. A provider whose engine has no BigInt cannot be handed one by its own realm, and
    /// refuses one as it refuses any handle another engine minted.
    /// </para>
    /// </remarks>
    bool ToBoolean(JsValue value);

    /// <summary>
    /// ECMAScript <c>IsStrictlyEqual</c> (<c>===</c>). Runs no page script.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For every kind but one this is <see cref="JsValue.op_Equality"/>, which is already
    /// <c>===</c> there:</b> NaN is unequal to itself, <c>+0</c> equals <c>-0</c>, strings compare by
    /// code unit, and symbols and objects by identity. The exception is again
    /// <see cref="JsValueKind.BigInt"/>. Two BigInts are strictly equal when they are the same
    /// mathematical integer, but the handle's operator compares the references BigInt handles carry,
    /// and whether two handles for one value share a reference is the provider's business: one may
    /// hand back the engine's own value, another a new box per crossing. So a <see langword="true"/>
    /// from the operator is reliable for a BigInt and a <see langword="false"/> is not; this member
    /// answers both. A BigInt never equals a Number here, as in the language.
    /// </para>
    /// <para>
    /// <b>The handle operator, <see cref="JsValue.Equals(JsValue)"/> and
    /// <see cref="JsValue.GetHashCode"/> keep comparing BigInt handles by reference,</b> deliberately:
    /// changing them would change the hash of every dictionary and list a host keys on a handle, and
    /// they cannot see inside a BigInt without a provider anyway. A host that needs a BigInt's value as
    /// a key should key on <see cref="ToJsString"/>'s answer.
    /// </para>
    /// <para>
    /// <b><see cref="JsValue.Missing"/> is <c>undefined</c> here:</b> it is no JavaScript value,
    /// and <c>undefined</c> is what the language has for a value nobody supplied (and what both
    /// providers make of it in <see cref="IJsClone.Clone"/>). So it is strictly equal to
    /// <see cref="JsValue.Undefined"/> and to itself, although the handle operator tells the two
    /// apart.
    /// </para>
    /// <para>
    /// A provider refuses a BigInt handle another engine minted with <see cref="JsEngineException"/>, as
    /// its <see cref="ToBoolean"/> does. A provider that predates this member and does not implement it
    /// gets this default body, which cannot look inside a handle: the operator's answer wherever that
    /// is <c>===</c>, and <see cref="NotSupportedException"/> for two distinct BigInt handles, which it
    /// cannot decide. Both registered providers implement it.
    /// </para>
    /// </remarks>
    bool IsStrictlyEqual(JsValue left, JsValue right)
    {
        if (left.IsMissing)
            left = JsValue.Undefined;

        if (right.IsMissing)
            right = JsValue.Undefined;

        if (left.Kind != JsValueKind.BigInt || right.Kind != JsValueKind.BigInt || left == right)
            return left == right;

        throw new NotSupportedException(
            "this provider does not implement IJsValues.IsStrictlyEqual, so two BigInt handles cannot be compared by value");
    }
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
    /// read-only, which is how the bridge expresses a read-only IDL attribute.
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
    /// Points <paramref name="target"/>'s prototype chain at <paramref name="prototype"/> — how a DOM
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
    /// <remarks>
    /// A handle whose <see cref="JsValue.IsFunction"/> is false is refused before any guest code runs,
    /// including a noncallable Proxy's traps: the provider throws <see cref="JsEngineException"/>
    /// carrying the realm's <c>TypeError</c>. <see cref="Construct"/> refuses the same handles. The
    /// check comes first, so a non-function handle minted by another realm of the same provider gets
    /// this <c>TypeError</c> rather than a foreign-handle refusal; either way nothing crosses.
    /// </remarks>
    JsValue Invoke(JsValue function, JsValue thisValue, ReadOnlySpan<JsValue> arguments = default);

    /// <summary>Calls a constructor with <c>new</c>.</summary>
    /// <remarks>
    /// A handle whose <see cref="JsValue.IsFunction"/> is false is refused before any guest code runs,
    /// as <see cref="Invoke"/> refuses it: <see cref="JsEngineException"/> carrying the realm's
    /// <c>TypeError</c>. A function that is not a constructor reaches the engine, which raises its own
    /// <c>TypeError</c>.
    /// </remarks>
    JsValue Construct(JsValue constructor, ReadOnlySpan<JsValue> arguments = default);

    /// <summary>
    /// The exception to <see langword="throw"/> so that JavaScript sees an error of
    /// <paramref name="kind"/> with <paramref name="message"/>.
    /// </summary>
    /// <remarks>
    /// It returns rather than throws so that a callback body reads <c>throw realm.Error(…)</c>, which
    /// tells the compiler the path ends and the reader that the throw is deliberate — a helper that
    /// threw would leave the compiler thinking control continued.
    /// </remarks>
    Exception Error(JsErrorKind kind, string message);

    /// <summary>
    /// The DOM exception to <see langword="throw"/> so that JavaScript sees a <c>DOMException</c> with
    /// the given <c>name</c> — <c>NotFoundError</c>, <c>HierarchyRequestError</c>, and the rest.
    /// </summary>
    Exception DomError(string name, string message);
}

/// <summary>
/// The realm's job queue: the promise reactions and <c>queueMicrotask</c> callbacks that run between
/// one piece of script and the next.
/// </summary>
/// <remarks>
/// <b>Pull, not push.</b> The host drives this — it decides when a microtask checkpoint happens,
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
    /// Both delegates require a live realm: after disposal they throw
    /// <see cref="ObjectDisposedException"/>, even if the promise has already settled, without
    /// reading thenable properties or scheduling reactions. The host must serialize their use
    /// with other realm operations, including disposal.
    /// </remarks>
    JsValue NewPromise(out Action<JsValue> resolve, out Action<JsValue> reject);
}

/// <summary>Separate host-authorized, classic and dynamic source evaluation.</summary>
/// <remarks>
/// EvaluateHostScript runs trusted host-authored code. The host authorizes each classic script before
/// calling EvaluateClassicScript. EvaluateDynamicSource requires GuestEval, and providers also block
/// guest eval and Function compilation when that capability is absent. Host/classic evaluation does
/// not exempt guest callbacks from this restriction. None of these methods executes module graphs.
/// <para>
/// Every <c>label</c> is a diagnostic source identity, selected by
/// <see cref="JsRealmOptions.SourceLabelFor"/> when blank. It never grants permission or changes
/// strictness. A guest throw escaping these members carries the selected identity in
/// <see cref="JsEngineException.SourceLabel"/>; a syntax error also carries its line when known.
/// Broiler.JS also gives the label to its compiler, so it appears in that engine's stack text.
/// The pinned Broiler.VM eval request carries only source bytes and always runs entry
/// <c>main</c>, so the VM provider keeps the identity in JSEAL and has no guest stack text.
/// </para>
/// </remarks>
public interface IJsSource
{
    /// <summary>Evaluate trusted host-authored source; ForceStrictMode applies here.</summary>
    /// <param name="source">JavaScript supplied by the host.</param>
    /// <param name="label">Diagnostic source identity; see the interface remarks.</param>
    JsValue EvaluateHostScript(string source, string label);

    /// <summary>Evaluate a classic script after the host has authorized it.</summary>
    /// <remarks>
    /// ClassicScriptSource describes ability to run supplied script text, independently of GuestEval.
    /// The host applies its per-script policy before calling. ForceStrictMode does not affect this member.
    /// </remarks>
    JsValue EvaluateClassicScript(string source, string label);

    /// <summary>
    /// Runs JavaScript the page asked to evaluate AT RUN TIME, on the page's behalf — what
    /// <c>eval</c> and <c>new Function</c> ask for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Throws when the realm was not built with <see cref="JsCapabilities.GuestEval"/> — which is what
    /// a page whose policy forbids evaluation gets, and is a contract outcome the page may catch
    /// rather than a check the engine performs.
    /// </para>
    /// <para>
    /// <b>It is narrower than its name once suggested.</b> A page's script ELEMENT is not this: it is
    /// <see cref="EvaluateClassicScript"/>, governed by a different directive, and routing one here
    /// would refuse a page that every browser runs. The provider also enforces this permission where
    /// a browser does — inside the realm, on the page's own <c>eval</c> and <c>Function</c> — so a
    /// host that never calls this member still gets the policy it asked for.
    /// </para>
    /// </remarks>
    JsValue EvaluateDynamicSource(string source, string label);
}

