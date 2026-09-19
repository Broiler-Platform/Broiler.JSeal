using System.Numerics;
using System.Reflection;

using Broiler.JSeal;

namespace Broiler.JSeal.Tests;

/// <summary>
/// Re-entrancy, worker realms and structured clone, the array questions the transfer-list walk asks, the realm's answers about itself, and binary data.
/// </summary>
public partial class JsealConformanceTests
{
    // â”€â”€ re-entrancy and worker realms â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [MemberData(nameof(Engines))]
    public void AHostFunctionMayCallBackIntoScriptWhileTheEngineIsInsideIt(string engine)
    {
        using var realm = NewRealm(engine);

        JsValue Twice(in JsCall call)
        {
            var callback = call[0];
            if (!callback.IsFunction)
                throw call.Realm.Error(JsErrorKind.TypeError, "a function is required");

            // The engine is inside this host call right now. An event listener, a promise reaction
            // and a toString coercion are all exactly this, so an engine without it can run a page's
            // script but cannot dispatch a click.
            var first = call.Realm.Invoke(callback, JsValue.Undefined, [JsValue.Number(20d)]);
            var second = call.Realm.Invoke(callback, JsValue.Undefined, [JsValue.Number(22d)]);
            return JsValue.Number(first.AsNumber + second.AsNumber);
        }

        if (Lacks(realm, JsCapabilities.ReentrantHostCalls))
        {
            realm.DefineValue(realm.Global, "twice", realm.NewMethod("twice", Twice));
            Assert.Throws<JsEngineException>(() => realm.EvaluateHostScript("twice(function (n) { return n; })", "test:reentrant"));
            return;
        }

        realm.DefineValue(realm.Global, "twice", realm.NewMethod("twice", Twice, 1));
        Assert.Equal("42", Eval(realm, "String(twice(function (n) { return n; }))", "test:reentrant"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ASecondRealmRunsOnASecondThread(string engine)
    {
        var provider = Provider(engine);
        using var first = provider.CreateRealm(JsRealmOptions.Default);

        if (Lacks(first, JsCapabilities.WorkerRealms))
            return;

        // Nothing promises that two threads may touch ONE realm â€” the contract says so and this
        // provider does not permit it â€” so the second realm is built and used entirely on the
        // second thread, which is what a Worker does.
        //
        // The capability names two things ("a second realm can be created on another thread AND
        // values moved between the two by structured clone"), so this exercises both: a message goes
        // out as a JsDetachedValue, is adopted on the worker thread, and a reply comes back the same
        // way. A test that only built the realm would leave half the claim unchecked.
        var outbound = first.NewObject();
        first.DefineValue(outbound, "greeting", JsValue.String("from the page"));
        var sent = first.Detach(outbound);

        JsValue secondGlobal = default;
        var answer = string.Empty;
        var received = string.Empty;
        JsDetachedValue? reply = null;
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            try
            {
                using var second = provider.CreateRealm(JsRealmOptions.Default);
                second.EvaluateHostScript("var inWorker = 'worker';", "test:worker");
                answer = second.ToJsString(second.GetProperty(second.Global, "inWorker"));
                secondGlobal = second.Global;

                // Adopted HERE, on this thread, by THIS realm â€” which is what makes the resulting
                // objects the worker's own rather than the page's.
                var inbound = second.Adopt(sent);
                received = second.ToJsString(second.GetProperty(inbound, "greeting"));

                var outgoing = second.NewObject();
                second.DefineValue(outgoing, "greeting", JsValue.String("from the worker"));
                reply = second.Detach(outgoing);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "the worker realm did not finish");
        Assert.Null(failure);
        Assert.Equal("worker", answer);
        Assert.Equal("from the page", received);

        // Two realms, not one shared one: a declaration in the worker is not visible here.
        Assert.False(first.Global == secondGlobal);
        Assert.True(first.GetProperty(first.Global, "inWorker").IsUndefined);

        // And the reply crosses back the same way, into the realm that asks for it.
        Assert.NotNull(reply);
        var materialized = first.Adopt(reply!);
        Assert.Equal("from the worker", first.ToJsString(first.GetProperty(materialized, "greeting")));
    }

    // â”€â”€ structured clone â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [MemberData(nameof(Engines))]
    public void ACloneIsACopyRatherThanTheSameObject(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.WorkerRealms))
            return;

        var original = realm.NewObject();
        realm.DefineValue(original, "n", JsValue.Number(1d));

        var copy = realm.Clone(original);

        // Not the same object, and not sharing state with it: mutating the source afterwards is
        // invisible to the copy, which is the property the messaging model depends on.
        Assert.False(copy == original);
        Assert.Equal(1d, realm.GetProperty(copy, "n").AsNumber);
        realm.SetProperty(original, "n", JsValue.Number(2d));
        Assert.Equal(1d, realm.GetProperty(copy, "n").AsNumber);

        // A primitive clones to itself, which is why a carrier cannot be a JsValue handle: there is
        // no engine instance in this answer to key one on.
        Assert.Equal("text", realm.Clone(JsValue.String("text")).AsString);
        Assert.True(realm.Clone(JsValue.Undefined).IsUndefined);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void CloningRefusesAValueTheAlgorithmDoesNotCover(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.WorkerRealms))
            return;

        // A function is the canonical uncloneable value, and a page reaches this by writing
        // postMessage(function () {}). The failure has to be an exception the host can catch and
        // turn into a DataCloneError, not a silently empty object.
        var uncloneable = realm.NewMethod("f", static (in _) => JsValue.Undefined);
        Assert.Throws<JsEngineException>(() => realm.Clone(uncloneable));
        Assert.Throws<JsEngineException>(() => realm.Detach(uncloneable));
    }

