using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Engine.Core;
using Broiler.JavaScript.Runtime;

namespace Broiler.JSeal.BroilerJs;

/// <summary>
/// One Broiler.JS realm behind the JSEAL contracts: a <c>JSContext</c>, the job pump that turns the
/// engine's push-shaped scheduling into the pull shape <see cref="IJsJobs"/> asks for, and the
/// trampoline that lets a <see cref="JsNativeFunction"/> be called by the engine.
/// </summary>
/// <remarks>
/// <para>
/// <b>The class is split by capability, one file per interface</b> â€” <c>.Values</c>, <c>.Members</c>,
/// <c>.Calls</c>, <c>.Jobs</c>, <c>.Source</c> â€” for the reason <c>IJsRealmCapabilities.cs</c> gives
/// for splitting the contract: a reader asking what this engine must do to serve a DOM gets five
/// answerable questions rather than one surface of forty members. This file carries what all five
/// share: the context, the ambient-realm scope they enter, and the trampoline.
/// </para>
/// <para>
/// <b>Every public member enters the realm before touching the engine.</b> Broiler.JS resolves a
/// realm's intrinsics from a thread-static â€” <c>new JSObject()</c> reads the current context's
/// <c>Object.prototype</c>, <c>new JSFunction(â€¦)</c> reads its <c>Function.prototype</c> â€” so an
/// operation performed with no context current mints an object with a null prototype and no error.
/// That is precisely the ambient-state hazard <c>JsCall</c> keeps off the contract by putting the
/// realm on the call; it does not go away underneath, so the provider is where it is paid for, once
/// per entry, rather than left to a caller who cannot see it.
/// </para>
/// </remarks>
internal sealed partial class BroilerJsRealm : IJsRealm
{
    private readonly BroilerJsEngineProvider _provider;
    private readonly JSContext _context;
    private readonly JsCapabilities _capabilities;
    private readonly bool _allowGuestEval;
    private readonly bool _forceStrictMode;
    private readonly bool _ownsContext;
    private bool _disposed;

    internal BroilerJsRealm(BroilerJsEngineProvider provider, JsRealmOptions options)
    {
        _provider = provider;
        _allowGuestEval = options.AllowGuestEval;
        _forceStrictMode = options.ForceStrictMode;
        _ownsContext = true;

        // The pump has to exist before the context does: JSContext captures the synchronization
        // context it was handed (or the thread's) in its constructor, and a promise created later
        // captures whatever the context captured. Building the context first and installing a pump
        // afterwards would leave every promise made in between reporting to the thread pool â€” which
        // is the defect MicroTaskSynchronizationContext was written to fix, reintroduced one layer
        // down.
        _jobs = new JobQueue();
        _pump = new JobPump(_jobs);
        _context = new JSContext(_pump);

        // A realm is never wider than its provider and may be narrower: a page whose
        // Content-Security-Policy forbids evaluation gets a realm without GuestEval from an engine
        // that has it. That is the whole of what narrowing means here, because it is the only option
        // the host passes that removes a capability rather than changing one.
        _capabilities = options.AllowGuestEval
            ? provider.Capabilities
            : provider.Capabilities & ~JsCapabilities.GuestEval;

        // AND THE POLICY IS ENFORCED WHERE A BROWSER ENFORCES IT: INSIDE THE REALM, AT THE PAGE'S OWN
        // eval AND Function.
        //
        // Refusing at EvaluateDynamicSource alone refuses a door no page walks through. A page does not
        // call a host member; it writes eval('...') or new Function('...'), and until this line a
        // realm built without guest evaluation ran both. The narrowed capability was true and
        // unenforced -- which is the worst shape a capability can have, because a host is entitled to
        // branch on one without verifying it.
        //
        // JSContext.EvalEvent is the engine's own hook, raised where the specification asks the host
        // before compiling a string: direct eval, the global eval, CreateDynamicFunction at every
        // arity -- the SHARED implementation for every function kind, so the async, generator and
        // async-generator constructors are covered by the same subscription rather than by three more
        // of them -- and ShadowRealm.prototype.evaluate, raised on the context that constructed the
        // ShadowRealm before its child realm compiles. It is NOT fired by JSContext's own evaluation
        // entry point, which is what the host members reach, so this refuses the page without touching
        // anything this repository runs.
        //
        // Two routes used to get past it, and both are closed in the engine: a dynamic function built
        // from no arguments returned before the dispatch, and ShadowRealm.prototype.evaluate never
        // raised it. What remains is code already running INSIDE a ShadowRealm, which dispatches on the
        // child context nothing subscribes to. It is unreachable while evaluate is refused and
        // importValue is unimplemented, and has to be revisited when importValue loads modules.
        // AnArgumentlessDynamicFunctionIsRefusedInARealmThatForbidsGuestEvaluation and
        // ShadowRealmEvaluatesNothingInARealmThatForbidsGuestEvaluation pin both routes.
        //
        // Replacing the eval and Function globals was considered and rejected: Function.prototype
        // .constructor reaches the compiler without either binding, so the stub would be a fence with
        // a gate beside it -- the shape ScriptEngine's own eval stub has (RegisterRuntimeExtensions).
        if (!options.AllowGuestEval)
            _context.EvalEvent += RefuseGuestCompilation;
    }

