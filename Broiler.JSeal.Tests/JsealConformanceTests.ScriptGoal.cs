namespace Broiler.JSeal.Tests;

/// <summary>
/// Host and classic source is script code, and dynamic source is eval code: the declarations each
/// leaves behind are the ones ECMAScript's <c>ScriptEvaluation</c> and <c>PerformEval</c> leave.
/// </summary>
/// <remarks>
/// <para>
/// <b>A page's scripts are separate Script records sharing one realm</b> (ES2026 16.1.6,
/// <c>GlobalDeclarationInstantiation</c>): a top-level <c>let</c>, <c>const</c> or <c>class</c>
/// becomes a binding of the global lexical environment that every later script sees without it
/// being a property of the global object, a <c>var</c> or function becomes a non-configurable
/// property of the global object, and a script whose declarations conflict with an existing one is
/// refused before it creates anything. An inline polyfill followed by the code that uses it is
/// exactly that shape, so a provider that evaluated scripts as eval code would lose the polyfill's
/// <c>const</c> between the two.
/// </para>
/// <para>
/// <b>Dynamic source is what a page's <c>eval</c> produces</b> (ES2026 19.2.1.1,
/// <c>PerformEval</c>): its lexical declarations live and die with that evaluation, and its
/// <c>var</c>s are configurable. The two kinds meeting the same name is the boundary pinned here.
/// </para>
/// </remarks>
public partial class JsealConformanceTests
{
    /// <summary>
    /// Script-goal cases a provider is known to answer wrongly, and why. Such a row must still FAIL,
    /// so a fixed provider cannot keep a stale entry; see <see cref="AssertScriptGoalCase"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Engine, string Case), string> ScriptGoalGaps =
        new Dictionary<(string, string), string>
        {
            [("broiler-js", nameof(AConflictingScriptIsRefusedBeforeItCreatesAnything))] =
                "the pinned Broiler.JavaScript 0.1.0-preview.1 JSContext.Eval runs no GlobalDeclarationInstantiation " +
                "checks, so a script redeclaring an earlier script's let, or a let over a var, is evaluated",
            [("broiler-js", nameof(DynamicSourceIsEvalCodeAndItsLexicalDeclarationsDoNotPersist))] =
                "the Broiler.JS provider evaluates dynamic source through the same JSContext.Eval as a script, so " +
                "its let persists and its var is non-configurable; a PerformEval route is a provider follow-up",
            [("broiler-vm", nameof(AConflictingScriptIsRefusedBeforeItCreatesAnything))] =
                "the pinned Broiler.VM 0.1.0-preview.3 has no script-goal host route, so host and classic scripts run through " +
                "the realm's eval, which runs no GlobalDeclarationInstantiation checks; JsHostRealm.EvaluateScript (VM JSD-0024 " +
                "section 16) is adopted with the next VM pin",
            [("broiler-vm", nameof(DynamicSourceIsEvalCodeAndItsLexicalDeclarationsDoNotPersist))] =
                "the pinned Broiler.VM 0.1.0-preview.3 evaluates global eval code with script semantics (its let persists and its " +
                "var is non-configurable); VM V15 corrects eval, and the provider adopts it with the next VM pin",
        };

