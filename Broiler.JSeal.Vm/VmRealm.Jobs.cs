using Broiler.VM.Profile.JavaScript;

namespace Broiler.JSeal.Vm;

/// <summary>
/// <see cref="IJsJobs"/>: the microtask queue, and a promise this engine can hand a host.
/// </summary>
internal sealed partial class VmRealm
{
    /// <inheritdoc />
    public bool HasPendingJobs => InStep(realm => realm.HasPendingJobs);

    /// <inheritdoc />
    public void EnqueueJob(Action job)
    {
        ArgumentNullException.ThrowIfNull(job);

        InStep(realm => realm.EnqueueJob(job));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>A job queued by a job is followed, which is why there is a limit.</b> The count answered
    /// is the number that ran, so a caller draining with a limit can tell an exhausted queue from a
    /// truncated one by asking <see cref="HasPendingJobs"/> afterwards.
    /// </remarks>
    public int DrainJobs(int limit = 10_000) => InStep(realm => realm.DrainJobs(limit));

    /// <summary>Create a promise through the captured intrinsic and retain its resolving functions.</summary>
    /// <remarks>
    /// The intrinsic constructor runs the host executor synchronously, so this needs no source evaluation
    /// and works without GuestEval. Settlement uses the owning realm's step, queues reactions without
    /// draining them, and refuses after disposal. JSEAL defines no unhandled-rejection reporting contract.
    /// </remarks>
    public JsValue NewPromise(out Action<JsValue> resolve, out Action<JsValue> reject)
    {
        ThrowIfDisposed();

        var capturedResolve = JsHostValue.Missing;
        var capturedReject = JsHostValue.Missing;

        // Taken at realm creation rather than now, because `Promise` is a writable global: see
        // VmHostBridge.Promise for what reading it here would let a page do.
        var constructor = _bridge.Promise;

        if (constructor.Kind is not JsHostValueKind.Function)
        {
            throw new JsEngineException(
                "the realm had no Promise constructor when it was created, so this provider cannot "
                    + "build a promise out of the language's own machinery");
        }

        var promise = InStep(realm =>
        {
            // The executor's whole body is the capture. It is minted with length 2 because that is
            // what `new Promise` passes and what `Function.prototype.length` should report, and it
            // is unnamed because nothing ever sees it: the constructor calls it once and drops it.
            var executor = realm.NewMethod(
                string.Empty,
                (_, _, arguments) =>
                {
                    if (arguments.Length >= 2)
                    {
                        capturedResolve = arguments[0];
                        capturedReject = arguments[1];
                    }

                    return JsHostValue.Undefined;
                },
                length: 2);

            return realm.Construct(constructor, [executor]);
        });

        if (capturedResolve.Kind is not JsHostValueKind.Function ||
            capturedReject.Kind is not JsHostValueKind.Function)
        {
            throw new JsEngineException(
                "the realm's Promise constructor did not call its executor synchronously, so the "
                    + "resolving pair could not be captured");
        }

        // Copied into locals the closures own, because the captures above are assigned inside the
        // executor and a closure over the mutable locals would read whatever ran last.
        var settleResolve = capturedResolve;
        var settleReject = capturedReject;

        resolve = value => Settle(settleResolve, value);
        reject = value => Settle(settleReject, value);

        return VmMarshal.Wrap(promise);
    }

    /// <summary>
    /// Calls one half of a resolving pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It goes through <see cref="InStep{T}"/> like every other crossing, and that is what makes
    /// a settle from outside the guest legal at all.</b> A host settles a promise at the moment its
    /// own work finished â€” a response arrived, a custom element was defined â€” which is almost never
    /// while guest code is on the stack. The turn that opens for it is an invocation the embedder
    /// pays for.
    /// </para>
    /// <para>
    /// <b>It inherits <see cref="InStep{T}"/>'s thread rule and does not widen it.</b> The VM realm
    /// is current only inside a step AND on the thread that opened it, so a settle from a pool
    /// thread asks the instance for a turn from that thread â€” which is the same thing every other
    /// member of this provider does, and is a question about the instance rather than about
    /// promises. A host that completes work on the pool should post the settle to the thread it
    /// drives the realm from, as it must for any other crossing.
    /// </para>
    /// <para>
    /// <b>A second settle is the language's no-op rather than something this detects.</b> The
    /// resolving functions are latched by the specification's own machinery, so resolving an
    /// already-settled promise does nothing and this does not have to guard it.
    /// </para>
    /// </remarks>
    private void Settle(JsHostValue settler, JsValue value)
    {
        var argument = VmMarshal.Unwrap(value);

        InStep(realm => realm.Invoke(settler, JsHostValue.Undefined, [argument]));
    }
}

