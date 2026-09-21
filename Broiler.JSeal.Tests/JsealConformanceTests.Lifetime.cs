namespace Broiler.JSeal.Tests;

public partial class JsealConformanceTests
{
    public static IEnumerable<object[]> DisposedRealmReads =>
        from engine in Engines
        from operation in new[] { "Global", "PendingJobs", "Buffer", "NonBuffer", "Transfer", "NonTransfer", "RestrictedSource" }
        select new object[] { engine[0], operation };

    [Theory]
    [MemberData(nameof(DisposedRealmReads))]
    public void RealmReadsRequireALiveRealm(string engine, string operation)
    {
        using var realm = NewRealm(engine, new JsRealmOptions { AllowGuestEval = false });
        var buffer = realm.NewArrayBuffer([1, 2, 3]);
        realm.Dispose();

        Action read = operation switch
        {
            "Global" => () => _ = realm.Global,
            "PendingJobs" => () => _ = realm.HasPendingJobs,
            "Buffer" => () => realm.TryGetArrayBufferBytes(buffer, out _),
            "NonBuffer" => () => realm.TryGetArrayBufferBytes(JsValue.Null, out _),
            "Transfer" => () => realm.ClassifyTransferable(buffer),
            "NonTransfer" => () => realm.ClassifyTransferable(JsValue.Null),
            "RestrictedSource" => () => realm.EvaluateDynamicSource("42", "test:disposed"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        Assert.Throws<ObjectDisposedException>(read);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void DisposalPreservesMetadataAndDiscardsJobsWithoutRunningThem(string engine)
    {
        using var realm = NewRealm(engine);
        var name = realm.EngineName;
        var capabilities = realm.Capabilities;
        var handle = realm.NewObject();
        var ran = 0;
        realm.DefineValue(realm.Global, "touch", realm.NewMethod("touch", (in JsCall _) =>
        {
            ran++;
            return JsValue.Undefined;
        }));
        realm.EnqueueJob(() => ran++);
        realm.EvaluateClassicScript("Promise.resolve().then(touch)", "test:queued-before-dispose");
        Assert.True(realm.HasPendingJobs);

        realm.Dispose();
        realm.Dispose();

        Assert.Equal(name, realm.EngineName);
        Assert.Equal(capabilities, realm.Capabilities);
        Assert.Equal(JsValueKind.Object, handle.Kind);
        Assert.Equal(0, ran);
        Assert.Throws<ObjectDisposedException>(() => realm.DrainJobs());
        Assert.Equal(0, ran);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void DisposedRealmOperationsCannotReachGuestOrHostCode(string engine)
    {
        using var realm = NewRealm(engine);
        var ran = 0;
        JsNativeFunction touch = (in JsCall _) => { ran++; return JsValue.Undefined; };
        var function = realm.NewConstructor("touch", touch);
        realm.DefineValue(realm.Global, "touch", function);
        var target = realm.EvaluateClassicScript(
            "({ get x() { touch(); }, set x(v) { touch(); }, get 0() { touch(); }, valueOf() { touch(); return 1; }, toString() { touch(); return 'x'; } })",
            "test:disposed-target");
        var detached = Broiler.JSeal.Providers.JsProviderClone.Detached(engine, null);
        realm.Dispose();

        // Valid arguments keep this focused on lifetime, rather than argument-validation order.
        (string Name, Action Run)[] operations =
        [
            ("NewObject", () => realm.NewObject()),
            ("NewArray", () => realm.NewArray()),
            ("NewMethod", () => realm.NewMethod("late", touch)),
            ("NewConstructor", () => realm.NewConstructor("late", touch)),
            ("NewExotic", () => realm.NewExotic(new LifetimeExotic())),
            ("NewArrayBuffer", () => realm.NewArrayBuffer([1])),
            ("ToJsString", () => realm.ToJsString(target)),
            ("ToNumber", () => realm.ToNumber(target)),
            ("ToBoolean", () => realm.ToBoolean(JsValue.Null)),
            ("DefineValue", () => realm.DefineValue(target, "y", JsValue.Null)),
            ("DefineAccessor", () => realm.DefineAccessor(target, "y", touch, null)),
            ("DefineIndex", () => realm.DefineIndex(target, 1, JsValue.Null)),
            ("GetProperty", () => realm.GetProperty(target, "x")),
            ("GetIndex", () => realm.GetIndex(target, 0)),
            ("SetProperty", () => realm.SetProperty(target, "x", JsValue.Null)),
            ("HasProperty", () => realm.HasProperty(target, "x")),
            ("DeleteProperty", () => realm.DeleteProperty(target, "x")),
            ("OwnPropertyNames", () => realm.OwnPropertyNames(target)),
            ("SetPrototype", () => realm.SetPrototype(target, JsValue.Null)),
            ("GetPrototype", () => realm.GetPrototype(target)),
            ("Invoke", () => realm.Invoke(function, JsValue.Undefined)),
            ("Construct", () => realm.Construct(function)),
            ("Error", () => realm.Error(JsErrorKind.TypeError, "late")),
            ("DomError", () => realm.DomError("InvalidStateError", "late")),
            ("EnqueueJob", () => realm.EnqueueJob(() => ran++)),
            ("DrainJobs", () => realm.DrainJobs()),
            ("DrainZeroJobs", () => realm.DrainJobs(0)),
            ("NewPromise", () => realm.NewPromise(out _, out _)),
            ("HostSource", () => realm.EvaluateHostScript("touch()", "test:disposed")),
            ("ClassicSource", () => realm.EvaluateClassicScript("touch()", "test:disposed")),
            ("DynamicSource", () => realm.EvaluateDynamicSource("touch()", "test:disposed")),
            ("Clone", () => realm.Clone(target)),
            ("Detach", () => realm.Detach(target)),
            ("Adopt", () => realm.Adopt(detached)),
        ];

        foreach (var operation in operations)
        {
            var error = Record.Exception(operation.Run);
            Assert.True(error is ObjectDisposedException, $"{operation.Name}: expected ObjectDisposedException, got {error}");
            Assert.Equal(0, ran);
        }
    }

    public static IEnumerable<object[]> DisposedPromiseStates =>
        from engine in Engines
        from state in new[] { "pending", "resolved", "rejected" }
        select new object[] { engine[0], state };

    [Theory]
    [MemberData(nameof(DisposedPromiseStates))]
    public void RetainedPromiseSettlersRequireALiveRealmEvenAfterSettlement(string engine, string state)
    {
        using var realm = NewRealm(engine);
        var inspected = 0;
        var thenable = realm.NewObject();
        realm.DefineAccessor(thenable, "then", (in JsCall _) =>
        {
            inspected++;
            return JsValue.Undefined;
        }, null);
        realm.NewPromise(out var resolve, out var reject);
        if (state == "resolved") resolve(JsValue.Undefined);
        if (state == "rejected") reject(JsValue.Undefined);
        realm.Dispose();

        Assert.Throws<ObjectDisposedException>(() => resolve(thenable));
        Assert.Throws<ObjectDisposedException>(() => reject(thenable));
        Assert.Equal(0, inspected);
    }

    private sealed class LifetimeExotic : IJsExotic
    {
        public bool TryGetNamed(string name, out JsValue value) { value = JsValue.Missing; return false; }
        public bool TryGetIndex(uint index, out JsValue value) { value = JsValue.Missing; return false; }
        public bool TrySetNamed(string name, JsValue value) => false;
        public IReadOnlyList<string> SupportedNames => [];
        public uint IndexedLength => 0;
    }
}
