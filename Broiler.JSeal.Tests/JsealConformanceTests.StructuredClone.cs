namespace Broiler.JSeal.Tests;

/// <summary>
/// I18: structured clone as shared provider cases - cycles, the brands every provider supports,
/// uncloneable values, transfer failure atomicity and source detachment for
/// <see cref="JsCapabilities.StructuredClone"/>, and destination-realm ownership and foreign-engine
/// rejection for <see cref="JsCapabilities.WorkerRealms"/>.
/// </summary>
/// <remarks>
/// The algorithm is each engine's own; these cases state what the contract lets a host rely on, and
/// read results back through the realm's own script so that a brand is judged by the realm's
/// intrinsics rather than by a handle's kind.
/// </remarks>
public partial class JsealConformanceTests
{
    /// <summary>
    /// Clone cases a provider is known to answer wrongly, and why. Such a row must still FAIL, so a
    /// fixed provider cannot keep a stale entry; see <see cref="AssertCloneCase"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Engine, string Case), string> CloneGaps =
        new Dictionary<(string, string), string>
        {
            [("broiler-js", nameof(EverySharedBrandSurvivesACloneAsTheRealmsOwn))] =
                "the pinned Broiler.JavaScript 0.1.0-preview.3 structured clone rebuilds a RangeError as a plain Error " +
                "(HTML keeps the name of the six native error types); an upstream Broiler.JS clone fix and pin update",
            [("broiler-js", nameof(UncloneableValuesAreRefusedRatherThanCopiedInPart))] =
                "the pinned Broiler.JavaScript 0.1.0-preview.3 structured clone copies a Symbol, and copies a WeakMap, a WeakSet, " +
                "a Proxy and a Promise as plain objects, instead of raising DataCloneError; an upstream Broiler.JS clone fix and pin update",
            [("broiler-js", nameof(ACloneKeepsHolesAccessorValuesWrappersAndArrayProperties))] =
                "the pinned Broiler.JavaScript 0.1.0-preview.3 structured clone compacts an array's holes (shortening it), drops " +
                "accessor properties instead of reading them, drops an array's non-index properties, and copies Boolean, Number " +
                "and String objects as empty plain objects; an upstream Broiler.JS clone fix and pin update",
            [("broiler-js", nameof(ABigIntSurvivesAStructuredClone))] =
                "the pinned Broiler.JavaScript 0.1.0-preview.3 structured clone copies a BigInt object as a plain object " +
                "(no [[BigIntData]] slot, not on BigInt.prototype) where HTML rebuilds a BigInt object; a BigInt primitive survives. " +
                "An upstream Broiler.JS clone fix and pin update",
        };

