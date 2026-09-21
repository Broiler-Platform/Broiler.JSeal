using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using Broiler.VM.Profile.JavaScript;

namespace Broiler.JSeal.Vm;

/// <summary>
/// <see cref="IJsValues"/>: minting values, the two coercions that can run guest code, and a truthiness
/// test that on this profile needs no crossing at all.
/// </summary>
internal sealed partial class VmRealm
{
    /// <inheritdoc />
    public JsValue NewObject() => InStep(realm => VmMarshal.Wrap(realm.NewObject()));

    /// <inheritdoc />
    public JsValue NewArray(ReadOnlySpan<JsValue> elements = default)
    {
        var converted = VmMarshal.UnwrapAll(elements);

        return InStep(realm => VmMarshal.Wrap(realm.NewArray(converted)));
    }

    /// <inheritdoc />
    public JsValue NewMethod(string name, JsNativeFunction body, int length = 0)
    {
        ArgumentNullException.ThrowIfNull(body);

        return InStep(realm => VmMarshal.Wrap(realm.NewMethod(name, Trampoline(body), length)));
    }

    /// <inheritdoc />
    public JsValue NewConstructor(string name, JsNativeFunction body, int length = 0)
    {
        ArgumentNullException.ThrowIfNull(body);

        return InStep(realm => VmMarshal.Wrap(realm.NewConstructor(name, Trampoline(body), length)));
    }

    /// <inheritdoc />
    public JsValue NewExotic(IJsExotic handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfDisposed();

        if ((Capabilities & JsCapabilities.ExoticObjects) == 0)
            throw Lacking(JsCapabilities.ExoticObjects);

        return InStep(realm =>
        {
            var completion = new VmExoticObject(handler);
            var exotic = realm.NewExotic(completion);
            completion.Target = exotic;

            return VmMarshal.Wrap(
                handler is IJsExoticDelete deleter ? Deleting(realm, exotic, deleter) : exotic);
        });
    }

    /// <summary>
    /// The same host exotic behind a proxy whose one trap routes a named deletion to the handler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The profile's host-object surface has no delete hook, and this is the route around that
    /// rather than a change to the engine.</b> It is the lesson <c>NewPromise</c> recorded applied
    /// to a second intrinsic: a thing the guest already has is reachable across the host surface
    /// without a new member on it. Three ordinary crossings - the constructor read at realm
    /// creation, a trap minted here, one construct - and no evaluation.
    /// </para>
    /// <para>
    /// <b>ONE trap, and only for a handler that declares a deletion.</b> Every operation on a proxy
    /// costs a property read on the trap object plus a charge before it forwards, which is exactly
    /// the price the profile weighed when it made its host exotic a subclass instead of a proxy. So
    /// the two storage areas pay it and the live collections - whose indexed reads are the hottest
    /// path a page has - are minted exactly as they were. This is the whole reason
    /// <see cref="IJsExoticDelete"/> is a second interface: a provider that could not ask the
    /// question at mint time would have to wrap everything.
    /// </para>
    /// <para>
    /// <b>The trap is the only one defined, so every other operation forwards to the target
    /// unchanged</b> - reads, writes, enumeration, descriptors and the prototype all reach the host
    /// exotic through the proxy's own missing-trap paths, which forward the internal method rather
    /// than an unchecked one. That includes the bridge's own member installation, which reaches the
    /// target while the realm is installing and is correctly not offered to the handler.
    /// </para>
    /// </remarks>
    private JsHostValue Deleting(JsHostRealm realm, JsHostValue exotic, IJsExoticDelete deleter)
    {
        var constructor = _bridge.Proxy;
        var forward = _bridge.ReflectDelete;

        if (constructor.Kind is not JsHostValueKind.Function ||
            forward.Kind is not JsHostValueKind.Function)
        {
            throw new JsEngineException(
                "the realm had no Proxy constructor or no Reflect.deleteProperty when it was created, "
                    + "so this provider cannot complete a deletion on an exotic object");
        }

        var traps = realm.NewObject();

        realm.DefineValue(
            traps,
            "deleteProperty",
            realm.NewMethod(
                "deleteProperty",
                (asked, _, arguments) =>
                {
                    var target = arguments.Length > 0 ? arguments[0] : JsHostValue.Undefined;
                    var key = arguments.Length > 1 ? arguments[1] : JsHostValue.Undefined;

                    // A NAME, NEVER AN INDEX AND NEVER A SYMBOL. This trap is handed every key kind
                    // the guest can delete by, and the profile's own host object routes an
                    // array-index key to the indexed hook and offers none of them to TrySetNamed.
                    // A deletion that reached the named hook with "7" would make this provider
                    // disagree with the other one about what a name is, which is worse than the gap
                    // they share.
                    if (key.Kind is JsHostValueKind.String && !IsArrayIndex(key.AsString()))
                        deleter.TryDeleteNamed(key.AsString());

                    // The ordinary deletion runs either way and its answer is the deletion's answer,
                    // which is also what keeps the proxy's own invariant satisfied. It forwards
                    // through the captured intrinsic rather than through JsHostRealm.DeleteProperty
                    // because that member takes a string name only, so a symbol-keyed deletion would
                    // be dropped in silence - see VmHostBridge.ReflectDelete.
                    return JsHostValue.Boolean(
                        asked.Invoke(forward, JsHostValue.Undefined, [target, key]).AsBoolean());
                },
                length: 2));

        return realm.Construct(constructor, [exotic, traps]);
    }