    /// <summary>
    /// The other side of every <c>Lacks(realm, WorkerRealms)</c> early return above: what a realm
    /// that does NOT declare the capability does when asked anyway, and what a caller may conclude
    /// from the shape of the refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every other clone test skips this case, which is how five call sites came to get it
    /// wrong.</b> A refusal is <c>JsCapabilityUnavailableException</c>, and that type deliberately
    /// does not derive from <see cref="JsEngineException"/> â€” <c>JsErrors.cs</c> gives the reason:
    /// the second means the page's code went wrong, the first means the host's did, and "a host that
    /// branches on IJsRealm.Capabilities never sees it". The DOM bindings had it the other way
    /// round: they called <c>Clone</c>, <c>Detach</c> and <c>Adopt</c> unguarded inside
    /// <c>catch (JsEngineException)</c> written to raise a <c>DataCloneError</c>, and on a provider
    /// without the capability that catch could never fire, so the host error went out through page
    /// script raw. They branch now.
    /// </para>
    /// <para>
    /// <b>The last assertion is the one that guards the fix rather than the bug.</b> Deriving
    /// <c>JsCapabilityUnavailableException</c> from <see cref="JsEngineException"/> would make every
    /// unfiltered catch in the bridge absorb a host bug as though it were a page error, so this
    /// pins that they stay unrelated â€” and if a later change decides otherwise, it fails here and
    /// the bindings get looked at again instead of quietly changing meaning.
    /// </para>
    /// <para>
    /// It asserts nothing for a provider that HAS the capability, and says so by returning: the
    /// clone behaviour of such a realm is what the two tests above are for.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void ARealmWithoutWorkerRealmsRefusesWithAHostErrorNoEngineCatchCanAbsorb(string engine)
    {
        using var realm = NewRealm(engine);

        if (!Lacks(realm, JsCapabilities.WorkerRealms))
            return;

        var value = realm.NewObject();

        var refusal = Assert.Throws<JsCapabilityUnavailableException>(() => realm.Clone(value));
        Assert.Equal(JsCapabilities.WorkerRealms, refusal.Missing);
        Assert.Equal(engine, refusal.EngineName);
        Assert.Throws<JsCapabilityUnavailableException>(() => realm.Detach(value));

        // Asked through reflection rather than as `refusal is JsEngineException`, which the compiler
        // would fold to a constant for two sealed unrelated types and which would then stop being a
        // question the moment someone changed the hierarchy â€” the exact change this is here to
        // notice.
        Assert.False(
            typeof(JsEngineException).IsAssignableFrom(refusal.GetType()),
            "A capability refusal must not be catchable as JsEngineException: the bindings branch on "
            + "IJsRealm.Capabilities precisely because it is not, and a catch that absorbed it would "
            + "report a host bug to the page as though the page had caused it.");

        // ClassifyTransferable answers rather than refusing, because a host walking a transfer list
        // asks it about every entry before deciding to clone anything. A refusal there would refuse
        // the question rather than the operation.
        Assert.Equal(JsTransferKind.NotTransferable, realm.ClassifyTransferable(value));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ATransferListDetachesItsSourceAndCarriesTheContents(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.WorkerRealms) || Lacks(realm, JsCapabilities.HostScriptSource))
            return;