    /// <summary>
    /// The refusal a page meets when its policy forbids evaluation.
    /// </summary>
    /// <remarks>
    /// <b>A <c>SyntaxError</c>, chosen to agree with the other provider rather than on its own
    /// merits.</b> Broiler.VM maps a refused compilation to <c>SyntaxError</c> and argues the choice
    /// from test262; a host that saw <c>EvalError</c> from one engine and <c>SyntaxError</c> from the
    /// other would have a difference no page should be able to observe. Since this handler chooses
    /// the error it raises, it chooses the one already argued for.
    /// </remarks>
    private static void RefuseGuestCompilation(object? sender, EvalEventArgs e) =>
        throw JSEngine.NewSyntaxError(
            "this realm was built without guest evaluation: its Content-Security-Policy forbids "
            + "'unsafe-eval', so eval, the Function constructors and ShadowRealm.prototype.evaluate compile nothing");

    /// <summary>
    /// Wraps a <c>JSContext</c> the host already built, without taking ownership of it. See
    /// <see cref="IJsRealmAdoption"/> for why this exists and how long it is expected to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An adopted realm's job queue sees only what the host puts in it.</b> A realm this provider
    /// created hands its pump to the <c>JSContext</c> constructor, so every promise made in it
    /// captures that pump and its reactions land where <see cref="DrainJobs"/> can run them. A context
    /// built elsewhere captured whatever was current when <em>it</em> was constructed â€” in this
    /// repository, <c>ScriptEngine</c>'s <c>MicroTaskSynchronizationContext</c> â€” and that cannot be
    /// changed afterwards. So <see cref="EnqueueJob"/> and <see cref="DrainJobs"/> work, and are the
    /// only jobs this realm knows about; the engine-scheduled reactions stay with the host's own
    /// queue, which is the one already draining them. Two queues is the correct count while the
    /// realm is adopted, and it goes to one when the bridge's realm is created rather than adopted.
    /// (This tied that to the bridge's two halves; the adoption is <c>Attach</c>'s, not a binding's.)
    /// </para>
    /// </remarks>
    internal BroilerJsRealm(BroilerJsEngineProvider provider, JSContext context, JsRealmOptions options)
    {
        _provider = provider;

        // THESE TWO WERE HARDCODED, AND THAT IS WHY NONE OF THE POLICY WORK REACHED A PAGE.
        //
        // A browser's realm is always adopted -- the host builds the context, the bridge wraps it --
        // so a constructor that invented a permissive policy meant every narrowing a host could
        // express was discarded on exactly the path that matters. A realm built by CreateRealm
        // honoured AllowGuestEval; the one a page actually ran in did not, and the difference was
        // invisible because nothing asked an adopted realm what it allowed.
        _allowGuestEval = options.AllowGuestEval;
        _forceStrictMode = options.ForceStrictMode;
        _ownsContext = false;

        _jobs = new JobQueue();
        _pump = new JobPump(_jobs);
        _context = context;

        // Narrowed the same way a created realm is: a realm is never wider than its provider and may
        // be narrower. Answering the provider's full set here would have been a capability declared
        // where it is not true, which is the one thing IJsEngineProvider says a narrowing exists to
        // prevent.
        _capabilities = options.AllowGuestEval
            ? provider.Capabilities
            : provider.Capabilities & ~JsCapabilities.GuestEval;

        // And the refusal a page meets, on the same terms as a created realm. Without this the
        // capability above would be narrowed and unenforced, which is the shape this whole sequence
        // exists to remove.
        if (!options.AllowGuestEval)
            _context.EvalEvent += RefuseGuestCompilation;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The <c>JSContext</c> itself, because under this engine <c>window</c> <em>is</em> the context:
    /// it derives from <c>JSObject</c>, so it is a <c>JSValue</c>, and a top-level <c>var</c> becomes
    /// one of its properties. That is what <see cref="JsCapabilities.GlobalIsVariableScope"/> asserts,
    /// and the reason this member can be a plain wrap rather than a lookup.
    /// </remarks>
    public JsValue Global => BroilerJsMarshal.Wrap(_context);

    /// <inheritdoc />
    public JsCapabilities Capabilities => _capabilities;

    /// <inheritdoc />
    public string EngineName => _provider.Name;

    /// <summary>The engine's realm, for the provider's own files.</summary>
    internal JSContext Context => _context;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Jobs queued but never drained belong to a realm that is going away; running them now would
        // execute page script against a document the host has already finished with.
        _jobs.Clear();

        // Unsubscribed before the context goes, and unconditionally: -= on a handler that was never
        // added is a no-op, so this needs no second reading of the option that decided it.
        _context.EvalEvent -= RefuseGuestCompilation;

        // An adopted context belongs to the host that built it, and that host disposes it â€” in this
        // repository, InteractiveSession, which tears the bridge down first and then disposes the
        // context. Disposing it from here would tear it down underneath whatever is still using it.
        if (_ownsContext)
            _context.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    // â”€â”€ the ambient realm â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Makes this realm â€” and its job pump â€” current on the calling thread until the returned scope
    /// is disposed. See the remarks on the class for why every entry point needs it.
    /// </summary>
    /// <remarks>
    /// <b>The pump is installed only for a realm this provider created, and that asymmetry is a
    /// correctness fix rather than an economy.</b> The engine routes a promise reaction to
    /// <c>SynchronizationContext.Current</c> when it is an <c>IJSJobPump</c>
    /// (<c>JSContext.PostJob</c>, case 1), ahead of the context's own microtask queue. For a realm
    /// this provider built that is exactly right: the pump is the one the <c>JSContext</c> was
    /// constructed with, and <see cref="DrainJobs"/> is what empties it. For an <em>adopted</em>
    /// context it is exactly wrong â€” the host built that context with its own scheduling
    /// (<c>MicroTaskSynchronizationContext</c> here) and drains that queue, so installing this pump
    /// over it for the duration of every contract call diverted any reaction created inside a JSEAL
    /// call into a queue nothing in the process empties. A page whose event listener resolved a
    /// promise would simply never see the reaction run, with no error anywhere. So an adopted realm
    /// leaves the thread's synchronization context alone and keeps its own queue for the jobs a host
    /// hands it through <see cref="EnqueueJob"/>, which is what its constructor already promises.
    /// </remarks>
    private RealmScope Enter()
    {
        ThrowIfDisposed();
        return new RealmScope(_context, _ownsContext ? _pump : null);
    }

    /// <remarks>
    /// A struct rather than a class because it is taken once per contract call, including per
    /// property read on a wrapper, and an allocation there would be the cost of the abstraction
    /// rather than of the work. Restoring the previous values rather than clearing them is what makes
    /// it nest: a host call re-entering the engine takes this scope again and must not tear down the
    /// outer one on the way out.
    /// </remarks>
    private readonly struct RealmScope : IDisposable
    {
        private readonly IJSContext? _previousContext;
        private readonly SynchronizationContext? _previousPump;
        private readonly bool _contextChanged;
        private readonly bool _pumpChanged;

        /// <param name="pump">
        /// The job pump to make current, or <see langword="null"/> to leave the thread's own
        /// synchronization context in place â€” see the remarks on <see cref="Enter"/>.
        /// </param>
        internal RealmScope(JSContext context, SynchronizationContext? pump)
        {
            _previousContext = JSEngine.Current;
            _contextChanged = !ReferenceEquals(context, _previousContext);
            if (_contextChanged)
                JSEngine.CurrentContext = context;

            _previousPump = SynchronizationContext.Current;
            _pumpChanged = pump is not null && !ReferenceEquals(pump, _previousPump);
            if (_pumpChanged)
                SynchronizationContext.SetSynchronizationContext(pump);
        }

        public void Dispose()
        {
            if (_pumpChanged)
                SynchronizationContext.SetSynchronizationContext(_previousPump);

            if (_contextChanged)
                JSEngine.CurrentContext = _previousContext;
        }
    }

    // â”€â”€ the trampoline â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// How many arguments a call carries before the trampoline has to reach for the heap. Eight
    /// covers every operation the DOM bridge installs â€” the widest WebIDL operation it binds takes
    /// six â€” and doubles Broiler.JS's own four inline <c>Arguments</c> slots, so an argument list the
    /// engine already spilled to an array is not spilled twice.
    /// </summary>
    private const int InlineArgumentCapacity = 8;

    /// <summary>
    /// The trampoline's stack buffer.
    /// </summary>
    /// <remarks>
    /// <see cref="InlineArrayAttribute"/> rather than a <c>stackalloc</c> because
    /// <see cref="JsValue"/> holds a managed reference and so cannot live in unmanaged stack memory;
    /// an inline array is the shape that gives a fixed-size, GC-tracked buffer inside the frame.
    /// </remarks>
    [InlineArray(InlineArgumentCapacity)]
    private struct ArgumentBuffer
    {
#pragma warning disable IDE0051 // The single field IS the array; an inline array declares no more.
        private JsValue _element0;
#pragma warning restore IDE0051
    }

    /// <summary>
    /// Turns one engine call into one <see cref="JsCall"/> and runs the host body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing is allocated on the common path.</b> The argument handles are copied into an inline
    /// buffer in this frame and handed on as a span of it, which is the shape <c>JsCall</c> was
    /// declared a <see langword="ref"/> struct to permit. A call with more arguments than the buffer
    /// holds rents an array rather than allocating one, and clears it before returning it â€” a
    /// <see cref="JsValue"/> can hold a reference to a DOM wrapper, and a pooled array is exactly the
    /// kind of long-lived root that would keep a document alive after the page that owned it went
    /// away.
    /// </para>
    /// <para>
    /// <b>An argument past the end is <see cref="JsValue.Missing"/>, not <c>undefined</c>.</b> The
    /// buffer is filled from <c>Arguments</c>'s indexer, which answers a CLR null there, and
    /// <see cref="BroilerJsMarshal.Wrap"/> is what preserves the difference. <c>a.GetAt(i)</c> would
    /// have been the shorter spelling and would have coerced the distinction away.
    /// </para>
    /// <para>
    /// <b>Nothing is caught.</b> A host body that throws â€” a WebIDL TypeError, a DOMException, a bug â€”
    /// must propagate into the engine so it becomes a JavaScript exception the page can catch, as
    /// it did from the <c>DomFunction</c> this replaced. A <see langword="catch"/> here would
    /// turn every one of those into a returned <c>undefined</c>, silently.
    /// </para>
    /// </remarks>
    private JSValue Dispatch(JsNativeFunction body, bool construct, in Arguments a)
    {
        using var scope = Enter();

        // Arguments has no new.target of its own, and the engine keeps it in TWO places that have to
        // be read in order. JSEngine.NewTarget resolves the interpreter's frame stack, which is the
        // right answer for a constructor written in JavaScript; a host constructor's body runs as a
        // CLR delegate and pushes no frame, so that read is null and only the execution context's
        // CurrentNewTarget â€” set by [[Construct]] immediately before it invokes the delegate
        // (JSFunction.cs:860) â€” has the value; ObjectClassFactory.cs:40 reads just that. The engine's
        // NewTargetPrototype reads both in the same order (EngineAssemblyInitializer.cs:70-71).
        //
        // Reading only the first is what made JsCall.NewTarget always Missing in a host constructor,
        // which the JSEAL conformance suite caught. An ordinary call reports Missing rather than
        // undefined, as JsCall.NewTarget specifies.
        var newTarget = construct
            ? BroilerJsMarshal.Wrap(JSEngine.NewTarget ?? _context.CurrentNewTarget)
            : JsValue.Missing;

        // A receiver is not an argument: Arguments leaves This null for a call made with none, and
        // the language calls that `undefined` rather than "not supplied".
        var thisValue = a.This is null ? JsValue.Undefined : BroilerJsMarshal.Wrap(a.This);

        var count = a.Length;
        if (count <= InlineArgumentCapacity)
        {
            var buffer = default(ArgumentBuffer);
            for (var i = 0; i < count; i++)
                buffer[i] = BroilerJsMarshal.Wrap(a[i]);

            var inline = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<ArgumentBuffer, JsValue>(ref buffer), count);

            var inlineCall = new JsCall(this, thisValue, inline, newTarget);
            return BroilerJsMarshal.Unwrap(body(in inlineCall));
        }

        var rented = ArrayPool<JsValue>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < count; i++)
                rented[i] = BroilerJsMarshal.Wrap(a[i]);