    /// <summary>
    /// Whether a key is a canonical array index, which is what makes it not a name.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than taken from <c>uint.TryParse</c> so that the leading-zero and
    /// upper-bound rules are visible: <c>"007"</c> and <c>"4294967295"</c> are names, <c>"7"</c> is
    /// an index, and a parse that accepted either would silently widen what reaches the handler.
    /// </remarks>
    private static bool IsArrayIndex(string key)
    {
        if (key.Length is 0 or > 10)
            return false;

        if (key.Length > 1 && key[0] == '0')
            return false;

        ulong value = 0;

        foreach (var digit in key)
        {
            if (digit is < '0' or > '9')
                return false;

            value = (value * 10) + (ulong)(digit - '0');
        }

        return value < uint.MaxValue;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Built out of the realm's own <c>ArrayBuffer</c>, because the profile's host surface has no
    /// binary member</b> - the same route <c>NewPromise</c> takes to the realm's own <c>Promise</c>.
    /// The buffer that comes back is the realm's: a page can <c>new Uint8Array(b)</c> it,
    /// <c>b.slice()</c> it and find it <c>instanceof ArrayBuffer</c>.
    /// </para>
    /// <para>
    /// <b>The bytes go across in chunks, and the chunking is not a performance nicety.</b> Every
    /// crossing of this host surface charges one unit against a host-call allowance that defaults to
    /// a million, and spending it raises a termination rather than anything a page could catch. A
    /// crossing per byte would therefore abort the program on a blob of about a megabyte - not run
    /// slowly, abort - so the bytes are handed over a chunk at a time through
    /// <c>%TypedArray%.prototype.set</c>: two crossings per chunk plus two, rather than one per byte.
    /// </para>
    /// <para>
    /// <b>The ceiling that remains is fuel, and it is a LIFETIME total for the realm rather than a
    /// per-call one.</b> <c>VmBudgetLevel.Release</c> refunds only a ceiling-class dimension - "an
    /// allowance never refunds", in its own words - and fuel is an allowance, so the default fifty
    /// million is what the realm gets for everything it will ever do. Both directions cost fuel
    /// proportional to n. <b>Measured on a realm that has run nothing else: eight megabytes makes the
    /// round trip and twelve does not</b>, failing as a termination rather than as anything a page
    /// could catch. A realm that has actually loaded a page has spent some of that allowance already,
    /// so the practical ceiling is lower and is not a constant. This is a real limit on what a page
    /// can do with a blob under this provider, and it is written down here because a ceiling nobody
    /// wrote down is one somebody meets by surprise.
    /// </para>
    /// </remarks>
    public JsValue NewArrayBuffer(ReadOnlySpan<byte> bytes)
    {
        ThrowIfDisposed();

        if ((Capabilities & JsCapabilities.BinaryData) == 0)
            throw Lacking(JsCapabilities.BinaryData);

        // Copied out of the span before the crossing, because the span cannot outlive this frame and
        // the lambda below runs inside a step.
        var source = bytes.ToArray();

        return VmMarshal.Wrap(InStep(realm =>
        {
            var buffer = realm.Construct(_bridge.ArrayBuffer, [JsHostValue.Number(source.Length)]);
            var view = realm.Construct(_bridge.Uint8Array, [buffer]);

            for (var offset = 0; offset < source.Length; offset += TransferChunk)
            {
                var length = Math.Min(TransferChunk, source.Length - offset);
                var chunk = new JsHostValue[length];

                for (var i = 0; i < length; i++)
                    chunk[i] = JsHostValue.Number(source[offset + i]);

                realm.Invoke(
                    _bridge.TypedArraySet,
                    view,
                    [realm.NewArray(chunk), JsHostValue.Number(offset)]);
            }

            return buffer;
        }));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>The brand check is <c>ArrayBuffer.prototype</c>'s own <c>byteLength</c> getter, invoked
    /// with the candidate as <c>this</c>.</b> Its body asks whether the receiver is the engine's
    /// buffer type and throws a TypeError otherwise, so one crossing answers both halves of this
    /// member - and answers by CLR type rather than by anything a page can write. A
    /// <c>DataView</c> and every typed array answer <c>byteLength</c> themselves, an object's
    /// prototype is settable and its <c>Symbol.toStringTag</c> is writable, so none of the three
    /// JS-visible routes would be an answer.
    /// </para>
    /// <para>
    /// <b>A detached buffer answers <see langword="true"/> with no bytes, and this getter is the only
    /// member that gets that right.</b> Its length is <c>Data?.Length ?? 0</c>, so it returns zero
    /// rather than throwing; <c>slice</c>, the <c>DataView</c> constructor and the <c>Uint8Array</c>
    /// constructor all throw on a detached buffer and would each have answered "not a buffer" for
    /// something that is one. The consequence to know is that a detached buffer is indistinguishable
    /// here from <c>new ArrayBuffer(0)</c>, which is what the File API wants of it anyway.
    /// </para>
    /// <para>
    /// <b>There is no <c>SharedArrayBuffer</c> to exclude here</b>, which the profile states as a
    /// deliberate omission rather than an unfinished one. The other provider's engine has one and
    /// declares it a subclass of the ordinary buffer, so that provider excludes it by hand; this one
    /// has nothing to exclude, and saying so is what stops a later reader adding a check that could
    /// never fire.
    /// </para>
    /// <para>
    /// <b>The bytes come back through the one wide channel the host surface has, which is a
    /// string.</b> A returned string crosses as a reference and costs nothing per character, so a
    /// chunk of the buffer rendered as text crosses in a single call; the alternative, a crossing per
    /// byte, would spend the host-call allowance and abort.
    /// </para>
    /// <para>
    /// <b>The renderer is <c>join</c>, and the reason is not brevity - it is that <c>join</c> asks
    /// the view nothing.</b> Its body walks the engine's own typed array by its CLR length and reads
    /// each element off the CLR object, so no property of the view is consulted on the way.
    /// <c>String.fromCharCode.apply(null, view)</c> would be the shorter spelling and is
    /// <b>wrong</b>: spreading an array-like reads <c>length</c> as a property, and a typed array's
    /// <c>length</c> is a <em>configurable accessor</em> on <c>%TypedArray%.prototype</c> - so
    /// <c>Object.defineProperty(Object.getPrototypeOf(Uint8Array.prototype), 'length', ...)</c>,
    /// which a page may legally do, would decide how many bytes the host reads. Answering zero is the
    /// dangerous direction: it does not throw, so the host would hand back a correctly sized array of
    /// zeros and report success, and a blob built from that is silently empty.
    /// <c>APageThatRewritesTypedArrayLengthCannotChangeWhatTheHostReads</c> is that case, and it
    /// failed before this line said <c>join</c>.
    /// </para>
    /// </remarks>
    public bool TryGetArrayBufferBytes(JsValue value, [NotNullWhen(true)] out byte[]? bytes)
    {
        ThrowIfDisposed();

        if ((Capabilities & JsCapabilities.BinaryData) == 0)
            throw Lacking(JsCapabilities.BinaryData);

        var candidate = VmMarshal.Unwrap(value);

        if (candidate.Kind is not JsHostValueKind.Object)
        {
            bytes = null;
            return false;
        }

        bytes = InStep(realm =>
        {
            int length;

            try
            {
                // Caught inside the step, before Translate turns a guest throw into a
                // JsEngineException: the throw IS the answer here rather than a failure.
                length = (int)realm.Invoke(_bridge.ArrayBufferByteLength, candidate, []).AsNumber();
            }
            catch (JsHostThrowException)
            {
                return null;
            }

            if (length <= 0)
                return [];

            var read = new byte[length];
            var view = realm.Construct(_bridge.Uint8Array, [candidate]);

            for (var offset = 0; offset < length; offset += TransferChunk)
            {
                var end = Math.Min(offset + TransferChunk, length);

                var part = realm.Invoke(
                    _bridge.TypedArraySubarray,
                    view,
                    [JsHostValue.Number(offset), JsHostValue.Number(end)]);

                var text = realm.Invoke(_bridge.TypedArrayJoin, part, [Separator]).AsString()
                    ?? string.Empty;

                // Decimal and comma-separated, parsed in one pass rather than split: a chunk of eight
                // thousand bytes renders as at most thirty-two thousand characters, and allocating a
                // substring per byte would undo the point of moving them in bulk.
                var at = offset;
                var element = 0;

                foreach (var character in text)
                {
                    if (character == ',')
                    {
                        read[at++] = (byte)element;
                        element = 0;
                    }
                    else
                    {
                        element = (element * 10) + (character - '0');
                    }
                }

                read[at] = (byte)element;
            }

            return read;
        });

        return bytes is not null;
    }

    /// <summary>
    /// How many bytes cross at a time, in both directions.
    /// </summary>
    /// <remarks>
    /// <b>Bounded above by the large-object heap and below by the host-call allowance.</b> Every
    /// chunk costs two crossings in each direction, so a small chunk spends the allowance; and each
    /// chunk builds a transient - an argument array going out, a joined string coming back - so a
    /// large one puts that transient on the LOH, where it is not compacted and is collected only with
    /// a generation two. Eight thousand bytes render as at most thirty-two thousand characters, which
    /// is about sixty-four kilobytes and comfortably under the eighty-five thousand byte threshold,
    /// and it puts a thirty-two megabyte buffer at roughly eight thousand crossings against an
    /// allowance of a million.
    /// </remarks>
    private const int TransferChunk = 8000;

    /// <summary>The separator <c>join</c> is asked for, hoisted so it is built once.</summary>
    private static readonly JsHostValue Separator = JsHostValue.String(",");

    /// <inheritdoc />
    public string ToJsString(JsValue value)
    {
        var converted = VmMarshal.Unwrap(value);

        return InStep(realm => realm.ToJsString(converted));
    }

    /// <inheritdoc />
    public double ToNumber(JsValue value)
    {
        var converted = VmMarshal.Unwrap(value);

        return InStep(realm => realm.ToNumber(converted));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>No crossing, because the one kind the contract member exists for is one this profile does not
    /// have.</b> Its host surface has no BigInt value kind, its <c>BigInt</c> global is unbound, and its
    /// front end refuses a BigInt literal by name, so every value of this realm is of a kind
    /// <see cref="JsValue.AsBoolean"/> already decides correctly. Crossing to ask would spend a host-call
    /// charge to be told what the handle knew. There is nothing to delegate to either: the host realm
    /// publishes no truthiness coercion, and the host value's own <c>AsBoolean()</c> reads a boolean's
    /// payload and answers <see langword="false"/> for every other kind, a non-empty string included.
    /// </para>
    /// <para>
    /// <b>The unwrap is a refusal, not a conversion.</b> A BigInt handle cannot have come from this
    /// realm, and <see cref="VmMarshal.Unwrap"/> is the one place that says so, for the same reason it
    /// refuses a handle another engine minted. Answering from the handle there would answer
    /// <see langword="true"/> for another engine's <c>0n</c>, which is the defect this member was added
    /// to remove.
    /// </para>
    /// </remarks>
    public bool ToBoolean(JsValue value)
    {
        ThrowIfDisposed();
        _ = VmMarshal.Unwrap(value);

        return value.AsBoolean;
    }

    private const int InlineArgumentCapacity = 8;

    // JsValue contains managed references, so use a GC-tracked inline array, not stackalloc.
    [InlineArray(InlineArgumentCapacity)]
    private struct ArgumentBuffer
    {
#pragma warning disable IDE0051 // Storage for the inline array.
        private JsValue _element0;
#pragma warning restore IDE0051
    }

    /// <summary>Wraps a JSEAL host body in the shape the VM realm calls.</summary>
    /// <remarks>
    /// Up to eight arguments use this invocation's inline buffer. Larger calls rent an array and
    /// clear its references in finally, including when conversion or the body throws. Each recursive
    /// invocation owns its buffer until its body returns. This removes the adapter's argument-array
    /// allocation; the VM host API still projects arguments into its own array before calling here.
    /// Guest exceptions retain their thrown value; ordinary host exceptions propagate unchanged.
    /// </remarks>
    private JsHostFunction Trampoline(JsNativeFunction body) =>
        (realm, thisValue, arguments) =>
        {
            var buffer = default(ArgumentBuffer);
            JsValue[]? rented = null;
            Span<JsValue> converted = arguments.Length <= InlineArgumentCapacity
                ? buffer[..arguments.Length]
                : (rented = ArrayPool<JsValue>.Shared.Rent(arguments.Length)).AsSpan(0, arguments.Length);

            try
            {
                for (var at = 0; at < arguments.Length; at++)
                    converted[at] = VmMarshal.Wrap(arguments[at]);

                var call = new JsCall(
                    this,
                    VmMarshal.Wrap(thisValue),
                    converted,
                    VmMarshal.Wrap(realm.NewTarget));

                return VmMarshal.Unwrap(body(in call));
            }
            catch (JsEngineException raised) when (!raised.Thrown.IsMissing)
            {
                // Preserve the thrown value so guest code can catch it. The VM constructs its
                // own exception; ordinary host failures pass through this adapter unchanged.
                throw realm.Throw(VmMarshal.Unwrap(raised.Thrown));
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<JsValue>.Shared.Return(rented, clearArray: true);
            }
        };
}

