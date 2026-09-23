using Broiler.JavaScript.BuiltIns.Promise;
using Broiler.JavaScript.Runtime;

namespace Broiler.JSeal.BroilerJs;

/// <summary>
/// <see cref="IJsJobs"/>: the realm's microtask queue, and promises the host can settle.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file is where the contract's shape and the engine's disagree, and the disagreement is the
/// provider's business.</b> <see cref="IJsJobs"/> is pull-shaped because in a browser the decision of
/// when a microtask checkpoint happens belongs to the event loop. Broiler.JS is push-shaped: it
/// resolves a promise reaction's destination through
/// <c>SynchronizationContext.Current ?? context.synchronizationContext</c> and falls back to the
/// thread pool, which runs the reaction beside the thread driving the page rather than on it. The
/// realm therefore builds its own pump, hands it to the <c>JSContext</c> at construction so every
/// promise captures it, and drains it only when the host says so.
/// </para>
/// <para>
/// The pump carries <see cref="IJSJobPump"/>, which is the engine's marker for "a context that runs
/// what it is given on the JavaScript thread, one item at a time". Without it the engine treats an
/// ambient context as untrusted and takes its own internal queue instead â€” correct, but then the
/// jobs are somewhere this realm cannot count or drain, and <see cref="HasPendingJobs"/> would answer
/// <see langword="false"/> with work outstanding.
/// </para>
/// </remarks>
internal partial class BroilerJsRealm
{
    private readonly JobQueue _jobs;
    private readonly JobPump _pump;

    /// <inheritdoc />
    public void EnqueueJob(Action job)
    {
        ArgumentNullException.ThrowIfNull(job);
        ThrowIfDisposed();
        _jobs.Enqueue(job);
    }

    /// <summary>
    /// Runs queued jobs until the queue is empty or <paramref name="limit"/> have run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A job that queues another is followed, which is what a microtask checkpoint does and why the
    /// limit exists at all: a promise chain that re-queues itself would otherwise hold the checkpoint
    /// forever, and a bounded drain lets the host notice rather than hang.
    /// </para>
    /// <para>
    /// <b>A job that throws stops the drain, and its successors stay queued.</b> Swallowing it here
    /// would lose the failure outright â€” JSEAL has no logger to report it to, by construction â€” while
    /// leaving the rest of the queue intact means the next checkpoint runs them. The host drives the
    /// drain, so the host is where the report belongs.
    /// </para>
    /// </remarks>
    public int DrainJobs(int limit = 10_000)
    {
        ThrowIfDisposed();

        // Refused before a job is dequeued: a host that drains from Resolve or LoadAsync must not lose one.
        ThrowIfInModuleHost();

        var ran = 0;
        while (ran < limit && _jobs.TryDequeue(out var job))
        {
            // Translate guest throws while leaving host exceptions and the remaining queue intact.
            // A module realm runs each job as a task on its scheduler; see BroilerJsRealm.Modules.cs.
            if (_moduleScheduler is { } scheduler)
                scheduler.RunInline(() => Execute(job, static action => action()));
            else
                Execute(job, static action => action());
            ran++;
        }

        return ran;
    }

    /// <inheritdoc />
    public bool HasPendingJobs
    {
        get
        {
            ThrowIfDisposed();
            return _jobs.Count > 0;
        }
    }

    /// <summary>
    /// A new pending promise, with the two functions that settle it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The executor runs synchronously on this engine, and capturing out of it is exactly what the
    /// contract exists to avoid depending on.</b> <c>JSPromise</c>'s delegate constructor calls the
    /// delegate before it returns, so the two locals below are assigned by the time the constructor
    /// has finished â€” which is what makes the capture safe here and nowhere else. Relying on that is
    /// relying on an engine's scheduling: the bridge's deferred promise for
    /// <c>customElements.whenDefined</c> did it in bridge code, and would have broken on an engine
    /// that ran the executor later. Handing back the pair moves that dependency into the provider,
    /// where it is a fact about Broiler.JS rather than an assumption the bridge makes about every
    /// engine it might one day run on; <c>whenDefined</c> takes its pair from here now.
    /// </para>
    /// <para>
    /// The guard afterwards is not paranoia about the current engine but about a future one: if the
    /// executor ever stopped running synchronously, this would fail loudly at the call that made the
    /// promise instead of quietly at the callback that later tried to resolve it.
    /// </para>
    /// </remarks>
    public JsValue NewPromise(out Action<JsValue> resolve, out Action<JsValue> reject)
    {
        ThrowIfDisposed();
        using var scope = Enter();

        Action<JsValue>? capturedResolve = null;
        Action<JsValue>? capturedReject = null;

        var promise = new JSPromise((engineResolve, engineReject) =>
        {
            // A settler can run thenable getters and enqueue reactions, including when another
            // realm is currently executing. It must enter this realm and use this realm's pump.
            capturedResolve = value => Execute((engineResolve, value), static s => s.engineResolve(BroilerJsMarshal.Unwrap(s.value)));
            capturedReject = value => Execute((engineReject, value), static s => s.engineReject(BroilerJsMarshal.Unwrap(s.value)));
        });

        if (capturedResolve is null || capturedReject is null)
        {
            throw new JsEngineException(
                "The Broiler.JS promise executor did not run synchronously, so the realm could not " +
                "capture its resolve/reject functions.");
        }

        resolve = capturedResolve;
        reject = capturedReject;
        return BroilerJsMarshal.Wrap(promise);
    }

    /// <summary>
    /// The realm's microtask queue: FIFO, and drained by the host rather than by the engine.
    /// </summary>
    /// <remarks>
    /// Locked rather than a <c>ConcurrentQueue</c> because <see cref="Count"/> and
    /// <see cref="TryDequeue"/> have to agree with each other for a bounded drain, and because the
    /// interesting concurrency is not two drains racing â€” the realm is driven by one thread â€” but a
    /// host <c>Task</c> completing on the pool and posting a reaction into a queue the page thread is
    /// draining.
    /// </remarks>
    internal sealed class JobQueue
    {
        private readonly Queue<Action> _queue = new();
        private readonly Lock _gate = new();

        internal int Count
        {
            get
            {
                lock (_gate)
                    return _queue.Count;
            }
        }

        internal void Enqueue(Action job)
        {
            lock (_gate)
                _queue.Enqueue(job);
        }

        internal bool TryDequeue(out Action job)
        {
            lock (_gate)
                return _queue.TryDequeue(out job!);
        }

        internal void Clear()
        {
            lock (_gate)
                _queue.Clear();
        }
    }

    /// <summary>
    /// The <see cref="SynchronizationContext"/> the engine posts promise reactions and <c>await</c>
    /// resumptions to.
    /// </summary>
    /// <remarks>
    /// <see cref="Send"/> stays synchronous: a caller asking to run inline is entitled to that, and it
    /// is already on the thread that would run the job anyway. <see cref="CreateCopy"/> returns the
    /// same instance because the engine may copy the context onto a captured execution context, and a
    /// copy that posted to a fresh queue would post into one nothing drains.
    /// </remarks>
    internal sealed class JobPump(JobQueue queue) : SynchronizationContext, IJSJobPump
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);
            queue.Enqueue(() => d(state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);
            d(state);
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}

