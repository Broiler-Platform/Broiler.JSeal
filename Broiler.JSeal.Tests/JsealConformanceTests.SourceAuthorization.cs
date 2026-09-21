using Broiler.JSeal;

namespace Broiler.JSeal.Tests;

public partial class JsealConformanceTests
{
    [Theory]
    [MemberData(nameof(Engines))]
    public void HostScriptDoesNotAuthorizeGuestCompilation(string engine)
    {
        (string Source, string Permitted)[] routes =
        [
            ("(0, eval)('42')", "42"),
            ("new Function('return 42')()", "42"),
            ("Function('return 42')()", "42"),
            ("typeof new Function()", "function"),
            ("Reflect.construct(Function, ['return 42'])()", "42")
        ];
        foreach (var allow in new[] { false, true })
        {
            using var realm = NewRealm(engine, new JsRealmOptions { AllowGuestEval = allow });
            foreach (var (source, permitted) in routes)
            {
                // J00's reproduction: classic source installs page(), then host source calls it.
                realm.EvaluateClassicScript($"function page() {{ return {source}; }}", "test:install-page");
                var result = realm.EvaluateHostScript(
                    "(function(){ try { return String(page()); } catch(e) { return e.name; } })()",
                    "test:host-calls-page");
                Assert.Equal((source, allow ? permitted : "SyntaxError"), (source, result.AsString));
            }
            Assert.Equal(JsValue.Number(42), realm.EvaluateHostScript("6 * 7", "test:host-still-authorized"));
        }
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void HostAndClassicEvaluationsReenterWithoutLendingPermission(string engine)
    {
        using var realm = NewRealm(engine, new JsRealmOptions { AllowGuestEval = false });
        InstallRefusedPage(realm);
        var hostCalls = 0;
        var classicCalls = 0;
        realm.DefineValue(realm.Global, "runClassic", realm.NewMethod("runClassic", (in JsCall call) =>
        {
            classicCalls++;
            return call.Realm.EvaluateClassicScript("page()", "test:nested-classic");
        }));
        realm.DefineValue(realm.Global, "runHost", realm.NewMethod("runHost", (in JsCall call) =>
        {
            hostCalls++;
            Assert.Equal(JsValue.Number(42), call.Realm.EvaluateHostScript("6 * 7", "test:nested-host-value"));
            return call.Realm.EvaluateHostScript("runClassic() + ':' + page()", "test:nested-host");
        }));

        const string source = "runHost() + ':' + page()";
        Assert.Equal("SyntaxError:SyntaxError:SyntaxError", realm.EvaluateHostScript(source, "test:outer-host").AsString);
        Assert.Equal("SyntaxError:SyntaxError:SyntaxError", realm.EvaluateClassicScript(source, "test:outer-classic").AsString);
        Assert.Equal(2, hostCalls);
        Assert.Equal(2, classicCalls);
        Assert.Equal("SyntaxError", realm.Invoke(realm.GetProperty(realm.Global, "page"), JsValue.Undefined).AsString);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void FailedSourceEvaluationDoesNotLeaveCompilePermission(string engine)
    {
        using var realm = NewRealm(engine, new JsRealmOptions { AllowGuestEval = false });
        InstallRefusedPage(realm);
        foreach (var hostSource in new[] { false, true })
        foreach (var source in new[] { "function (", "throw 7" })
        {
            JsValue Fail() => hostSource
                ? realm.EvaluateHostScript(source, "test:failing-host")
                : realm.EvaluateClassicScript(source, "test:failing-classic");

            // Test both unwind to the host and a failure caught during an outer host evaluation.
            Assert.Throws<JsEngineException>(() => Fail());
            Assert.Equal("SyntaxError", realm.Invoke(realm.GetProperty(realm.Global, "page"), JsValue.Undefined).AsString);
            var recovered = false;
            realm.DefineValue(realm.Global, "recover", realm.NewMethod("recover", (in JsCall call) =>
            {
                Assert.Throws<JsEngineException>(() => Fail());
                recovered = true;
                Assert.Equal(JsValue.Number(42), realm.EvaluateHostScript("6 * 7", "test:host-after-failure"));
                return realm.Invoke(realm.GetProperty(realm.Global, "page"), JsValue.Undefined);
            }));
            Assert.Equal("SyntaxError:SyntaxError",
                realm.EvaluateHostScript("recover() + ':' + page()", "test:recover-in-host").AsString);
            Assert.True(recovered);
            Assert.Equal("SyntaxError", realm.Invoke(realm.GetProperty(realm.Global, "page"), JsValue.Undefined).AsString);
        }
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ForcedHostStrictnessDoesNotReachGuestCompilation(string engine)
    {
        using var realm = NewRealm(engine, new JsRealmOptions { ForceStrictMode = true });
        const string plainCall = "(function(){ return typeof this; })()";
        realm.EvaluateClassicScript(
            "function page() { return (0,eval)('(function(){return typeof this;})()')" +
            " + ':' + new Function('return typeof this')(); }", "test:sloppy-page");
        realm.DefineValue(realm.Global, "nested", realm.NewMethod("nested", (in JsCall call) =>
        {
            Assert.Equal("object", call.Realm.EvaluateClassicScript(plainCall, "test:nested-sloppy").AsString);
            Assert.Throws<JsEngineException>(() => realm.EvaluateHostScript("function (", "test:nested-strict-failure"));
            return call.Realm.EvaluateHostScript(plainCall, "test:nested-strict");
        }));

        Assert.Equal("undefined:undefined:object:object", realm.EvaluateHostScript(
            plainCall + " + ':' + nested() + ':' + page()", "test:strict-host-sloppy-guest").AsString);
        Assert.Equal("object", realm.EvaluateClassicScript(plainCall, "test:classic-after-host").AsString);
        Assert.Equal("object", realm.EvaluateDynamicSource(plainCall, "test:dynamic-after-host").AsString);
        Assert.Equal("undefined", realm.EvaluateHostScript(plainCall, "test:strict-after-unwind").AsString);
    }

    private static void InstallRefusedPage(IJsRealm realm) => realm.EvaluateClassicScript(
        "function page() { try { return String((0,eval)('42')); } catch(e) { return e.name; } }",
        "test:refused-page");
}
