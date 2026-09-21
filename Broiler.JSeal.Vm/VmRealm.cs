using System.Runtime.ExceptionServices;

using Broiler.VM;
using Broiler.VM.Profile.JavaScript;

namespace Broiler.JSeal.Vm;

/// <summary>
/// A JSEAL realm over one Broiler.VM instance and the host surface its profile publishes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole of this provider's difficulty is that the two realms disagree about when they may
/// be touched.</b> A JSEAL realm is held by the host and called whenever the host likes; a
/// Broiler.VM realm is valid only inside a step, on the guest's own thread, because outside one
/// there is no meter to charge the work to and no operation to fault. Neither is wrong. What
/// bridges them is <see cref="InStep{T}"/>: a crossing made while guest code is on the stack -
/// which is where a DOM binding does almost all of its work - runs inline and costs nothing extra,
/// and a crossing made between two invocations asks the profile for a turn and runs inside that.
/// </para>
/// <para>
/// <b>A turn is an invocation, and an embedder pays for it.</b> That is the honest price of
/// reaching into a realm from outside its execution, and it is why the fast path is the one that
/// matters: installing a document's members happens inside the realm-created callback, and every
/// property read and listener dispatch afterwards happens inside a step already.
/// </para>
/// </remarks>
internal sealed partial class VmRealm : IJsRealm
{
    private readonly VmRuntime _runtime;
    private readonly VmVerifiedArtifact _artifact;
    private readonly VmInstance _instance;
    private readonly VmHostBridge _bridge;
    private readonly VmSourceProvider _sources;

    private bool _disposed;

    internal VmRealm(
        VmRuntime runtime,
        VmVerifiedArtifact artifact,
        VmInstance instance,
        VmHostBridge bridge,
        VmSourceProvider sources,
        JsCapabilities capabilities,
        string engineName)
    {
        _runtime = runtime;
        _artifact = artifact;
        _instance = instance;
        _bridge = bridge;
        _sources = sources;
        Capabilities = capabilities;
        EngineName = engineName;
    }

    /// <inheritdoc />
    public JsCapabilities Capabilities { get; }

    /// <inheritdoc />
    public string EngineName { get; }

    /// <inheritdoc />
    public JsValue Global => InStep(realm => VmMarshal.Wrap(realm.Global));

    /// <summary>The VM realm, which is only meaningful inside a step.</summary>
    private JsHostRealm Host =>
        _bridge.Realm ?? throw new JsEngineException(
            "the Broiler.VM instance never handed its realm to this provider, which means the "
                + "composition did not register the host-surface capability");

    /// <summary>
    /// Runs one crossing where the VM realm is willing to be touched, and answers what it produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The in-step case is checked first and is not an optimisation.</b> Asking for a turn while
    /// a step is already open would be asking the instance to run an invocation while it is
    /// executing, which its own lifecycle refuses - so the fast path is also the only correct path
    /// for a crossing made from inside a host callback.
    /// </para>
    /// <para>
    /// <b>An exception raised inside a turn is captured and rethrown here, with its stack.</b> A
    /// turn is an invocation, so an exception escaping it would be flattened into an outcome and a
    /// reason, and a page's own <c>TypeError</c> would reach the bridge as "the invocation faulted".
    /// Capturing it on the way out keeps a guest throw a guest throw.
    /// </para>
    /// </remarks>
    private T InStep<T>(Func<JsHostRealm, T> body)
    {
        ThrowIfDisposed();

        var host = Host;

        if (host.IsCurrent)
            return Translate(body, host);

        var result = default(T);
        ExceptionDispatchInfo? failure = null;

        _bridge.Pending = asked =>
        {
            try
            {
                result = Translate(body, asked);
            }
            catch (Exception raised)
            {
                failure = ExceptionDispatchInfo.Capture(raised);
            }
        };

        var request = new VmInvocationRequest(
            new VmUtf8Text(System.Text.Encoding.UTF8.GetBytes(JavaScriptProfile.TurnEntryPoint)));

        var outcome = _instance.Invoke(in request, CancellationToken.None);

        failure?.Throw();

        if (outcome.Outcome is not VmOutcome.Normal)
        {
            throw new JsEngineException(
                $"the Broiler.VM instance answered {outcome.Outcome}/{outcome.Reason} for a host turn");
        }

        return result!;
    }

    private void InStep(Action<JsHostRealm> body) =>
        InStep<object?>(realm =>
        {
            body(realm);
            return null;
        });

    /// <summary>
    /// Runs a crossing and turns the VM's three failure shapes into JSEAL's two.
    /// </summary>
    /// <remarks>
    /// <b>A guest throw and a host mistake are different exceptions on both sides, and the mapping
    /// keeps them apart.</b> The VM raises a throw-carrying exception for what the page threw and a
    /// surface exception for what the embedder did wrong; JSEAL has
    /// <see cref="JsEngineException"/> for the first and expects the second to be a defect. The
    /// termination is the one that must not be softened: it means the operation ended underneath
    /// this call, and turning it into an ordinary engine exception would let a <c>catch</c> in the
    /// bridge continue past a spent allowance.
    /// </remarks>
    private static T Translate<T>(Func<JsHostRealm, T> body, JsHostRealm realm)
    {
        try
        {
            return body(realm);
        }
        catch (JsHostThrowException thrown)
        {
            throw new JsEngineException(thrown.Message, VmMarshal.Wrap(thrown.Thrown), thrown);
        }
        catch (JsHostSurfaceException refusal)
        {
            throw new JsEngineException(
                "the Broiler.VM host surface refused this crossing: " + refusal.Message, refusal);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, typeof(VmRealm));

    /// <summary>
    /// Raises the capability refusal a host that did not branch on <see cref="Capabilities"/> gets.
    /// </summary>
    private JsCapabilityUnavailableException Lacking(JsCapabilities missing) =>
        new(EngineName, missing);

    /// <inheritdoc />
    /// <remarks>
    /// <b>Disposal drops queued jobs rather than running them</b>, which is what the contract asks
    /// and what the VM does anyway on its own terminal unwind: a job is guest code, and running
    /// guest code while tearing a realm down is the one thing an unwind must not do.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _instance.Dispose();
        _artifact.Dispose();
        _runtime.Dispose();
    }
}