    /// <summary>Runs a case, or, for a recorded gap, requires the case's own assertions to fail.</summary>
    private static void AssertCloneCase(string engine, string contractCase, Action run)
    {
        if (!CloneGaps.TryGetValue((engine, contractCase), out var gap))
        {
            run();
            return;
        }

        var failure = Record.Exception(run);
        Assert.True(failure is not null, $"'{engine}' / {contractCase} now passes; remove its gap ({gap}).");
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(failure);
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.StructuredClone | JsCapabilities.HostScriptSource)]
    public void ACloneKeepsCyclesAndSharedReferences(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.StructuredClone | JsCapabilities.HostScriptSource);

        var original = realm.EvaluateHostScript(
            "(function () { var shared = { n: 1 }; var o = { a: shared, b: shared, list: [shared] }; o.self = o; return o; })()",
            "test:clone-cycle");
        realm.DefineValue(realm.Global, "cloneOriginal", original);
        realm.DefineValue(realm.Global, "cloneCopy", realm.Clone(original));

        Assert.Equal(
            "true,true,true,false,false,1",
            Eval(realm,
                "[cloneCopy.self === cloneCopy, cloneCopy.a === cloneCopy.b, cloneCopy.list[0] === cloneCopy.a," +
                " cloneCopy === cloneOriginal, cloneCopy.a === cloneOriginal.a, cloneCopy.a.n].join()",
                "test:clone-cycle-read"));
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.StructuredClone | JsCapabilities.HostScriptSource)]
    public void EverySharedBrandSurvivesACloneAsTheRealmsOwn(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.StructuredClone | JsCapabilities.HostScriptSource);

        var original = realm.EvaluateHostScript(
            "(function () {" +
            " var buffer = new ArrayBuffer(4); var bytes = new Uint8Array(buffer); bytes[0] = 9; bytes[3] = 7;" +
            " return { date: new Date(5), re: /a+/gi, map: new Map([[1, 'x']]), set: new Set(['y'])," +
            "  error: new RangeError('range'), buffer: buffer, bytes: bytes, list: [1, 'two', true, null]," +
            "  nested: { deeper: { value: -0 } } };" +
            "})()",
            "test:clone-brands");
        realm.DefineValue(realm.Global, "brandCopy", realm.Clone(original));
        realm.DefineValue(realm.Global, "brandOriginal", original);

        AssertCloneCase(engine, nameof(EverySharedBrandSurvivesACloneAsTheRealmsOwn), () => Assert.Equal(
            "true,5,a+:gi,x,true,RangeError:range:true,9:7:true,1:two:true::null,true",
            Eval(realm,
                "[brandCopy.date instanceof Date, brandCopy.date.getTime()," +
                " brandCopy.re.source + ':' + brandCopy.re.flags," +
                " brandCopy.map instanceof Map ? brandCopy.map.get(1) : 'no map'," +
                " brandCopy.set instanceof Set && brandCopy.set.has('y')," +
                " brandCopy.error.name + ':' + brandCopy.error.message + ':' + (brandCopy.error instanceof RangeError)," +
                " brandCopy.bytes[0] + ':' + brandCopy.bytes[3] + ':' + (brandCopy.bytes.buffer === brandCopy.buffer)," +
                " brandCopy.list.join(':') + ':' + String(brandCopy.list[3])," +
                " Object.is(brandCopy.nested.deeper.value, -0)].join()",
                "test:clone-brands-read")));

        // Copies, not views of the original: every one is a new object of this realm.
        Assert.Equal(
            "false,false,false,true",
            Eval(realm,
                "[brandCopy.buffer === brandOriginal.buffer, brandCopy.map === brandOriginal.map, brandCopy.date === brandOriginal.date," +
                " Object.getPrototypeOf(brandCopy) === Object.prototype].join()",
                "test:clone-brands-identity"));
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.StructuredClone | JsCapabilities.HostScriptSource)]
    public void ACloneKeepsHolesAccessorValuesWrappersAndArrayProperties(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.StructuredClone | JsCapabilities.HostScriptSource);

        // StructuredSerializeInternal keeps an array's length and non-index properties, reads an
        // accessor's value through [[Get]], and keeps a primitive wrapper's brand and value.
        var original = realm.EvaluateHostScript(
            "(function () { var list = [1, , 3]; list.extra = 'e';" +
            " return { list: list, get read() { return 'got'; }, flag: new Boolean(false), count: new Number(3), text: new String('s') }; })()",
            "test:clone-shapes");
        realm.DefineValue(realm.Global, "shapeCopy", realm.Clone(original));

        AssertCloneCase(engine, nameof(ACloneKeepsHolesAccessorValuesWrappersAndArrayProperties), () => Assert.Equal(
            "3,false,3,e,got,Boolean:false,Number:3,String:s",
            Eval(realm,
                "[shapeCopy.list.length, 1 in shapeCopy.list, shapeCopy.list[2], shapeCopy.list.extra, shapeCopy.read," +
                " Object.prototype.toString.call(shapeCopy.flag).slice(8, -1) + ':' + shapeCopy.flag.valueOf()," +
                " Object.prototype.toString.call(shapeCopy.count).slice(8, -1) + ':' + shapeCopy.count.valueOf()," +
                " Object.prototype.toString.call(shapeCopy.text).slice(8, -1) + ':' + shapeCopy.text.valueOf()].join()",
                "test:clone-shapes-read")));
    }

    /// <summary>
    /// <see cref="JsValue.Missing"/> is handed to the engine as <c>undefined</c>, as everywhere else,
    /// so it clones as <c>undefined</c> and a transfer-list entry of it is the engine's refusal of a
    /// non-buffer - never a host argument exception.
    /// </summary>
    /// <remarks>
    /// The Broiler.VM profile began refusing its own <c>Missing</c> marker with an
    /// <see cref="ArgumentException"/> (VM-FIX-H); a provider passing the marker through would leak
    /// that exception to the host.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.StructuredClone)]
    public void AMissingValueClonesAsUndefinedAndIsNotTransferable(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.StructuredClone);

        Assert.Equal(JsValueKind.Undefined, realm.Clone(JsValue.Missing).Kind);
        Assert.Equal(JsValueKind.Undefined, realm.Clone(JsValue.Undefined).Kind);
        Assert.Throws<JsEngineException>(() => realm.Clone(JsValue.Number(1d), [JsValue.Missing]));
        Assert.Throws<JsEngineException>(() => realm.Clone(JsValue.Number(1d), [JsValue.Undefined]));
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.StructuredClone | JsCapabilities.HostScriptSource)]
    public void UncloneableValuesAreRefusedRatherThanCopiedInPart(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.StructuredClone | JsCapabilities.HostScriptSource);

        var cloned = new List<string>();

        foreach (var source in new[]
        {
            "Symbol('s')",
            "(function () {})",
            "new WeakMap()",
            "new WeakSet()",
            "new Proxy({ a: 1 }, {})",
            "Promise.resolve(1)",
            "({ keep: 1, lose: function () {} })",
            "[1, Symbol('in an array')]",
        })
        {
            var value = realm.EvaluateHostScript(source, "test:clone-refused");
            var refused = Record.Exception(() => realm.Clone(value));
            if (refused is not JsEngineException)
                cloned.Add($"{source} ({refused?.GetType().Name ?? "cloned"})");
        }

        AssertCloneCase(engine, nameof(UncloneableValuesAreRefusedRatherThanCopiedInPart),
            () => Assert.True(cloned.Count == 0, "Not refused: " + string.Join("; ", cloned)));
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.StructuredClone | JsCapabilities.HostScriptSource)]
    public void AFailedTransferDetachesNothing(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.StructuredClone | JsCapabilities.HostScriptSource);

        var kept = realm.EvaluateHostScript("(function () { var b = new ArrayBuffer(3); new Uint8Array(b)[1] = 5; return b; })()", "test:transfer-kept");
        var spent = realm.EvaluateHostScript("new ArrayBuffer(2)", "test:transfer-spent");
        realm.Clone(JsValue.Undefined, [spent]);
        Assert.Equal(JsTransferKind.Detached, realm.ClassifyTransferable(spent));

        // An already-detached entry, and an uncloneable value, each fail the whole clone before the
        // good entry is detached.
        var payload = realm.NewObject();
        realm.DefineValue(payload, "kept", kept);
        Assert.Throws<JsEngineException>(() => realm.Clone(payload, [kept, spent]));
        Assert.Equal(JsTransferKind.Transferable, realm.ClassifyTransferable(kept));

        realm.DefineValue(payload, "function", realm.NewMethod("f", static (in _) => JsValue.Undefined));
        Assert.Throws<JsEngineException>(() => realm.Clone(payload, [kept]));
        Assert.Equal(JsTransferKind.Transferable, realm.ClassifyTransferable(kept));

        Assert.True(realm.TryGetArrayBufferBytes(kept, out var bytes));
        Assert.Equal(new byte[] { 0, 5, 0 }, bytes);
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.WorkerRealms | JsCapabilities.HostScriptSource)]
    public void AnAdoptedValueIsBuiltFromTheAdoptingRealmsIntrinsics(string engine)
    {
        var provider = Provider(engine);
        using var sender = provider.CreateRealm(JsRealmOptions.Default);
        using var receiver = provider.CreateRealm(JsRealmOptions.Default);
        AssertHas(receiver, JsCapabilities.WorkerRealms | JsCapabilities.HostScriptSource);

        var carrier = sender.Detach(sender.EvaluateHostScript("({ list: [1, 2], when: new Date(3), inner: { n: 4 } })", "test:adopt-source"));
        receiver.DefineValue(receiver.Global, "adopted", receiver.Adopt(carrier));

        Assert.Equal(
            "true,true,true,4",
            Eval(receiver,
                "[Object.getPrototypeOf(adopted) === Object.prototype, adopted.list instanceof Array," +
                " adopted.when instanceof Date, adopted.inner.n].join()",
                "test:adopt-read"));
        Assert.True(sender.GetProperty(sender.Global, "adopted").IsUndefined);
    }

