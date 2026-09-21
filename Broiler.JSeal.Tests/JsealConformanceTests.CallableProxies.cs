namespace Broiler.JSeal.Tests;

public partial class JsealConformanceTests
{
    [Theory]
    [MemberData(nameof(Engines))]
    public void CallableProxiesInvokeWithTheReceiverAndArguments(string engine)
    {
        using var realm = NewRealm(engine);
        var receiver = realm.NewObject();
        realm.DefineValue(receiver, "offset", JsValue.Number(20));

        foreach (var source in new[]
        {
            "(function (x) { return this.offset + x; })",
            "new Proxy(function (x) { return this.offset + x; }, {})",
            "new Proxy(new Proxy(function (x) { return this.offset + x; }, {}), {})",
            "new Proxy(function () { return -1; }, { apply: function (target, receiver, args) { return receiver.offset + args[0]; } })",
        })
        {
            var callback = realm.EvaluateClassicScript(source, "test:callable-proxy");
            Assert.True(callback.IsFunction);
            Assert.True(callback.IsObject);
            Assert.Equal(JsValue.Number(42), realm.Invoke(callback, receiver, [JsValue.Number(22)]));
        }
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void CallableProxiesPassHostGuardsAndKeepTheirOwnIdentity(string engine)
    {
        using var realm = NewRealm(engine);
        var holder = realm.EvaluateClassicScript("""
            (function () {
                var target = function () { return 42; };
                var proxy = new Proxy(target, { get: function () { throw 7; } });
                return { target: target, first: proxy, second: proxy };
            })()
            """, "test:proxy-identity");
        var callback = realm.GetProperty(holder, "first");
        var second = realm.GetProperty(holder, "second");
        Assert.True(callback.IsFunction);
        Assert.Equal(callback, second);
        Assert.Same(callback.ObjectIdentity, second.ObjectIdentity);
        Assert.Equal(callback.GetHashCode(), second.GetHashCode());
        var target = realm.GetProperty(holder, "target");
        Assert.NotEqual(target, callback);
        Assert.NotSame(target.ObjectIdentity, callback.ObjectIdentity);

        JsValue AcceptCallback(in JsCall call)
        {
            if (!call[0].IsFunction)
                throw call.Realm.Error(JsErrorKind.TypeError, "Expected a callback");
            Assert.Equal(callback, call[0]);
            Assert.Same(callback.ObjectIdentity, call[0].ObjectIdentity);
            Assert.Equal(JsValue.Number(42), call.Realm.Invoke(call[0], JsValue.Undefined));
            return call[0];
        }

        realm.DefineValue(realm.Global, "holder", holder);
        realm.DefineValue(realm.Global, "acceptCallback", realm.NewMethod("acceptCallback", AcceptCallback, 1));
        var returned = realm.EvaluateClassicScript("acceptCallback(holder.first)", "test:proxy-callback");
        Assert.Equal(callback, returned);
        Assert.Same(callback.ObjectIdentity, returned.ObjectIdentity);
        Assert.True(realm.EvaluateClassicScript("acceptCallback(holder.first) === holder.second", "test:proxy-roundtrip").AsBoolean);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void NoncallableProxiesDoNotBecomeFunctionsThroughPropertiesOrTraps(string engine)
    {
        using var realm = NewRealm(engine);
        foreach (var source in new[]
        {
            "new Proxy({ call: function () { return 42; } }, {})",
            "new Proxy({}, { get: function () { throw 7; }, apply: function () { return 42; } })",
        })
        {
            var value = realm.EvaluateClassicScript(source, "test:noncallable-proxy");
            Assert.True(value.IsObject);
            Assert.False(value.IsFunction);
        }
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void RevokedProxiesKeepCallabilityAndIdentityButCannotBeInvoked(string engine)
    {
        using var realm = NewRealm(engine);
        foreach (var callable in new[] { true, false })
        {
            var target = callable ? "function () { return 42; }" : "{}";
            var holder = realm.EvaluateClassicScript($"Proxy.revocable({target}, {{}})", "test:revocable-proxy");
            var before = realm.GetProperty(holder, "proxy");
            Assert.Equal(callable, before.IsFunction);
            realm.Invoke(realm.GetProperty(holder, "revoke"), JsValue.Undefined);

            // Reading the holder rewraps the revoked proxy without accessing its properties.
            var after = realm.GetProperty(holder, "proxy");
            Assert.True(after.IsObject);
            Assert.Equal(callable, after.IsFunction);
            Assert.Equal(before, after);
            Assert.Same(before.ObjectIdentity, after.ObjectIdentity);
            Assert.Equal(before.GetHashCode(), after.GetHashCode());
            var error = Assert.Throws<JsEngineException>(() => realm.Invoke(after, JsValue.Undefined));
            if (callable)
            {
                realm.DefineValue(realm.Global, "revocationError", error.Thrown);
                Assert.True(realm.EvaluateClassicScript("revocationError instanceof TypeError", "test:revocation-error").AsBoolean);
            }
        }
    }
}
