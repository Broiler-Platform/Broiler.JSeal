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
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.ReentrantHostCalls | JsCapabilities.HostScriptSource)]
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

        AssertHas(realm, JsCapabilities.ReentrantHostCalls | JsCapabilities.HostScriptSource);

        realm.DefineValue(realm.Global, "twice", realm.NewMethod("twice", Twice, 1));
        Assert.Equal("42", Eval(realm, "String(twice(function (n) { return n; }))", "test:reentrant"));
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.WorkerRealms | JsCapabilities.HostScriptSource)]
    public void ASecondRealmRunsOnASecondThread(string engine)
    {
        var provider = Provider(engine);
        using var first = provider.CreateRealm(JsRealmOptions.Default);

        AssertHas(first, JsCapabilities.WorkerRealms | JsCapabilities.HostScriptSource);

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
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.StructuredClone)]
    public void ACloneIsACopyRatherThanTheSameObject(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.StructuredClone);

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
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.StructuredClone)]
    public void CloningRefusesAValueTheAlgorithmDoesNotCover(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.StructuredClone);

        // A function is the canonical uncloneable value, and a page reaches this by writing
        // postMessage(function () {}). The failure has to be an exception the host can catch and
        // turn into a DataCloneError, not a silently empty object.
        var uncloneable = realm.NewMethod("f", static (in _) => JsValue.Undefined);
        Assert.Throws<JsEngineException>(() => realm.Clone(uncloneable));
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.WorkerRealms)]
    public void DetachingRefusesAValueTheAlgorithmDoesNotCover(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.WorkerRealms);

        // The sending half of a worker message refuses it the same way Clone does.
        var uncloneable = realm.NewMethod("f", static (in _) => JsValue.Undefined);
        Assert.Throws<JsEngineException>(() => realm.Detach(uncloneable));
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.StructuredClone | JsCapabilities.HostScriptSource)]
    public void ATransferListDetachesItsSourceAndCarriesTheContents(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.StructuredClone | JsCapabilities.HostScriptSource);

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
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.StructuredClone)]
    public void OnlyATransferableObjectClassifiesAsOne(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.StructuredClone);

        // The question a postMessage transfer list asks of every entry it does not recognise itself.
        // Everything that is not transferable answers the same way, including the hole a sparse
        // array hands over and the primitive a page puts in the list by mistake.
        Assert.Equal(JsTransferKind.NotTransferable, realm.ClassifyTransferable(realm.NewObject()));
        Assert.Equal(JsTransferKind.NotTransferable, realm.ClassifyTransferable(realm.NewArray()));
        Assert.Equal(JsTransferKind.NotTransferable, realm.ClassifyTransferable(JsValue.Number(1d)));
        Assert.Equal(JsTransferKind.NotTransferable, realm.ClassifyTransferable(JsValue.Missing));
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.WorkerRealms)]
    public void ADetachedCarrierBelongsToItsEngineAndToNoRealm(string engine)
    {
        var provider = Provider(engine);
        using var realm = provider.CreateRealm(JsRealmOptions.Default);

        AssertHas(realm, JsCapabilities.WorkerRealms);

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
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.HostScriptSource)]
    public void OwnPropertyNamesOfAnArrayAreItsPresentIndicesAndNotItsLength(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.HostScriptSource);

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
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.HostScriptSource)]
    public void DefiningTheNextIndexOnAnArrayExtendsIt(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.HostScriptSource);

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
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.BinaryData | JsCapabilities.HostScriptSource)]
    public void AMintedArrayBufferIsTheRealmsOwnAndAViewOverItSeesTheBytes(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.BinaryData | JsCapabilities.HostScriptSource);

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
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.BinaryData | JsCapabilities.HostScriptSource)]
    public void TheHostReadsBackABufferAScriptMadeAndTellsOneFromEverythingElse(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.BinaryData | JsCapabilities.HostScriptSource);

        var buffer = realm.EvaluateHostScript(
            "(function () { var b = new ArrayBuffer(3); var v = new Uint8Array(b); v[0] = 7; v[2] = 8; return b; })()",
            "test:script-buffer");

        Assert.True(realm.TryGetArrayBufferBytes(buffer, out var bytes));
        Assert.Equal([7, 0, 8], bytes);

        // A view is not a buffer, and the buffer it names is.
        var view = realm.EvaluateHostScript("new Uint8Array([4, 5, 6, 7])", "test:script-view");
        Assert.False(realm.TryGetArrayBufferBytes(view, out var notBuffer));
        Assert.Null(notBuffer); // The contract's [NotNullWhen(true)]: a false answer carries no snapshot.
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
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.BinaryData | JsCapabilities.HostScriptSource)]
    public void ABufferOfSixtyFourKilobytesMakesTheRoundTrip(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.BinaryData | JsCapabilities.HostScriptSource);

        var bytes = new byte[64 * 1024];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i * 31 % 256);

        var buffer = realm.NewArrayBuffer(bytes);

        Assert.True(realm.TryGetArrayBufferBytes(buffer, out var read));
        Assert.Equal(bytes, read);

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
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.BinaryData | JsCapabilities.HostScriptSource)]
    public void APageThatRewritesTypedArrayLengthCannotChangeWhatTheHostReads(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.BinaryData | JsCapabilities.HostScriptSource);

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

    /// <summary>
    /// A page that redefines the typed-array species cannot change what the host reads out of a
    /// buffer, nor make the read run the page's code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>%TypedArray%.prototype.subarray</c> consults the view's <c>constructor</c> and then
    /// <c>Symbol.species</c></b>, which the language requires and which a page may legally
    /// redefine on <c>Uint8Array</c> or on <c>Uint8Array.prototype</c>. A provider that cut its
    /// chunks with <c>subarray</c> - even the pinned intrinsic - would therefore let the page choose
    /// the view the host renders: run guest code inside a host read, throw from it, or answer a
    /// view of other bytes, or of more bytes than the chunk, which overran the host's array.
    /// </para>
    /// <para>
    /// The chunk view is constructed directly with the pinned <c>Uint8Array</c> over the buffer,
    /// offset and length, and that construction consults no species.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.BinaryData | JsCapabilities.HostScriptSource)]
    public void APageThatRedefinesTypedArraySpeciesCannotChangeWhatTheHostReads(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.BinaryData | JsCapabilities.HostScriptSource);

        var buffer = realm.EvaluateHostScript(
            "(function () { var b = new ArrayBuffer(3); var v = new Uint8Array(b);" +
            " v[0] = 104; v[1] = 105; v[2] = 33; return b; })()",
            "test:species-buffer");

        // A longer answer than the chunk: the direction that overruns the host's array.
        realm.EvaluateHostScript(
            "globalThis.speciesCalls = 0;" +
            "Object.defineProperty(Uint8Array, Symbol.species, { configurable: true, get: function () {" +
            " globalThis.speciesCalls++; return function () { return new Uint8Array([6, 6, 6, 6, 6]); }; } });",
            "test:species-longer");

        Assert.True(realm.TryGetArrayBufferBytes(buffer, out var bytes));
        Assert.Equal([104, 105, 33], bytes);

        // A throwing species, reached through the prototype's constructor rather than the static.
        realm.EvaluateHostScript(
            "Object.defineProperty(Uint8Array.prototype, 'constructor', { configurable: true, get: function () {" +
            " globalThis.speciesCalls++; throw new Error('page'); } });",
            "test:species-throwing");

        Assert.True(realm.TryGetArrayBufferBytes(buffer, out var again));
        Assert.Equal([104, 105, 33], again);

        Assert.Equal(0, realm.GetProperty(realm.Global, "speciesCalls").AsNumber);
    }

    /// <summary>
    /// A page that replaces every binary intrinsic cannot change what the host mints or reads, nor
    /// make either operation run the page's code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written against the realm's own globals, prototypes and accessors, all of which the
    /// language lets a page redefine.</b> A provider that built or read a buffer through anything the
    /// page can reach when the host asks - <c>ArrayBuffer</c>, <c>Uint8Array</c>, a typed array's
    /// <c>set</c>, <c>join</c> or <c>subarray</c>, the buffer's <c>byteLength</c> getter, or
    /// <c>Object.getOwnPropertyDescriptor</c> - would either run page code inside a host crossing or
    /// answer with the page's bytes. Every replacement counts its calls, and the count must stay zero.
    /// </para>
    /// <para>
    /// The minted buffer is asserted to be on the realm's original <c>ArrayBuffer.prototype</c>,
    /// which the page kept a reference to before replacing the global: a buffer built through the
    /// replaced constructor would carry the page's prototype instead.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.BinaryData | JsCapabilities.HostScriptSource)]
    public void APageThatReplacesTheBinaryIntrinsicsCannotChangeWhatTheHostMintsOrReads(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.BinaryData | JsCapabilities.HostScriptSource);

        var buffer = realm.EvaluateHostScript(
            "(function () { var b = new ArrayBuffer(4); var v = new Uint8Array(b);" +
            " v[0] = 1; v[1] = 2; v[2] = 3; v[3] = 250; return b; })()",
            "test:patched-buffer");

        realm.EvaluateHostScript(
            "globalThis.pageCalls = 0;" +
            "var OriginalBufferPrototype = ArrayBuffer.prototype;" +
            "function counted(answer) { return function () { globalThis.pageCalls++; return answer; }; }" +
            "var typed = Object.getPrototypeOf(Uint8Array.prototype);" +
            "['set', 'join', 'subarray', 'slice', 'fill'].forEach(function (name) {" +
            "  typed[name] = counted(undefined); Uint8Array.prototype[name] = counted(undefined); });" +
            "Object.defineProperty(ArrayBuffer.prototype, 'byteLength', { configurable: true, get: counted(99) });" +
            "ArrayBuffer.prototype.slice = counted(undefined);" +
            "Object.getOwnPropertyDescriptor = counted(undefined);" +
            "globalThis.ArrayBuffer = function () { globalThis.pageCalls++; this.page = true; };" +
            "globalThis.Uint8Array = function () { globalThis.pageCalls++; this.page = true; };",
            "test:patch-binary");

        Assert.True(realm.TryGetArrayBufferBytes(buffer, out var read));
        Assert.Equal([1, 2, 3, 250], read);

        var minted = realm.NewArrayBuffer([9, 8, 7]);
        Assert.True(realm.TryGetArrayBufferBytes(minted, out var mintedBytes));
        Assert.Equal([9, 8, 7], mintedBytes);

        realm.DefineValue(realm.Global, "minted", minted);
        Assert.Equal(0, realm.GetProperty(realm.Global, "pageCalls").AsNumber);

        // RECORDED GAP, Broiler.JS provider: the pinned engine's buffer constructor takes its
        // prototype from the global named ArrayBuffer at the moment of the mint, so a page that
        // replaced the global gives the host's buffer the page's prototype. Pinned here so that a
        // fix is noticed; the bytes and the page-call count above are right on both providers.
        Assert.Equal(
            engine == "broiler-js" ? "false" : "true",
            Eval(realm, "String(Object.getPrototypeOf(minted) === OriginalBufferPrototype)", "test:patched-prototype"));
    }

    /// <summary>
    /// The brand is the engine's buffer type: a subclass instance and a buffer whose prototype was
    /// changed are buffers, and a proxy around one is not.
    /// </summary>
    /// <remarks>
    /// <b>Each case would be answered the other way by a check a page can influence.</b> A prototype
    /// test rejects the re-parented buffer and accepts nothing it should not; a proxy forwards
    /// <c>byteLength</c> reads and prototype queries to its target, so any check made through
    /// ordinary property access would call it a buffer. The language's own brand check
    /// (<c>ArrayBuffer.prototype.byteLength</c> on a proxy throws) says it is not.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.BinaryData | JsCapabilities.HostScriptSource)]
    public void TheBufferBrandIsTheEnginesTypeAndNotAnythingAPageCanDress(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.BinaryData | JsCapabilities.HostScriptSource);

        var subclassed = realm.EvaluateHostScript(
            "(function () { class Sub extends ArrayBuffer {} var b = new Sub(2); new Uint8Array(b)[1] = 5; return b; })()",
            "test:brand-subclass");
        Assert.True(realm.TryGetArrayBufferBytes(subclassed, out var subclassBytes));
        Assert.Equal([0, 5], subclassBytes);

        var reparented = realm.EvaluateHostScript(
            "(function () { var b = new ArrayBuffer(1); new Uint8Array(b)[0] = 6; Object.setPrototypeOf(b, null); return b; })()",
            "test:brand-reparented");
        Assert.True(realm.TryGetArrayBufferBytes(reparented, out var reparentedBytes));
        Assert.Equal([6], reparentedBytes);

        var proxied = realm.EvaluateHostScript("new Proxy(new ArrayBuffer(3), {})", "test:brand-proxy");
        Assert.False(realm.TryGetArrayBufferBytes(proxied, out var proxiedBytes));
        Assert.Null(proxiedBytes);

        Assert.False(realm.TryGetArrayBufferBytes(JsValue.Number(4d), out _));
        Assert.False(realm.TryGetArrayBufferBytes(JsValue.Undefined, out _));
        Assert.False(realm.TryGetArrayBufferBytes(JsValue.Boolean(true), out _));
    }

    /// <summary>
    /// A detached buffer answers <see langword="true"/> with no bytes, the buffer it moved to answers
    /// its bytes, and an empty buffer answers <see langword="true"/> with no bytes too.
    /// </summary>
    /// <remarks>
    /// <b>The contract's detached rule, asserted directly.</b> <c>transfer</c> is the language's own
    /// door to detachment, so this needs no host detach operation. A provider that answered a
    /// detached buffer as "not a buffer" would turn a zero-length blob into the string
    /// <c>"[object ArrayBuffer]"</c>; one that answered its old bytes would read freed storage.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.BinaryData | JsCapabilities.HostScriptSource)]
    public void ADetachedBufferAnswersTrueWithNoBytesAndTheBufferItMovedToAnswersThem(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.BinaryData | JsCapabilities.HostScriptSource);

        realm.EvaluateHostScript(
            "var original = new ArrayBuffer(3); new Uint8Array(original).set([4, 5, 6]);" +
            "var moved = original.transfer();",
            "test:detach");

        Assert.Equal("0", Eval(realm, "String(original.byteLength)", "test:detached-length"));

        var original = realm.GetProperty(realm.Global, "original");
        Assert.True(realm.TryGetArrayBufferBytes(original, out var detachedBytes));
        Assert.Empty(detachedBytes);

        Assert.True(realm.TryGetArrayBufferBytes(realm.GetProperty(realm.Global, "moved"), out var movedBytes));
        Assert.Equal([4, 5, 6], movedBytes);

        var empty = realm.NewArrayBuffer([]);
        Assert.True(realm.TryGetArrayBufferBytes(empty, out var emptyBytes));
        Assert.Empty(emptyBytes);
        realm.DefineValue(realm.Global, "empty", empty);
        Assert.Equal("0/0", Eval(realm, "empty.byteLength + '/' + new Uint8Array(empty).length", "test:empty-buffer"));
    }

    /// <summary>
    /// A read hands the host a snapshot it owns, and a mint copies the host's bytes: neither side's
    /// later writes reach the other.
    /// </summary>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.BinaryData | JsCapabilities.HostScriptSource)]
    public void AReadIsASnapshotAndAMintIsACopyInBothDirections(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.BinaryData | JsCapabilities.HostScriptSource);

        var source = new byte[20_000];
        for (var i = 0; i < source.Length; i++)
            source[i] = (byte)(i % 251);

        var minted = realm.NewArrayBuffer(source);
        Array.Fill(source, (byte)0xEE);
        realm.DefineValue(realm.Global, "minted", minted);

        Assert.True(realm.TryGetArrayBufferBytes(minted, out var first));
        Assert.Equal(20_000, first.Length);
        Assert.Equal((byte)(19_999 % 251), first[19_999]);

        // The guest writes after the host read; the host's snapshot must not move with it.
        realm.EvaluateHostScript("new Uint8Array(minted).fill(7);", "test:guest-writes");
        Assert.Equal((byte)(19_999 % 251), first[19_999]);

        // And the host writes into its snapshot; the guest's buffer must not move with it.
        first[0] = 99;
        Assert.Equal("7/7", Eval(realm, "(function () { var v = new Uint8Array(minted); return v[0] + '/' + v[19999]; })()", "test:host-writes"));
    }
}