        var buffer = realm.EvaluateHostScript(
            "(function () { var b = new ArrayBuffer(4); new Uint8Array(b)[0] = 7; return b; })()",
            "test:transfer");

        Assert.Equal(JsTransferKind.Transferable, realm.ClassifyTransferable(buffer));

        var payload = realm.NewObject();
        realm.DefineValue(payload, "buffer", buffer);
        var moved = realm.Clone(payload, [buffer]);

        // The observable half of "transfer": the source is detached afterwards and the receiver has
        // the bytes. (What is NOT promised is zero copies; this engine copies and then detaches.)
        Assert.Equal(JsTransferKind.Detached, realm.ClassifyTransferable(buffer));
        realm.DefineValue(realm.Global, "moved", moved);
        Assert.Equal("7", Eval(realm, "String(new Uint8Array(moved.buffer)[0])", "test:transfer-read"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void OnlyATransferableObjectClassifiesAsOne(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.WorkerRealms))
            return;

        // The question a postMessage transfer list asks of every entry it does not recognise itself.
        // Everything that is not transferable answers the same way, including the hole a sparse
        // array hands over and the primitive a page puts in the list by mistake.
        Assert.Equal(JsTransferKind.NotTransferable, realm.ClassifyTransferable(realm.NewObject()));
        Assert.Equal(JsTransferKind.NotTransferable, realm.ClassifyTransferable(realm.NewArray()));
        Assert.Equal(JsTransferKind.NotTransferable, realm.ClassifyTransferable(JsValue.Number(1d)));
        Assert.Equal(JsTransferKind.NotTransferable, realm.ClassifyTransferable(JsValue.Missing));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ADetachedCarrierBelongsToItsEngineAndToNoRealm(string engine)
    {
        var provider = Provider(engine);
        using var realm = provider.CreateRealm(JsRealmOptions.Default);

        if (Lacks(realm, JsCapabilities.WorkerRealms))
            return;

        var source = realm.NewObject();
        realm.DefineValue(source, "n", JsValue.Number(3d));
        var carrier = realm.Detach(source);

        Assert.Equal(realm.EngineName, carrier.EngineName);

        // Adopting twice yields two independent copies. One message delivered to two realms needs
        // that, and it is also what says the carrier is not itself a realm's object.
        var first = realm.Adopt(carrier);
        var second = realm.Adopt(carrier);
        Assert.False(first == second);
        Assert.Equal(3d, realm.GetProperty(first, "n").AsNumber);
        Assert.Equal(3d, realm.GetProperty(second, "n").AsNumber);

        // A carrier another engine minted is refused rather than walked. In a process with one
        // provider registered this is the only way to build one, and the refusal is what keeps two
        // linked engines from silently handing each other graphs neither can read.
        var foreign = Broiler.JSeal.Providers.JsProviderClone.Detached("not-this-engine", null);
        Assert.Throws<JsEngineException>(() => realm.Adopt(foreign));
    }

    // â”€â”€ the two array questions the messaging transfer-list walk is built on â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [MemberData(nameof(Engines))]
    public void OwnPropertyNamesOfAnArrayAreItsPresentIndicesAndNotItsLength(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.HostScriptSource))
            return;

        // This is how the bridge walks a postMessage transfer list, and both halves matter. A HOLE
        // must be skipped: handing it on as a value would turn postMessage(m, [ , buf]) into a
        // DataCloneError a browser does not raise. And `length` must not appear, or the walk would
        // classify a number as a transfer-list entry.
        var sparse = realm.EvaluateHostScript("(function () { var a = ['x']; a[2] = 'z'; return a; })()", "test:sparse");