            var call = new JsCall(this, thisValue, rented.AsSpan(0, count), newTarget);
            return BroilerJsMarshal.Unwrap(body(in call));
        }
        finally
        {
            Array.Clear(rented, 0, count);
            ArrayPool<JsValue>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// The engine-facing delegate for one host function.
    /// </summary>
    /// <remarks>
    /// A class with an instance method rather than a lambda closing over the realm: the delegate is
    /// built once per host function and is then held for the life of the wrapper, so what matters is
    /// that the shape is explicit â€” a reader can see that the realm, the body and the call kind are
    /// the whole of what a trampoline captures, which a compiler-generated closure would not say.
    /// </remarks>
    private sealed class CallSite(BroilerJsRealm realm, JsNativeFunction body, bool construct)
    {
        internal JSValue Invoke(in Arguments a) => realm.Dispatch(body, construct, in a);
    }

    /// <summary>The engine delegate that runs <paramref name="body"/> as an ordinary call.</summary>
    private JSFunctionDelegate MethodTrampoline(JsNativeFunction body) =>
        new CallSite(this, body, construct: false).Invoke;

    /// <summary>The engine delegate that runs <paramref name="body"/> as a construct call.</summary>
    private JSFunctionDelegate ConstructorTrampoline(JsNativeFunction body) =>
        new CallSite(this, body, construct: true).Invoke;
}