#if BROILER_VM_JS
    /// <summary>
    /// The Broiler.VM provider with WorkerRealms declared, through its internal test gate: the two
    /// halves of worker transport run, and the one clause they do not meet is pinned below.
    /// </summary>
    private static IJsEngineProvider VmWorkerRealmsProvider() =>
        new Broiler.JSeal.Vm.VmEngineProvider { EnableWorkerRealms = true };

    [Fact]
    public void TheRegisteredVmProviderDeclaresSameRealmCloneButNotWorkerRealms()
    {
        var registered = Provider("broiler-vm");
        Assert.True(registered.Capabilities.HasFlag(JsCapabilities.StructuredClone));
        Assert.False(registered.Capabilities.HasFlag(JsCapabilities.WorkerRealms));

        using var realm = registered.CreateRealm(JsRealmOptions.Default);
        var copy = realm.Clone(realm.NewObject());
        Assert.True(copy.IsObject);
        Assert.Throws<JsCapabilityUnavailableException>(() => realm.Detach(copy));
    }

    [Fact]
    public void VmWorkerTransportRunsOnTwoThreadsThroughTheGate()
    {
        var provider = VmWorkerRealmsProvider();
        var page = provider.CreateRealm(JsRealmOptions.Default);
        var message = page.EvaluateHostScript(
            "(function () { var o = { text: 'from the page', when: new Date(8) }; o.self = o; return o; })()",
            "test:vm-worker-send");
        var sent = page.Detach(message);

        // The sender is gone before the receiver adopts: a carrier holds data only.
        page.Dispose();

        string received = string.Empty;
        JsDetachedValue? reply = null;
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            try
            {
                using var second = provider.CreateRealm(JsRealmOptions.Default);
                second.DefineValue(second.Global, "inbound", second.Adopt(sent));
                received = Eval(second,
                    "[inbound.text, inbound.self === inbound, inbound.when instanceof Date, Object.getPrototypeOf(inbound) === Object.prototype].join()",
                    "test:vm-worker-read");
                reply = second.Detach(second.EvaluateHostScript("({ text: 'from the worker' })", "test:vm-worker-reply"));
            }
            catch (Exception raised)
            {
                failure = raised;
            }
        });

        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "the worker realm did not finish");
        Assert.Null(failure);
        Assert.Equal("from the page,true,true,true", received);

        using var home = provider.CreateRealm(JsRealmOptions.Default);
        Assert.Equal("from the worker", home.ToJsString(home.GetProperty(home.Adopt(reply!), "text")));

        // Repeatable without a transfer list: two adoptions, two independent copies.
        Assert.False(home.Adopt(reply!) == home.Adopt(reply!));
    }

    [Fact]
    public void AVmCarrierHoldingATransferIsSingleUseWhichIsWhyWorkerRealmsIsUndeclared()
    {
        var provider = VmWorkerRealmsProvider();
        using var realm = provider.CreateRealm(JsRealmOptions.Default);
        var buffer = realm.EvaluateHostScript("(function () { var b = new ArrayBuffer(2); new Uint8Array(b)[0] = 4; return b; })()", "test:vm-single-use");

        var carrier = realm.Detach(buffer, [buffer]);
        Assert.Equal(JsTransferKind.Detached, realm.ClassifyTransferable(buffer));

        Assert.True(realm.TryGetArrayBufferBytes(realm.Adopt(carrier), out var bytes));
        Assert.Equal(new byte[] { 4, 0 }, bytes);

        // IJsClone.Adopt says a carrier may be adopted more than once; this one may not. The profile
        // refuses the second claim, and the refusal is explicit rather than a second copy.
        var second = Assert.Throws<JsEngineException>(() => realm.Adopt(carrier));
        var refusal = Assert.IsType<Broiler.VM.Profile.JavaScript.JsHostSurfaceException>(second.InnerException);
        Assert.Equal(Broiler.VM.Profile.JavaScript.JsHostRefusal.CarrierConsumed, refusal.Refusal);
    }

    [Fact]
    public void ACarrierFromTheOtherEngineIsRefusedByBothProviders()
    {
        using var vm = VmWorkerRealmsProvider().CreateRealm(JsRealmOptions.Default);
        using var js = Provider("broiler-js").CreateRealm(JsRealmOptions.Default);

        var fromVm = vm.Detach(vm.NewObject());
        var fromJs = js.Detach(js.NewObject());

        Assert.Throws<JsEngineException>(() => js.Adopt(fromVm));
        Assert.Throws<JsEngineException>(() => vm.Adopt(fromJs));

        // A carrier that names this engine but carries another engine's graph is the profile's own
        // ForeignCarrier refusal.
        Assert.Throws<JsEngineException>(() => vm.Adopt(Broiler.JSeal.Providers.JsProviderClone.Detached(vm.EngineName, new object())));
    }
#endif
}
