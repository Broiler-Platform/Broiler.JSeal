namespace Broiler.JSeal.Vm;

/// <summary>
/// <see cref="IJsClone"/>: structured clone, which this provider cannot offer on the pinned
/// Broiler.VM packages.
/// </summary>
/// <remarks>
/// <para>
/// <b>The members exist because the contract makes them mandatory, and they refuse because the
/// capabilities behind them are not declared.</b> <c>IJsClone</c> is a base interface of
/// <see cref="IJsRealm"/> rather than an optional one, so a provider must implement it whether or
/// not its engine can clone. <see cref="Clone"/> refuses <see cref="JsCapabilities.StructuredClone"/>
/// and <see cref="Detach"/> and <see cref="Adopt"/> refuse <see cref="JsCapabilities.WorkerRealms"/>,
/// the split I18 adopted from the J18 decision.
/// </para>
/// <para>
/// <b>What is missing is a public door to the profile's carrier.</b> The VM's own structured clone
/// (JSD-0032) is reachable from the host only through <c>JsHostRealm.DetachClone</c> and
/// <c>AdoptClone</c> (VM JSD-0024 section 17), which the pinned <c>0.1.0-preview.3</c> packages do
/// not have; the adoption waits for the next VM release and pin update.
/// </para>
/// </remarks>
internal sealed partial class VmRealm
{
    /// <inheritdoc />
    public JsValue Clone(JsValue value, ReadOnlySpan<JsValue> transfer = default)
    {
        ThrowIfDisposed();
        throw Lacking(JsCapabilities.StructuredClone);
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

