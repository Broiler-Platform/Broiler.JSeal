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

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>It builds the promise the way a page would, and that is the whole of why it can exist.</b>
    /// The realm's own <c>Promise</c> â€” captured at creation, see <c>VmHostBridge.Promise</c> â€” is
    /// handed an executor minted as a host method, and constructing with it yields the pair the
    /// language itself created. So the promise is an ordinary promise of this realm and
    /// <c>p instanceof Promise</c> holds for the page. Three members of the host surface do all of
    /// it: <c>GetProperty</c>, <c>NewMethod</c> and <c>Construct</c>.
    /// </para>
    /// <para>
    /// <b>What it does not do is track an unhandled rejection, because this profile does not.</b>
    /// A rejection reaching a drain with no handler is silent here and is reported on Broiler.JS,
    /// which is a difference between the two engines rather than a gap in this member â€” no JSEAL
    /// contract asks for the tracking. It is written down because a bridge that relies on the
    /// report would lose it quietly on this engine rather than loudly.
    /// </para>
    /// <para>
    /// <b>This member used to refuse, and the reason it gave was wrong in a way worth keeping.</b>
    /// It argued that the only way to fake a settleable promise was to EVALUATE a snippet that
    /// captured the resolvers, and that a page whose Content-Security-Policy forbids evaluation
    /// would then be asking <c>fetch</c> for a promise built on the capability it had just refused.
    /// The objection is sound about the route it names and does not reach this one: evaluation is
    /// not the only way to reach a constructor, and nothing above compiles a character. A realm
    /// built with <c>AllowGuestEval: false</c> gets a working promise here, which is exactly the
    /// case the old argument said could not exist. It is the same shape of mistake as the one
    /// <c>docs/jseal.md</c> records about the capability channel: a true statement about one
    /// mechanism, read as a statement about all of them.
    /// </para>
    /// <para>
    /// <b>The executor runs synchronously and this depends on it rather than hoping for it.</b>
    /// <c>new Promise(f)</c> has called <c>f</c> by the time it returns â€” the specification says so
    /// and the profile implements it that way â€” so the resolvers are in hand the moment
    /// <c>Construct</c> answers. A realm whose <c>Promise</c> did not do that would leave the
    /// captures unset, and this refuses at that point rather than handing back two actions that
    /// silently do nothing.
    /// </para>
    /// <para>
    /// <b>Settling does not drain, and that is the contract's own ordering rather than an
    /// accident.</b> A reaction is a job: it must not run at the moment the promise is settled, and
    /// it must run at the next checkpoint the host takes. Calling the guest's <c>resolve</c> only
    /// ever enqueues, and a settle made from outside a step takes a turn â€” <c>#host-turn</c>, which
    /// runs the crossing and returns â€” rather than the profile's separate <c>#drain-jobs</c>. So
    /// nothing here runs guest reactions inside the host's own frame at a point no page could have
    /// predicted.
    /// </para>
    /// <para>
    /// <b>Both actions stay valid for the life of the realm, and refuse after it.</b> The captured
    /// functions are ordinary guest objects whose identity the profile keys on a weak table, so a
    /// handle held across turns still names the same function; a settle attempted after
    /// <see cref="Dispose"/> meets the same <see cref="ObjectDisposedException"/> every other member
    /// raises, because a host that resolves a <c>fetch</c> after the document it belonged to was
    /// torn down has made a mistake worth hearing about.
    /// </para>
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