    /// <summary>Runs a case, or, for a recorded gap, requires the case's own assertions to fail.</summary>
    private static void AssertScriptGoalCase(string engine, string contractCase, Action run)
    {
        if (!ScriptGoalGaps.TryGetValue((engine, contractCase), out var gap))
        {
            run();
            return;
        }

        var failure = Record.Exception(run);
        Assert.True(failure is not null, $"'{engine}' / {contractCase} now passes; remove its gap ({gap}).");
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(failure);
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.HostScriptSource | JsCapabilities.ClassicScriptSource)]
    public void TopLevelLexicalDeclarationsPersistIntoLaterScripts(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.HostScriptSource | JsCapabilities.ClassicScriptSource);

        realm.EvaluateHostScript("let counter = 1; const limit = 40; class Widget { static kind() { return 'widget'; } }", "test:script-goal-declare");
        realm.EvaluateClassicScript("counter += 1;", "test:script-goal-update");

        Assert.Equal(
            "42,widget",
            realm.EvaluateClassicScript("(counter + limit) + ',' + Widget.kind()", "test:script-goal-read").AsString);

        // Bindings of the global lexical environment, not properties of the global object.
        foreach (var name in new[] { "counter", "limit", "Widget" })
            Assert.DoesNotContain(name, realm.OwnPropertyNames(realm.Global));

        // A guest function created later closes over the same binding.
        realm.EvaluateHostScript("function readCounter() { return counter; }", "test:script-goal-closure");
        realm.EvaluateHostScript("counter = 7;", "test:script-goal-assign");
        Assert.True(realm.Invoke(realm.GetProperty(realm.Global, "readCounter"), JsValue.Undefined) == JsValue.Number(7d));

        // A const stays constant across scripts.
        var constant = Assert.Throws<JsEngineException>(() => realm.EvaluateHostScript("limit = 1;", "test:script-goal-const"));
        Assert.Equal("TypeError", realm.GetProperty(constant.Thrown, "name").AsString);
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.HostScriptSource)]
    public void ScriptVarAndFunctionDeclarationsAreNonConfigurableGlobalProperties(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.HostScriptSource);

        realm.EvaluateHostScript("var scriptVar = 1; function scriptFunction() {}", "test:script-goal-var");

        Assert.Equal(
            "false,false,true",
            realm.EvaluateHostScript(
                "[Object.getOwnPropertyDescriptor(globalThis, 'scriptVar').configurable," +
                " Object.getOwnPropertyDescriptor(globalThis, 'scriptFunction').configurable," +
                " delete globalThis.scriptVar === false].join()",
                "test:script-goal-var-descriptors").AsString);
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.HostScriptSource | JsCapabilities.ClassicScriptSource)]
    public void AConflictingScriptIsRefusedBeforeItCreatesAnything(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.HostScriptSource | JsCapabilities.ClassicScriptSource);

        AssertScriptGoalCase(engine, nameof(AConflictingScriptIsRefusedBeforeItCreatesAnything), () =>
        {
            realm.EvaluateHostScript("let taken = 'first'; var sharedVar = 1;", "test:script-goal-first");

            foreach (var (source, error) in new[]
            {
                ("var createdBefore1 = 1; let taken = 'second';", "SyntaxError"),
                ("var createdBefore2 = 1; var taken = 'second';", "SyntaxError"),
                ("var createdBefore3 = 1; function taken() {}", "SyntaxError"),
                ("var createdBefore4 = 1; let sharedVar = 2;", "SyntaxError"),
                ("var createdBefore5 = 1; let undefined = 2;", "SyntaxError"),
            })
            {
                var refused = Assert.Throws<JsEngineException>(() => realm.EvaluateClassicScript(source, "test:script-goal-conflict"));
                Assert.Equal((source, error), (source, realm.GetProperty(refused.Thrown, "name").AsString));
            }

            // Nothing the refused scripts declared exists, and what was there is unchanged.
            Assert.Equal(
                "undefined,undefined,undefined,undefined,undefined,first,1",
                realm.EvaluateHostScript(
                    "[typeof createdBefore1, typeof createdBefore2, typeof createdBefore3, typeof createdBefore4," +
                    " typeof createdBefore5, taken, sharedVar].join()",
                    "test:script-goal-unchanged").AsString);
        });
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.HostScriptSource | JsCapabilities.GuestEval)]
    public void DynamicSourceIsEvalCodeAndItsLexicalDeclarationsDoNotPersist(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.HostScriptSource | JsCapabilities.GuestEval);

        AssertScriptGoalCase(engine, nameof(DynamicSourceIsEvalCodeAndItsLexicalDeclarationsDoNotPersist), () =>
        {
            Assert.True(realm.EvaluateDynamicSource("let evalLexical = 1; var evalVar = 2; evalLexical + evalVar", "test:eval-goal") == JsValue.Number(3d));

            Assert.Equal(
                "undefined,2,true",
                realm.EvaluateHostScript(
                    "[typeof evalLexical, evalVar, Object.getOwnPropertyDescriptor(globalThis, 'evalVar').configurable].join()",
                    "test:eval-goal-read").AsString);

            // A script's lexical binding is visible to later dynamic source, which cannot redeclare it
            // with var.
            realm.EvaluateHostScript("let fromScript = 'script';", "test:eval-goal-script");
            Assert.Equal("script", realm.EvaluateDynamicSource("fromScript", "test:eval-goal-sees-script").AsString);
            var clash = Assert.Throws<JsEngineException>(() => realm.EvaluateDynamicSource("var fromScript = 1;", "test:eval-goal-clash"));
            Assert.Equal("SyntaxError", realm.GetProperty(clash.Thrown, "name").AsString);
        });
    }
}
