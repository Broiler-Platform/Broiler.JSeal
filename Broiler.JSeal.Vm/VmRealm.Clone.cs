using Broiler.JSeal.Providers;
using Broiler.VM.Profile.JavaScript;

namespace Broiler.JSeal.Vm;

/// <summary>
/// <see cref="IJsClone"/>: structured clone over the profile's own carrier (VM JSD-0024 section 17,
/// JSD-0032), public from Broiler.VM <c>0.1.0-preview.4</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every member is the engine's operation.</b> <c>JsHostRealm.DetachClone</c> serializes a value,
/// with its transfer list, into an opaque <c>JsHostCloneCarrier</c> that holds no guest object and
/// no realm, and <c>JsHostRealm.AdoptClone</c> rebuilds it in the realm that is asked, against that
/// realm's own intrinsics. <see cref="Detach"/> and <see cref="Adopt"/> are exactly those two
/// halves; <see cref="Clone"/> is both in one step of the same realm. The supported brands, the
/// refusals (a <c>TypeError</c> whose message begins <c>DataCloneError:</c>, raised here as
/// <see cref="JsEngineException"/>), getter order, transfer validation before any buffer detaches,
/// and the bounds are the profile's, not this provider's.
/// </para>
/// <para>
/// <b>A carrier holding transferred bytes is single-use.</b> The profile moves a transferred buffer's
/// bytes into the carrier and lets exactly one adoption claim them, atomically, across threads; a
/// later adoption is refused and surfaces as <see cref="JsEngineException"/>. A carrier made without
/// a transfer list is repeatable, each adoption an independent copy. This is narrower than
/// <see cref="IJsClone.Adopt"/>'s "may be adopted more than once" for the transfer case, and is why
/// <see cref="JsCapabilities.WorkerRealms"/>, which gates <see cref="Detach"/> and <see cref="Adopt"/>,
/// is not declared (see <see cref="VmEngineProvider.Capabilities"/>);
/// <see cref="JsCapabilities.StructuredClone"/>, which gates <see cref="Clone"/>, is: a same-realm
/// clone adopts its carrier exactly once, in the same step.
/// </para>
/// <para>
/// <b>The carrier is a crossing's result, so every member runs in a step</b> of this realm on the
/// calling thread, charged to it. A carrier may be handed to another thread and adopted there by a
/// realm that thread built and drives; nothing else about it is thread-bound.
/// </para>
/// </remarks>
internal partial class VmRealm
{
    /// <inheritdoc />
    public JsValue Clone(JsValue value, ReadOnlySpan<JsValue> transfer = default)
    {
        ThrowIfDisposed();

        if ((Capabilities & JsCapabilities.StructuredClone) == 0)
            throw Lacking(JsCapabilities.StructuredClone);

        var target = CloneOperand(VmMarshal.Unwrap(value));
        var listed = CloneOperands(VmMarshal.UnwrapAll(transfer));

        return InStep(realm => VmMarshal.Wrap(realm.AdoptClone(realm.DetachClone(target, listed))));
    }

    /// <inheritdoc />
    public JsDetachedValue Detach(JsValue value, ReadOnlySpan<JsValue> transfer = default)
    {
        ThrowIfDisposed();

        if ((Capabilities & JsCapabilities.WorkerRealms) == 0)
            throw Lacking(JsCapabilities.WorkerRealms);

        var target = CloneOperand(VmMarshal.Unwrap(value));
        var listed = CloneOperands(VmMarshal.UnwrapAll(transfer));

        return JsProviderClone.Detached(EngineName, InStep(realm => realm.DetachClone(target, listed)));
    }

    /// <summary>
    /// <see cref="JsValue.Missing"/> as <c>undefined</c>, which is what every other member hands the
    /// engine for it (the other provider's unwrap does the same).
    /// </summary>
    /// <remarks>
    /// <c>DetachClone</c> refuses the profile's own <c>Missing</c> marker, as the value or as a
    /// transfer entry, with an <see cref="ArgumentException"/>, which is a host-argument failure no
    /// <see cref="IJsClone"/> caller is written against. As <c>undefined</c> the value clones, and the
    /// entry meets the profile's <c>DataCloneError</c> for a non-buffer, which reaches the host as
    /// <see cref="JsEngineException"/>.
    /// </remarks>
    private static JsHostValue CloneOperand(JsHostValue value) =>
        value.Kind == JsHostValueKind.Missing ? JsHostValue.Undefined : value;

    private static JsHostValue[] CloneOperands(JsHostValue[] values)
    {
        for (var at = 0; at < values.Length; at++)
            values[at] = CloneOperand(values[at]);

        return values;
    }

    /// <inheritdoc />
    public JsValue Adopt(JsDetachedValue detached)
    {
        ArgumentNullException.ThrowIfNull(detached);
        ThrowIfDisposed();

        if ((Capabilities & JsCapabilities.WorkerRealms) == 0)
            throw Lacking(JsCapabilities.WorkerRealms);

        // Refused by engine name first, like the other provider, and then by the profile itself: a
        // payload that is not a carrier of this profile build is its ForeignCarrier refusal.
        if (!string.Equals(detached.EngineName, EngineName, StringComparison.Ordinal))
        {
            throw new JsEngineException(
                $"A structured clone from the '{detached.EngineName}' engine cannot be adopted into a " +
                $"'{EngineName}' realm.");
        }

        var carrier = JsProviderClone.PayloadOf(detached)
            ?? throw new JsEngineException("The structured clone carries no Broiler.VM carrier.");

        return InStep(realm => VmMarshal.Wrap(realm.AdoptClone(carrier)));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>By brand, through the profile's buffer read, and without copying.</b> A zero-length
    /// destination makes the read answer the brand and the detached state and write nothing; no
    /// property is read and no guest code runs. A fixed-length <c>ArrayBuffer</c> is what the
    /// profile transfers.
    /// </para>
    /// <para>
    /// <b>A resizable <c>ArrayBuffer</c> classifies as transferable</b>, because HTML makes it one,
    /// and the profile's carrier then refuses it as an unsupported brand (JSD-0032 section 5): an
    /// explicit refusal from the clone, with every listed buffer still attached, rather than a
    /// classification this provider could only make by reading a guest-writable property.
    /// </para>
    /// </remarks>
    public JsTransferKind ClassifyTransferable(JsValue value)
    {
        ThrowIfDisposed();

        if ((Capabilities & JsCapabilities.StructuredClone) == 0 || !value.IsObject)
            return JsTransferKind.NotTransferable;

        var target = VmMarshal.Unwrap(value);

        return InStep(realm => realm.TryReadArrayBuffer(target, Span<byte>.Empty, out _) switch
        {
            JsHostBufferStatus.Copied or JsHostBufferStatus.DestinationTooSmall => JsTransferKind.Transferable,
            JsHostBufferStatus.Detached => JsTransferKind.Detached,
            _ => JsTransferKind.NotTransferable,
        });
    }
}
