namespace Broiler.JSeal.Tests;

public partial class JsealConformanceTests
{
    public static IEnumerable<object[]> ThrowingMembers =>
        from engine in Engines
        from operation in new[] { "getter", "index-getter", "setter", "get", "index", "set", "has", "delete", "keys", "descriptor", "get-prototype", "set-prototype", "define", "define-index", "define-accessor" }
        from payload in new[] { "42", "undefined", "({ marker: 42 })" }
        select new object[] { engine[0], operation, payload };

    [Theory]
    [MemberData(nameof(ThrowingMembers))]
    public void MemberExceptionsPreserveGuestValuesAcrossHostCalls(string engine, string operation, string payloadSource)
    {
        using var realm = NewRealm(engine);
        var payload = realm.EvaluateClassicScript(payloadSource, "test:member-payload");
        realm.DefineValue(realm.Global, "payload", payload);
        var source = operation switch
        {
            "getter" => "({ get x() { throw payload; } })",
            "index-getter" => "({ get 0() { throw payload; } })",
            "setter" => "({ set x(value) { throw payload; } })",
            "descriptor" => "new Proxy({ x: 1 }, { ownKeys: function () { return ['x']; }, getOwnPropertyDescriptor: function () { throw payload; } })",
            _ => "new Proxy({}, { " + (operation switch
            {
                "get" or "index" => "get",
                "set" => "set",
                "has" => "has",
                "delete" => "deleteProperty",
                "keys" => "ownKeys",
                "get-prototype" => "getPrototypeOf",
                "set-prototype" => "setPrototypeOf",
                "define" or "define-index" or "define-accessor" => "defineProperty",
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            }) + ": function () { throw payload; } })",
        };
        var target = realm.EvaluateClassicScript(source, "test:throwing-member");

        var failure = Assert.Throws<JsEngineException>(() => RunThrowingMember(realm, target, operation));
        Assert.Equal(payload, failure.Thrown);
        if (payload.IsObject)
            Assert.Same(payload.ObjectIdentity, failure.Thrown.ObjectIdentity);

        JsValue HostOperation(in JsCall call)
        {
            RunThrowingMember(call.Realm, target, operation);
            return JsValue.Undefined;
        }
        realm.DefineValue(realm.Global, "hostOperation", realm.NewMethod("hostOperation", HostOperation));
        Assert.True(realm.EvaluateClassicScript("""
            (function () {
                try { hostOperation(); return false; }
                catch (error) { return error === payload; }
            })()
            """, "test:member-throw-roundtrip").AsBoolean);
        Assert.Equal(JsValue.Number(42), realm.EvaluateClassicScript("20 + 22", "test:after-member-throw"));
    }

    private static void RunThrowingMember(IJsRealm realm, JsValue target, string operation)
    {
        switch (operation)
        {
            case "getter": case "get": realm.GetProperty(target, "x"); break;
            case "index-getter": case "index": realm.GetIndex(target, 0); break;
            case "setter": case "set": realm.SetProperty(target, "x", JsValue.Number(1)); break;
            case "has": realm.HasProperty(target, "x"); break;
            case "delete": realm.DeleteProperty(target, "x"); break;
            case "keys": case "descriptor": realm.OwnPropertyNames(target); break;
            case "get-prototype": realm.GetPrototype(target); break;
            case "set-prototype": realm.SetPrototype(target, JsValue.Null); break;
            case "define": realm.DefineValue(target, "x", JsValue.Number(1)); break;
            case "define-index": realm.DefineIndex(target, 0, JsValue.Number(1)); break;
            case "define-accessor": realm.DefineAccessor(target, "x", static (in JsCall _) => JsValue.Undefined, null); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void MemberExceptionsRestoreTheEnclosingRealmAndHostContext(string engine)
    {
        using var outer = NewRealm(engine);
        using var inner = NewRealm(engine);
        var target = inner.EvaluateClassicScript("({ get x() { throw 42; } })", "test:inner-getter");
        var previousContext = SynchronizationContext.Current;
        var hostContext = new SynchronizationContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(hostContext);
            JsValue Reenter(in JsCall call)
            {
                var outerContext = SynchronizationContext.Current;
                var failure = Assert.Throws<JsEngineException>(() => inner.GetProperty(target, "x"));
                Assert.Equal(JsValue.Number(42), failure.Thrown);
                Assert.Same(outerContext, SynchronizationContext.Current);
                return call.Realm.NewObject();
            }
            outer.DefineValue(outer.Global, "reenter", outer.NewMethod("reenter", Reenter));
            Assert.True(outer.EvaluateClassicScript("Object.getPrototypeOf(reenter()) === Object.prototype", "test:outer-realm").AsBoolean);
            Assert.Same(hostContext, SynchronizationContext.Current);
            Assert.Throws<JsEngineException>(() => inner.GetProperty(target, "x"));
            Assert.Same(hostContext, SynchronizationContext.Current);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void MemberExceptionsDoNotTranslateHostProgrammingErrors(string engine)
    {
        using var realm = NewRealm(engine);
        var target = realm.NewObject();
        Assert.Throws<ArgumentNullException>(() => realm.DefineAccessor(target, "x", null!, null));

        var hostFailure = new InvalidOperationException("host accessor failed");
        realm.DefineAccessor(target, "x", (in JsCall _) => throw hostFailure, null);
        Assert.Same(hostFailure, Assert.Throws<InvalidOperationException>(() => realm.GetProperty(target, "x")));

        var refusal = new JsEngineException("host refusal without a guest value");
        realm.DefineAccessor(target, "y", (in JsCall _) => throw refusal, null);
        Assert.Same(refusal, Assert.Throws<JsEngineException>(() => realm.GetProperty(target, "y")));
        Assert.True(refusal.Thrown.IsMissing);

        realm.Dispose();
        Assert.Throws<ObjectDisposedException>(() => realm.GetProperty(target, "x"));
    }
}
