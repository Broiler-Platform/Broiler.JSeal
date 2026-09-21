namespace Broiler.JSeal.Tests;

public partial class JsealConformanceTests
{
    public static IEnumerable<object[]> ThrowingCoercions =>
        from engine in Engines
        from coercion in new[] { "toString", "valueOf", "primitive-string", "primitive-number" }
        from payload in new[] { "42", "undefined", "({ marker: 42 })" }
        select new object[] { engine[0], coercion, payload };

    [Theory]
    [MemberData(nameof(ThrowingCoercions))]
    public void CoercionExceptionsPreserveGuestValuesAndReenter(string engine, string coercion, string payloadSource)
    {
        using var realm = NewRealm(engine);
        var payload = realm.EvaluateClassicScript(payloadSource, "test:coercion-payload");
        realm.DefineValue(realm.Global, "payload", payload);
        var hook = coercion.StartsWith("primitive-") ? "[Symbol.toPrimitive]" : coercion;
        var target = realm.EvaluateClassicScript("({ " + hook + ": function () { throw payload; } })", "test:throwing-coercion");
        void Convert()
        {
            if (coercion is "toString" or "primitive-string") realm.ToJsString(target);
            else realm.ToNumber(target);
        }
        var context = SynchronizationContext.Current;
        var failure = Assert.Throws<JsEngineException>(Convert);
        Assert.Equal(payload, failure.Thrown);
        if (payload.IsObject) Assert.Same(payload.ObjectIdentity, failure.Thrown.ObjectIdentity);
        Assert.Same(context, SynchronizationContext.Current);

        realm.DefineValue(realm.Global, "convert", realm.NewMethod("convert", (in JsCall _) =>
        {
            Convert();
            return JsValue.Undefined;
        }));
        Assert.True(realm.EvaluateClassicScript("(function () { try { convert(); return false; } catch (error) { return error === payload; } })()", "test:coercion-roundtrip").AsBoolean);
        Assert.Equal(42, realm.ToNumber(JsValue.String("42")));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void CoercionUsesPrimitiveHintsAndPreservesTypeErrors(string engine)
    {
        using var realm = NewRealm(engine);
        var target = realm.EvaluateClassicScript("({ [Symbol.toPrimitive]: function (hint) { return hint === 'string' ? 'label' : 42; } })", "test:primitive-hint");
        Assert.Equal("label", realm.ToJsString(target));
        Assert.Equal(42, realm.ToNumber(target));
        var symbol = realm.EvaluateClassicScript("Symbol('label')", "test:coercion-symbol");
        var failure = Assert.Throws<JsEngineException>(() => realm.ToJsString(symbol));
        realm.DefineValue(realm.Global, "coercionError", failure.Thrown);
        Assert.True(realm.EvaluateClassicScript("coercionError instanceof TypeError", "test:coercion-type-error").AsBoolean);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void JobExceptionsStopTheDrainAndPreserveLaterJobs(string engine)
    {
        using var realm = NewRealm(engine);
        var payload = realm.NewObject();
        var carried = new JsEngineException("guest object", payload);
        var hostFailure = new InvalidOperationException("host job failed");
        foreach (var failure in new Exception[] { hostFailure, carried, realm.Error(JsErrorKind.TypeError, "guest job failed") })
        {
            var order = new List<int>();
            realm.EnqueueJob(() =>
            {
                order.Add(1);
                realm.EnqueueJob(() => order.Add(3));
                throw failure;
            });
            realm.EnqueueJob(() => order.Add(2));
            var context = SynchronizationContext.Current;
            if (ReferenceEquals(failure, hostFailure))
                Assert.Same(hostFailure, Assert.Throws<InvalidOperationException>(() => realm.DrainJobs()));
            else
            {
                var caught = Assert.Throws<JsEngineException>(() => realm.DrainJobs());
                if (ReferenceEquals(failure, carried))
                    Assert.Same(payload.ObjectIdentity, caught.Thrown.ObjectIdentity);
                else
                {
                    realm.DefineValue(realm.Global, "jobError", caught.Thrown);
                    Assert.True(realm.EvaluateClassicScript("jobError instanceof TypeError", "test:job-error").AsBoolean);
                }
            }
            Assert.Same(context, SynchronizationContext.Current);
            Assert.Equal(new[] { 1 }, order);
            Assert.True(realm.HasPendingJobs);
            Assert.Equal(1, realm.DrainJobs(1));
            Assert.Equal(new[] { 1, 2 }, order);
            Assert.Equal(1, realm.DrainJobs());
            Assert.Equal(new[] { 1, 2, 3 }, order);
            Assert.False(realm.HasPendingJobs);
        }
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void PromiseGuestFailuresStayRejections(string engine)
    {
        using var realm = NewRealm(engine);
        foreach (var payloadSource in new[] { "undefined", "({ marker: 42 })" })
        foreach (var route in new[] { "getter", "then", "reaction", "reject" })
        {
            var payload = realm.EvaluateClassicScript(payloadSource, "test:promise-payload");
            realm.DefineValue(realm.Global, "payload", payload);
            var promise = realm.NewPromise(out var resolve, out var reject);
            realm.DefineValue(realm.Global, "pending", promise);
            realm.EvaluateClassicScript("var seen = false; pending" + (route == "reaction" ? ".then(function () { throw payload; })" : "") + ".catch(function (error) { seen = error === payload; });", "test:promise-handler");
            var value = route switch
            {
                "getter" => realm.EvaluateClassicScript("({ get then() { throw payload; } })", "test:then-getter"),
                "then" => realm.EvaluateClassicScript("({ then: function () { throw payload; } })", "test:then-call"),
                _ => payload,
            };
            if (route == "reject") reject(value); else resolve(value);
            Assert.False(realm.GetProperty(realm.Global, "seen").AsBoolean);
            realm.DrainJobs();
            Assert.True(realm.GetProperty(realm.Global, "seen").AsBoolean);
        }
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void PromiseSettlersEnterTheirRealmAndRestoreTheCaller(string engine)
    {
        using var realm = NewRealm(engine);
        using var caller = NewRealm(engine);
        var prototype = realm.GetProperty(realm.GetProperty(realm.Global, "Object"), "prototype");
        var inspected = false;
        realm.DefineValue(realm.Global, "inspect", realm.NewMethod("inspect", (in JsCall call) =>
        {
            Assert.Equal(prototype, call.Realm.GetPrototype(call[0]));
            inspected = true;
            return JsValue.Undefined;
        }));
        var thenable = realm.EvaluateClassicScript("({ get then() { inspect({}); return undefined; } })", "test:thenable-realm");
        var promise = realm.NewPromise(out var resolve, out var reject);
        realm.DefineValue(realm.Global, "pending", promise);
        realm.EvaluateClassicScript("var settled = false; pending.then(function () { settled = true; });", "test:settle-handler");
        caller.DefineValue(caller.Global, "settle", caller.NewMethod("settle", (in JsCall _) =>
        {
            var context = SynchronizationContext.Current;
            resolve(thenable);
            Assert.Same(context, SynchronizationContext.Current);
            return JsValue.Undefined;
        }));
        caller.EvaluateClassicScript("settle()", "test:reentrant-settle");
        Assert.True(inspected);
        Assert.False(realm.GetProperty(realm.Global, "settled").AsBoolean);
        Assert.False(caller.HasPendingJobs);
        realm.DrainJobs();
        Assert.True(realm.GetProperty(realm.Global, "settled").AsBoolean);
        realm.Dispose();
        Assert.Throws<ObjectDisposedException>(() => resolve(JsValue.Undefined));
        Assert.Throws<ObjectDisposedException>(() => reject(JsValue.Undefined));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void PromiseRejectersQueueReactionsInTheirOwnRealm(string engine)
    {
        using var realm = NewRealm(engine);
        using var caller = NewRealm(engine);
        var promise = realm.NewPromise(out _, out var reject);
        realm.DefineValue(realm.Global, "pending", promise);
        realm.EvaluateClassicScript("var rejected = false; pending.catch(function (error) { rejected = error === undefined; });", "test:reject-handler");
        caller.DefineValue(caller.Global, "rejectPending", caller.NewMethod("rejectPending", (in JsCall _) =>
        {
            var context = SynchronizationContext.Current;
            reject(JsValue.Undefined);
            Assert.Same(context, SynchronizationContext.Current);
            return JsValue.Undefined;
        }));
        caller.EvaluateClassicScript("rejectPending()", "test:reentrant-reject");
        Assert.False(caller.HasPendingJobs);
        Assert.False(realm.GetProperty(realm.Global, "rejected").AsBoolean);
        realm.DrainJobs();
        Assert.True(realm.GetProperty(realm.Global, "rejected").AsBoolean);
    }
}
