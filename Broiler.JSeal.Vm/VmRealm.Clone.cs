namespace Broiler.JSeal.Vm;

/// <summary>
/// <see cref="IJsClone"/>: structured clone, which this engine does not have.
/// </summary>
/// <remarks>
/// <para>
/// <b>The members exist because the contract makes them mandatory, and they refuse because the
/// capability behind them is not declared.</b> <c>IJsClone</c> is a base interface of
/// <see cref="IJsRealm"/> rather than an optional one, so a provider must implement it whether or
/// not its engine can clone; what a provider gets to decide is whether it declares
/// <see cref="JsCapabilities.WorkerRealms"/>, and this one does not.
/// </para>
/// <para>
/// <b>What is missing is not a clone algorithm but a second realm to clone into.</b> The
/// Broiler.VM profile creates one realm per instance and has no agent model, so the two-realm half
/// of the capability - detach on the sending thread, adopt on the receiving one - has no receiving
/// side to be about. Writing the same-realm clone alone would let a host declare the capability and
/// discover the other half missing at the moment a Worker started, which is later and worse.
/// </para>
/// </remarks>
internal sealed partial class VmRealm
{
    /// <inheritdoc />
    public JsValue Clone(JsValue value, ReadOnlySpan<JsValue> transfer = default)
    {
        ThrowIfDisposed();
        throw Lacking(JsCapabilities.WorkerRealms);
    }

    /// <inheritdoc />
    public JsDetachedValue Detach(JsValue value, ReadOnlySpan<JsValue> transfer = default)
    {
        ThrowIfDisposed();
        throw Lacking(JsCapabilities.WorkerRealms);
    }

    /// <inheritdoc />
    public JsValue Adopt(JsDetachedValue detached)
    {
        ThrowIfDisposed();
        throw Lacking(JsCapabilities.WorkerRealms);
    }

    /// <inheritdoc />
    /// <remarks>
    /// It answers rather than throwing, because a host walking a transfer list asks this about every
    /// entry before it has decided to clone anything - so a refusal here would refuse the question
    /// rather than the operation. Nothing in this engine is transferable, and saying so is a
    /// complete answer.
    /// </remarks>
    public JsTransferKind ClassifyTransferable(JsValue value)
    {
        ThrowIfDisposed();
        return JsTransferKind.NotTransferable;
    }
}

