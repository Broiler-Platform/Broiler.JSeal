namespace Broiler.JSeal;

/// <summary>
/// One call from JavaScript into the host: the receiver, the arguments, the realm the call is running
/// in, and â€” for a construct call â€” <c>new.target</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The realm is on the call, and that is load-bearing.</b> The obvious alternative is an ambient
/// <c>[ThreadStatic] Js.Current</c>, which reads better at every call site (917 on 2026-09-08) and is
/// wrong here: three threads run one page's JavaScript. <c>ScriptEngine</c> installs a
/// synchronization context before it builds the realm precisely because promise and generator
/// continuations were resuming on the thread pool, <c>BrowserEventLoop</c>'s queues are all
/// <c>ConcurrentDictionary</c> for the same reason, and a Worker builds a second realm on a thread of
/// its own. An ambient realm turns every one of those into a null reference at run time that no
/// compiler can see â€” and it would do so in code migrated file-by-file, where a missing realm is
/// exactly the mistake a reviewer cannot spot.
/// </para>
/// <para>
/// <b>It is a <see langword="ref"/> struct over a span the provider fills.</b> Nothing is allocated
/// per call: the provider's trampoline copies argument handles into an inline buffer on its own stack
/// frame and hands a span of it here. That keeps the shape of Broiler.JS's <c>in Arguments</c> â€” which
/// is itself a readonly struct with four inline slots â€” while being expressible by a provider whose
/// engine hands arguments over as a C array or a value stack.
/// </para>
/// <para>
/// <b>An index past the end is <see cref="JsValue.Missing"/>, not <c>undefined</c>.</b> Broiler.JS's
/// indexer answers a CLR <see langword="null"/> there, and the bridge's arity-sensitive operations â€”
/// <c>scrollTo()</c> versus <c>scrollTo(undefined)</c>, <c>toggle(name)</c> versus
/// <c>toggle(name, force)</c> â€” depend on the difference. Making it a kind rather than a nullable
/// keeps that distinction without exporting CLR null into the argument reads (598 on 2026-09-08).
/// </para>
/// </remarks>
public readonly ref struct JsCall
{
    private readonly ReadOnlySpan<JsValue> _arguments;

    /// <summary>Builds a call frame. Providers only.</summary>
    public JsCall(IJsRealm realm, JsValue thisValue, ReadOnlySpan<JsValue> arguments, JsValue newTarget = default)
    {
        Realm = realm ?? throw new ArgumentNullException(nameof(realm));
        This = thisValue;
        _arguments = arguments;
        NewTarget = newTarget;
    }

    /// <summary>The realm this call is running in. Never <see langword="null"/>.</summary>
    public IJsRealm Realm { get; }

    /// <summary>The receiver â€” <c>this</c> inside the called function.</summary>
    public JsValue This { get; }

    /// <summary>
    /// <c>new.target</c> for a construct call, and <see cref="JsValue.Missing"/> for an ordinary one.
    /// </summary>
    /// <remarks>
    /// Broiler.JS's <c>Arguments</c> has no such member, so that provider reads <c>new.target</c>
    /// from the engine itself; declaring it here lets a provider supply it directly. Custom-element
    /// construction still passes <c>new.target</c> as argument zero from the bridge's JavaScript
    /// shim, by choice (see <c>CustomElementsBinding</c>). (This said the missing member was the
    /// reason, and that the Broiler.JS provider would keep the shim as its own business.)
    /// </remarks>
    public JsValue NewTarget { get; }

    /// <summary>How many arguments were supplied.</summary>
    public int Length => _arguments.Length;

    /// <summary>
    /// The argument at <paramref name="index"/>, or <see cref="JsValue.Missing"/> when fewer were
    /// supplied. Never throws for an out-of-range index; see the remarks on this type.
    /// </summary>
    public JsValue this[int index] =>
        (uint)index < (uint)_arguments.Length ? _arguments[index] : JsValue.Missing;

    /// <summary>Every supplied argument, for the operations that forward them on unchanged.</summary>
    public ReadOnlySpan<JsValue> Arguments => _arguments;
}

/// <summary>
/// A host function callable from JavaScript.
/// </summary>
/// <remarks>
/// The shape mirrors Broiler.JS's <c>JSFunctionDelegate</c> (<c>JSValue F(in Arguments a)</c>) closely
/// enough that migrating a callback body is a change of vocabulary rather than of structure, which is
/// what makes a 250-file port something parallel agents can do against a build gate. Returning
/// <see cref="JsValue.Missing"/> is treated as returning <c>undefined</c>, so a body that falls off
/// the end of a <c>void</c>-shaped operation does the right thing.
/// </remarks>
public delegate JsValue JsNativeFunction(in JsCall call);