        Assert.Equal(new[] { "0", "2" }, realm.OwnPropertyNames(sparse));
        Assert.Equal("x", realm.GetIndex(sparse, 0).AsString);
        Assert.Equal("z", realm.GetIndex(sparse, 2).AsString);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void DefiningTheNextIndexOnAnArrayExtendsIt(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.HostScriptSource))
            return;

        // Appending, as the worker global's listener array does it. An engine on which DefineIndex
        // left `length` behind would grow an array that JavaScript could not iterate, and the
        // listeners a worker registered would silently never be called.
        var array = realm.NewArray([JsValue.String("a")]);
        realm.DefineIndex(array, (uint)realm.GetProperty(array, "length").AsNumber, JsValue.String("b"));

        Assert.Equal(2d, realm.GetProperty(array, "length").AsNumber);
        Assert.Equal("b", realm.GetIndex(array, 1).AsString);

        realm.DefineValue(realm.Global, "appended", array);
        Assert.Equal("a,b", Eval(realm, "Array.prototype.join.call(appended, ',')", "test:append"));
    }

    // â”€â”€ the realm's own answers about itself â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [MemberData(nameof(Engines))]
    public void ARealmNamesItsEngineAndIsNeverWiderThanIt(string engine)
    {
        var provider = Provider(engine);
        using var realm = provider.CreateRealm(JsRealmOptions.Default);

        Assert.Equal(provider.Name, realm.EngineName);
        Assert.Equal(engine, realm.EngineName);

        // "A realm's capabilities may be narrower than its provider's, but never wider" is what lets
        // a host decide whether an engine can serve a page without paying to build a realm.
        Assert.Equal(realm.Capabilities, realm.Capabilities & provider.Capabilities);

        // A lower-case, hyphenated, stable identifier: it is what a configuration or an environment
        // variable names, so it must not read as a display string.
        Assert.Equal(provider.Name.ToLowerInvariant(), provider.Name);
        Assert.DoesNotContain(' ', provider.Name);
        Assert.False(string.IsNullOrWhiteSpace(provider.Description));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ADisposedRealmRefusesRatherThanAnsweringForARealmThatIsGone(string engine)
    {
        var realm = NewRealm(engine);
        realm.EnqueueJob(static () => throw new InvalidOperationException("a job of a disposed realm must not run"));
        realm.Dispose();

        // Jobs queued but never drained belong to a realm that is going away; running them would
        // execute page script against a document the host has already finished with.
        Assert.Throws<ObjectDisposedException>(() => realm.DrainJobs());
        Assert.Throws<ObjectDisposedException>(() => realm.EnqueueJob(static () => { }));

        // Dispose is idempotent, because a host with a realm in a using block inside a teardown path
        // will reach it twice.
        realm.Dispose();
    }

    // â”€â”€ binary data â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //
    // Two members and three operations: mint a buffer over host bytes, tell a buffer from everything
    // else, and read one back. The bridge needs all three - a factory alone would leave the test and
    // the read behind, which is what `BlobBinding`'s remarks say and why the contract took the shape
    // it did.

    /// <summary>
    /// A buffer minted by the host is the realm's own, and a view over it sees the bytes.
    /// </summary>
    /// <remarks>
    /// <b>"The realm's own" is the whole claim, and <c>byteLength</c> does not establish it.</b> An
    /// ordinary object carrying a <c>byteLength</c> would satisfy a length assertion and fail every
    /// page that writes <c>new Uint8Array(b)</c>. So this asserts the brand from JavaScript, which
    /// also makes the failure name a missing intrinsic rather than surfacing as something odd three
    /// layers down - the case that matters for a provider reaching its realm's own
    /// <c>ArrayBuffer</c> rather than declaring a type of its own.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void AMintedArrayBufferIsTheRealmsOwnAndAViewOverItSeesTheBytes(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.BinaryData))
            return;

        var bytes = new byte[] { 1, 2, 250 };
        var buffer = realm.NewArrayBuffer(bytes);

        Assert.True(realm.TryGetArrayBufferBytes(buffer, out var read));
        Assert.Equal(bytes, read);
        Assert.True(realm.GetProperty(buffer, "byteLength") == JsValue.Number(3d));

        // Copied, not aliased. A blob is immutable, and a provider that wrapped the host's array
        // would let a page rewrite the blob its buffer came from.
        bytes[0] = 9;
        Assert.True(realm.TryGetArrayBufferBytes(buffer, out var again));
        Assert.Equal([1, 2, 250], again);

        if (Lacks(realm, JsCapabilities.HostScriptSource))
            return;

        realm.DefineValue(realm.Global, "minted", buffer);

        Assert.Equal("[object ArrayBuffer]", Eval(realm, "Object.prototype.toString.call(minted)", "test:buffer-brand"));
        Assert.Equal("true", Eval(realm, "String(minted instanceof ArrayBuffer)", "test:buffer-instanceof"));
        Assert.Equal("1,2,250", Eval(realm, "Array.prototype.join.call(new Uint8Array(minted), ',')", "test:buffer-view"));
        Assert.Equal("3", Eval(realm, "String(minted.slice(0).byteLength)", "test:buffer-slice"));
    }

    /// <summary>
    /// The host reads back a buffer a script made, and tells one from everything else.
    /// </summary>
    /// <remarks>
    /// <b><c>new Blob([part])</c> is the caller, and it has to tell a buffer from an object it must
    /// stringify.</b> There is no JS-visible property that answers it, which is why this is a
    /// contract member and not a property read. The view case matters most: a typed array is NOT a
    /// buffer, and a host that said it was would take a <c>Uint8Array</c>'s bytes where the page
    /// passed a <c>Float64Array</c>. Reaching the buffer at the end of a view's own
    /// <c>buffer</c>/<c>byteOffset</c>/<c>byteLength</c> chain is ordinary property reads and needs
    /// no contract of its own - this is the assertion that the chain ends somewhere testable.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void TheHostReadsBackABufferAScriptMadeAndTellsOneFromEverythingElse(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.BinaryData) || Lacks(realm, JsCapabilities.HostScriptSource))
            return;

        var buffer = realm.EvaluateHostScript(
            "(function () { var b = new ArrayBuffer(3); var v = new Uint8Array(b); v[0] = 7; v[2] = 8; return b; })()",
            "test:script-buffer");

        Assert.True(realm.TryGetArrayBufferBytes(buffer, out var bytes));
        Assert.Equal([7, 0, 8], bytes);

        // A view is not a buffer, and the buffer it names is.
        var view = realm.EvaluateHostScript("new Uint8Array([4, 5, 6, 7])", "test:script-view");
        Assert.False(realm.TryGetArrayBufferBytes(view, out _));
        Assert.True(realm.TryGetArrayBufferBytes(realm.GetProperty(view, "buffer"), out var viewed));
        Assert.Equal([4, 5, 6, 7], viewed);

        // Neither is a DataView, whose byteLength answers exactly as a buffer's does.
        var dataView = realm.EvaluateHostScript("new DataView(new ArrayBuffer(2))", "test:script-dataview");
        Assert.False(realm.TryGetArrayBufferBytes(dataView, out _));

        // Nor an object dressed as one. A prototype is settable and a toStringTag is writable, so a
        // provider answering by either would be answering a page's claim about itself.
        var impostor = realm.EvaluateHostScript(
            "Object.defineProperty(Object.create(ArrayBuffer.prototype), 'byteLength', { value: 8 })",
            "test:script-impostor");
        Assert.False(realm.TryGetArrayBufferBytes(impostor, out _));

        Assert.False(realm.TryGetArrayBufferBytes(realm.NewObject(), out _));
        Assert.False(realm.TryGetArrayBufferBytes(realm.NewArray(), out _));
        Assert.False(realm.TryGetArrayBufferBytes(JsValue.String("bytes"), out _));
        Assert.False(realm.TryGetArrayBufferBytes(JsValue.Missing, out _));
        Assert.False(realm.TryGetArrayBufferBytes(JsValue.Null, out _));
    }

    /// <summary>
    /// A buffer survives a round trip larger than one host crossing per byte would allow.
    /// </summary>
    /// <remarks>
    /// <b>This is a budget test wearing a correctness test's clothes, and it is here because one
    /// provider has no binary member on its host surface.</b> That provider reaches its realm's
    /// <c>ArrayBuffer</c> intrinsic, and the obvious way to fill one - a crossing per byte - is not
    /// merely slow: every crossing of that host surface charges against a host-call allowance, and
    /// spending it aborts the program terminally rather than raising anything a page could catch. A
    /// blob of a few hundred kilobytes is an ordinary thing for a page to have. Sixty-four kilobytes
    /// is well past the point where a per-byte route would be visible and well inside what the
    /// suite should run in a millisecond.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void ABufferOfSixtyFourKilobytesMakesTheRoundTrip(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.BinaryData))
            return;

        var bytes = new byte[64 * 1024];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i * 31 % 256);

        var buffer = realm.NewArrayBuffer(bytes);

        Assert.True(realm.TryGetArrayBufferBytes(buffer, out var read));
        Assert.Equal(bytes, read);

        if (Lacks(realm, JsCapabilities.HostScriptSource))
            return;

        // Asserted from the guest as well, so a provider that kept the bytes somewhere the page
        // cannot see them fails here rather than passing on the host's own read.
        realm.DefineValue(realm.Global, "big", buffer);
        Assert.Equal(
            $"{bytes.Length}/{bytes[1]}/{bytes[bytes.Length - 1]}",
            Eval(
                realm,
                "(function () { var v = new Uint8Array(big); return v.length + '/' + v[1] + '/' + v[v.length - 1]; })()",
                "test:big-buffer"));
    }

    /// <summary>
    /// A page that rewrites the typed-array machinery cannot change what the host reads out of a
    /// buffer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the assertion that a provider reading a buffer through the realm's own intrinsics
    /// reads it by brand and not by anything a page can write.</b> A provider with no binary member
    /// on its host surface has to go through the guest's objects to get at the bytes, and the moment
    /// it asks one of them a <em>question</em> â€” how long are you? â€” it has put a page's code between
    /// the host and the answer.
    /// </para>
    /// <para>
    /// <c>%TypedArray%.prototype</c>'s <c>length</c> is an accessor and it is configurable, which the
    /// language requires, so <c>Object.defineProperty</c> on it is a thing a page may legally do.
    /// Answering zero is the dangerous direction: it does not throw, so a host that spread a view by
    /// its <c>length</c> would hand back a correctly sized array of zeros and report success. A blob
    /// built from that is silently empty, and nothing anywhere says so.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void APageThatRewritesTypedArrayLengthCannotChangeWhatTheHostReads(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.BinaryData) || Lacks(realm, JsCapabilities.HostScriptSource))
            return;

        var buffer = realm.EvaluateHostScript(
            "(function () { var b = new ArrayBuffer(5); var v = new Uint8Array(b);" +
            " for (var i = 0; i < 5; i++) { v[i] = i + 1; } return b; })()",
            "test:poison-buffer");

        realm.EvaluateHostScript(
            "Object.defineProperty(Object.getPrototypeOf(Uint8Array.prototype), 'length', " +
            "{ get: function () { return 0; }, configurable: true });",
            "test:poison-length");

        Assert.True(realm.TryGetArrayBufferBytes(buffer, out var bytes));
        Assert.Equal([1, 2, 3, 4, 5], bytes);

        // And the other direction, which turns a wrong answer into a crash rather than into zeros.
        realm.EvaluateHostScript(
            "Object.defineProperty(Object.getPrototypeOf(Uint8Array.prototype), 'length', " +
            "{ get: function () { return 1000000; }, configurable: true });",
            "test:poison-length-large");

        Assert.True(realm.TryGetArrayBufferBytes(buffer, out var again));
        Assert.Equal([1, 2, 3, 4, 5], again);

        // Minting is asserted under the same poisoning, because it writes through a view too.
        var minted = realm.NewArrayBuffer([9, 8, 7]);
        Assert.True(realm.TryGetArrayBufferBytes(minted, out var read));
        Assert.Equal([9, 8, 7], read);
    }
}

