namespace Broiler.JSeal.Tests;

public partial class JsealConformanceTests
{
    public static IEnumerable<object[]> CallbackArities =>
        from engine in Engines
        from arity in new[] { 0, 1, 8, 9, 32 }
        select new object[] { engine[0], arity };

    [Theory]
    [MemberData(nameof(CallbackArities))]
    public void CallbackFramesPreserveValuesAtBufferBoundaries(string engine, int arity)
    {
        using var realm = NewRealm(engine);
        var arguments = CallbackArguments(realm, arity);
        var receiver = realm.NewObject();
        var result = realm.NewObject();
        var calls = 0;
        var method = realm.NewMethod("probe", (in JsCall call) =>
        {
            CheckCallbackArguments(in call, realm, arguments);
            Assert.Equal(receiver, call.This);
            Assert.True(call.NewTarget.IsMissing);
            calls++;
            return result;
        });
        Assert.Equal(result, realm.Invoke(method, receiver, arguments));

        var constructor = JsValue.Missing;
        var constructedThis = JsValue.Missing;
        constructor = realm.NewConstructor("Probe", (in JsCall call) =>
        {
            CheckCallbackArguments(in call, realm, arguments);
            Assert.Equal(constructor, call.NewTarget);
            Assert.True(call.This.IsObject);
            constructedThis = call.This;
            calls++;
            return JsValue.Undefined;
        });
        Assert.Equal(realm.Construct(constructor, arguments), constructedThis);
        Assert.Equal(2, calls);
    }

    [Theory]
    [MemberData(nameof(CallbackArities))]
    public void RecursiveCallbacksKeepEachArgumentsBufferAlive(string engine, int arity)
    {
        using var realm = NewRealm(engine);
        JsValue[][] arguments = [CallbackArguments(realm, arity), CallbackArguments(realm, 32), CallbackArguments(realm, 9)];
        JsValue[] receivers = [realm.NewObject(), realm.NewObject(), realm.NewObject()];
        var callback = JsValue.Missing;
        var depth = 0;
        var calls = 0;
        var nestedFailure = new InvalidOperationException("nested callback failure");
        var throwInNestedCall = false;
        callback = realm.NewMethod("recursive", (in JsCall call) =>
        {
            var level = depth;
            CheckCallbackArguments(in call, realm, arguments[level]);
            Assert.Equal(receivers[level], call.This);
            Assert.True(call.NewTarget.IsMissing);
            calls++;
            if (level == 2 && throwInNestedCall) throw nestedFailure;
            if (level + 1 < arguments.Length)
            {
                depth++;
                try { realm.Invoke(callback, receivers[depth], arguments[depth]); }
                catch (InvalidOperationException raised) when (level == 1 && throwInNestedCall)
                {
                    Assert.Same(nestedFailure, raised);
                }
                finally { depth--; }
            }
            // A nested rent/return must not overwrite or clear the outer callback's live span.
            CheckCallbackArguments(in call, realm, arguments[level]);
            Assert.Equal(receivers[level], call.This);
            return JsValue.Undefined;
        });
        realm.Invoke(callback, receivers[0], arguments[0]);
        throwInNestedCall = true;
        realm.Invoke(callback, receivers[0], arguments[0]);
        Assert.Equal(6, calls);
    }

    [Theory]
    [MemberData(nameof(CallbackArities))]
    public void ThrowingCallbacksPreserveFailuresAndAllowBufferReuse(string engine, int arity)
    {
        using var realm = NewRealm(engine);
        var arguments = CallbackArguments(realm, arity);
        var hostFailure = new InvalidOperationException("callback host failure");
        var payload = realm.NewObject();
        realm.DefineValue(realm.Global, "callbackFailure", payload);
        var guestFailure = Assert.Throws<JsEngineException>(() =>
            realm.EvaluateClassicScript("throw callbackFailure", "test:callback-failure"));
        Exception? failure = guestFailure;
        var callback = realm.NewMethod("throwing", (in JsCall call) =>
        {
            CheckCallbackArguments(in call, realm, arguments);
            if (failure is not null) throw failure;
            return JsValue.Number(call.Length);
        });

        var raised = Assert.Throws<JsEngineException>(() => realm.Invoke(callback, JsValue.Undefined, arguments));
        Assert.Equal(payload, raised.Thrown);
        failure = hostFailure;
        Assert.Same(hostFailure, Assert.Throws<InvalidOperationException>(() => realm.Invoke(callback, JsValue.Undefined, arguments)));
        failure = null;
        Assert.Equal(arity, realm.Invoke(callback, JsValue.Undefined, arguments).AsNumber);
    }

    private static JsValue[] CallbackArguments(IJsRealm realm, int count) =>
        Enumerable.Range(0, count).Select(i => (i % 4) switch
        {
            0 => JsValue.Undefined,
            1 => realm.NewObject(),
            2 => JsValue.String($"argument-{i}"),
            _ => JsValue.Number(i),
        }).ToArray();

    private static void CheckCallbackArguments(in JsCall call, IJsRealm realm, JsValue[] expected)
    {
        Assert.Same(realm, call.Realm);
        Assert.Equal(expected.Length, call.Length);
        Assert.Equal(expected.Length, call.Arguments.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], call[i]);
            Assert.Equal(expected[i], call.Arguments[i]);
        }
        Assert.True(call[-1].IsMissing);
        Assert.True(call[expected.Length].IsMissing);
        Assert.True(call[expected.Length + 100].IsMissing);
    }
}
