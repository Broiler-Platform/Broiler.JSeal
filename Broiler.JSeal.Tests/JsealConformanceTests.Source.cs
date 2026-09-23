using System.Numerics;
using System.Reflection;

using Broiler.JSeal;

namespace Broiler.JSeal.Tests;

/// <summary>
/// Evaluating source: host script, classic script, and the refusals of a realm that forbids guest evaluation.
/// </summary>
public partial class JsealConformanceTests
{
    // â”€â”€ source â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.HostScriptSource)]
    public void EvaluatingHostScriptAnswersTheValueOfTheLastExpression(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.HostScriptSource);

        Assert.True(realm.EvaluateHostScript("2 + 3", "test:host") == JsValue.Number(5d));
        Assert.True(realm.EvaluateHostScript("'a' + 'b'", "test:host-string") == JsValue.String("ab"));
        Assert.True(realm.EvaluateHostScript("({ a: 1 })", "test:host-object").IsObject);
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.GuestEval | JsCapabilities.HostScriptSource)]
    public void ARealmBuiltWithoutGuestEvalRefusesDynamicSourceAndStillRunsHostScript(string engine)
    {
        var provider = Provider(engine);

        using (var permissive = provider.CreateRealm(JsRealmOptions.Default))
        {
            // The default realm runs both, and the split is only visible when a host asks for it.
            Assert.True(permissive.Capabilities.HasFlag(JsCapabilities.GuestEval));
            Assert.True(permissive.EvaluateDynamicSource("6 * 7", "test:dynamic") == JsValue.Number(42d));
        }

        using var restricted = provider.CreateRealm(new JsRealmOptions { AllowGuestEval = false });

        // A realm is never wider than its provider and may be narrower. This is the only narrowing
        // the options can express, and it takes away only what 'unsafe-eval' governs: the dynamic
        // source refused below, and not the bridge's own script, which still runs.
        Assert.False(restricted.Capabilities.HasFlag(JsCapabilities.GuestEval));
        Assert.Equal(
            provider.Capabilities & ~JsCapabilities.GuestEval,
            restricted.Capabilities & ~JsCapabilities.GuestEval);

        var refusal = Assert.Throws<JsCapabilityUnavailableException>(
            () => restricted.EvaluateDynamicSource("6 * 7", "test:dynamic-refused"));
        Assert.Equal(JsCapabilities.GuestEval, refusal.Missing);
        Assert.Equal(restricted.EngineName, refusal.EngineName);

        Assert.True(restricted.EvaluateHostScript("6 * 7", "test:host-still-runs") == JsValue.Number(42d));
    }

    /// <summary>
    /// A realm whose policy forbids <c>'unsafe-eval'</c> still runs the page's script ELEMENTS.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the assertion the two-member contract could not make, and its absence was a bug
    /// waiting for a host to write one line.</b> <c>script-src</c> and <c>'unsafe-eval'</c> are
    /// different directives: a page served <c>script-src 'unsafe-inline'</c> runs every one of its
    /// script elements and no <c>eval</c>. With one member for both, a host that read a restrictive
    /// policy and narrowed the realm â€” which the provider contract instructs it to do â€” would have
    /// refused that page's ordinary scripts. Nothing in this repository had written that line yet,
    /// so the defect was latent rather than live, and this test is what stops it being written.
    /// </para>
    /// <para>
    /// Its rows come from ClassicScriptSource alone. A provider with classic script and no GuestEval
    /// is exactly the realm a restrictive policy produces, and it must be witnessed too; the control
    /// half that evaluates dynamic source runs only where GuestEval is declared, and elsewhere the
    /// permissive realm must refuse it as well.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.ClassicScriptSource)]
    public void AClassicScriptRunsInARealmThatForbidsGuestEvaluation(string engine)
    {
        var provider = Provider(engine);

        using var restricted = provider.CreateRealm(new JsRealmOptions { AllowGuestEval = false });

        // The ability survives the narrowing; only the permission goes.
        AssertHas(restricted, JsCapabilities.ClassicScriptSource);
        Assert.False(restricted.Capabilities.HasFlag(JsCapabilities.GuestEval));

        Assert.True(restricted.EvaluateClassicScript("6 * 7", "test:classic") == JsValue.Number(42d));

        Assert.Throws<JsCapabilityUnavailableException>(
            () => restricted.EvaluateDynamicSource("6 * 7", "test:dynamic-refused"));

        // The control, and it is doing real work: a provider whose EvaluateClassicScript refused
        // everything would satisfy nothing above, but one whose EvaluateDynamicSource refused
        // everything - narrowed or not - would satisfy the refusal having tested no narrowing.
        using var permissive = provider.CreateRealm(JsRealmOptions.Default);

        AssertHas(permissive, JsCapabilities.ClassicScriptSource);
        Assert.True(permissive.EvaluateClassicScript("6 * 7", "test:classic-permitted") == JsValue.Number(42d));

        if (provider.Capabilities.HasFlag(JsCapabilities.GuestEval))
        {
            AssertHas(permissive, JsCapabilities.GuestEval);
            Assert.True(permissive.EvaluateDynamicSource("6 * 7", "test:dynamic-permitted") == JsValue.Number(42d));
        }
        else
        {
            // Without the ability there is no narrowing to observe, and the refusal is the provider's.
            var refusal = Assert.Throws<JsCapabilityUnavailableException>(
                () => permissive.EvaluateDynamicSource("6 * 7", "test:dynamic-unavailable"));
            Assert.Equal(JsCapabilities.GuestEval, refusal.Missing);
        }
    }

    /// <summary>
    /// And the page's own <c>eval</c> and <c>Function</c> are refused inside that realm â€” which is
    /// where a browser refuses them, and where the capability had not been enforced at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A capability that is declared false and enforced nowhere is worse than one that is
    /// absent</b>, because a host is entitled to branch on it without verifying it. Refusing at the
    /// host member alone refuses a door no page walks through: a page does not call a contract
    /// member, it writes <c>eval('â€¦')</c>.
    /// </para>
    /// <para>
    /// <b>It also pins that permission to run a script is not permission for what that script asks
    /// for next.</b> The classic script here is handed over by the host and compiles; the
    /// <c>eval</c> inside it is the page asking for more executable bytes and does not. On an engine
    /// whose only compiler is a registered provider, that distinction is the difference between a
    /// permission held across the evaluation and one spent by the compile it authorises.
    /// </para>
    /// <para>
    /// <c>new Function</c> is asserted separately from <c>eval</c> on purpose: they are different
    /// routes to the same compiler, an implementation that stubbed the <c>eval</c> global would pass
    /// the first and fail the second, and the second is the one a real page's framework uses.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void APagesOwnEvalAndFunctionAreRefusedInARealmThatForbidsGuestEvaluation(string engine)
    {
        var provider = Provider(engine);

        using var restricted = provider.CreateRealm(new JsRealmOptions { AllowGuestEval = false });

        Assert.Throws<JsEngineException>(
            () => restricted.EvaluateClassicScript("eval('1 + 1')", "test:page-eval"));

        Assert.Throws<JsEngineException>(
            () => restricted.EvaluateClassicScript("new Function('return 1')", "test:page-function"));

        // The control: both are ordinary JavaScript, and a provider that simply could not run them
        // would satisfy the refusals above without enforcing anything.
        using var permissive = provider.CreateRealm(JsRealmOptions.Default);

        Assert.True(
            permissive.EvaluateClassicScript("eval('1 + 1')", "test:page-eval-permitted")
                == JsValue.Number(2d));
        Assert.True(
            permissive.EvaluateClassicScript("new Function('return 7')()", "test:page-function-permitted")
                == JsValue.Number(7d));
    }

    /// <summary>
    /// A dynamic function built from no arguments at all is refused there too, for every function
    /// kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It compiles nothing the page wrote, and a browser refuses it anyway.</b> The specification's
    /// dynamic-function algorithm asks the host whether strings may be compiled before it looks at how
    /// many it was handed, so <c>new Function()</c> is an <c>'unsafe-eval'</c> question exactly as
    /// <c>new Function('return 1')</c> is. Broiler.JS used to answer it without asking: its
    /// argument-less shortcut returned before the hook this refusal hangs on.
    /// </para>
    /// <para>
    /// Each route is its own classic script, so an engine that cannot parse one of them cannot mask
    /// the answer for another, and every answer is read back as a property rather than evaluated.
    /// Every engine must refuse <c>new Function()</c> with a <c>SyntaxError</c>, and so must every
    /// route that builds a function where evaluation is permitted; a route an engine cannot run at all
    /// has to fail the same way in both realms. No engine may build the function where it is forbidden.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void AnArgumentlessDynamicFunctionIsRefusedInARealmThatForbidsGuestEvaluation(string engine)
    {
        var provider = Provider(engine);

        string[] routes =
        [
            "new Function()",
            "Function()",
            "(function () {}).constructor()",
            "Reflect.construct(Function, [])",
            "new (Object.getPrototypeOf(async function () {}).constructor)()",
            "new (Object.getPrototypeOf(function* () {}).constructor)()",
            "new (Object.getPrototypeOf(async function* () {}).constructor)()",
        ];

        using var restricted = provider.CreateRealm(new JsRealmOptions { AllowGuestEval = false });
        using var permissive = provider.CreateRealm(JsRealmOptions.Default);

        for (var i = 0; i < routes.Length; i++)
        {
            var outcome = ProbeConstruction(restricted, engine, $"argumentless{i}", routes[i]);

            // The control, per route: where evaluation is permitted the route builds a function, so its
            // refusal is the realm's doing and not an engine that cannot run it. Another engine may fail a
            // route in both realms, but only identically; one it builds when permitted has to be refused,
            // catchably, when forbidden.
            var permitted = ProbeConstruction(permissive, engine, $"argumentlessPermitted{i}", routes[i]);

            // The route rides along in the tuple so a failure names the one that was built.
            if (engine == "broiler-js" || i == 0 || permitted == "made:function")
            {
                Assert.Equal((routes[i], "made:function"), (routes[i], permitted));
                Assert.Equal((routes[i], "refused:SyntaxError"), (routes[i], outcome));
            }
            else
            {
                Assert.Equal((routes[i], permitted), (routes[i], outcome));
            }
        }
    }

    /// <summary>
    /// And <c>ShadowRealm.prototype.evaluate</c>, which compiles the page's string in a realm of its
    /// own, is refused there as well.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The realm the string runs in is new; the string is still the page's.</b> The specification
    /// asks the host before it parses, as it does for <c>eval</c>, about the ShadowRealm's own realm,
    /// which Broiler.JS answers with the policy of the realm that constructed it: here, the page's.
    /// Broiler.JS used to compile the string in a child context that raised nothing, so a page
    /// forbidden <c>'unsafe-eval'</c> could run <c>new ShadowRealm().evaluate('6 * 7')</c> and read 42.
    /// </para>
    /// <para>
    /// A provider without <c>ShadowRealm</c> passes by not defining it. Broiler.JS defines it, so
    /// there its absence fails rather than skipping the assertion. Constructing one compiles nothing
    /// and stays permitted: the probe constructs outside its <c>try</c>, so a refused constructor
    /// fails the classic script itself.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void ShadowRealmEvaluatesNothingInARealmThatForbidsGuestEvaluation(string engine)
    {
        var provider = Provider(engine);

        const string probe =
            "var shadow = 'absent';" +
            "if (typeof ShadowRealm === 'function') {" +
            "  var shadowRealm = new ShadowRealm();" +
            "  try { shadow = 'compiled:' + shadowRealm.evaluate('6 * 7'); }" +
            "  catch (e) { shadow = 'refused:' + ((e && e.name) || 'unnamed'); }" +
            "}";

        using var permissive = provider.CreateRealm(JsRealmOptions.Default);
        using var restricted = provider.CreateRealm(new JsRealmOptions { AllowGuestEval = false });

        if (engine == "broiler-js")
        {
            Assert.True(permissive.GetProperty(permissive.Global, "ShadowRealm").IsFunction,
                "broiler-js exposes ShadowRealm; the probe must run there.");
            Assert.True(restricted.GetProperty(restricted.Global, "ShadowRealm").IsFunction,
                "broiler-js exposes ShadowRealm in a restricted realm too; the probe must run there.");
        }

        permissive.EvaluateClassicScript(probe, "test:shadow-permitted");
        restricted.EvaluateClassicScript(probe, "test:shadow-restricted");

        var permitted = permissive.GetProperty(permissive.Global, "shadow").AsString;
        var refused = restricted.GetProperty(restricted.Global, "shadow").AsString;

        if (engine == "broiler-js")
        {
            Assert.Equal("compiled:42", permitted);
            Assert.Equal("refused:SyntaxError", refused);
            return;
        }

        Assert.True(permitted is "compiled:42" or "absent", $"{engine}: {permitted}");

        // Where it compiled when permitted, the refusal has to be the SyntaxError every provider raises
        // for a refused compile, as the argument-less theory demands; a TypeError would be a restricted
        // realm whose evaluate went missing.
        Assert.True(
            refused == "absent" || (permitted == "compiled:42" && refused == "refused:SyntaxError"),
            $"{engine}: {refused}");

        // Passing by not defining ShadowRealm means not defining it in either realm: a provider that
        // removed it only where evaluation is forbidden would be refusing by reshaping the language.
        Assert.True(
            (permitted == "absent") == (refused == "absent"),
            $"{engine} defines ShadowRealm in one realm only: {permitted} / {refused}");
    }

    /// <summary>
    /// Runs a classic script that attempts <paramref name="construct"/> and records what happened in
    /// the global <paramref name="name"/>, then reads that global back as a property.
    /// </summary>
    /// <remarks>
    /// A classic script that throws past the probe's own <c>catch</c> answers <c>unparsed</c>. In
    /// practice that is an engine that cannot parse the construct, but any other
    /// <see cref="JsEngineException"/> from the evaluation answers the same, so the label is not proof
    /// of a parse failure. Broiler.JS parses every construct these tests use, so there any such
    /// exception fails the test instead.
    /// </remarks>
    private static string ProbeConstruction(IJsRealm realm, string engine, string name, string construct)
    {
        try
        {
            realm.EvaluateClassicScript(
                $"var {name} = (function () {{" +
                $"  try {{ return 'made:' + typeof ({construct}); }}" +
                "  catch (e) { return 'refused:' + ((e && e.name) || 'unnamed'); }" +
                "})();",
                $"test:{name}");
        }
        catch (JsEngineException) when (engine != "broiler-js")
        {
            return "unparsed";
        }

        var outcome = realm.GetProperty(realm.Global, name);
        return outcome.AsString ?? $"not-a-string:{outcome.Kind}";
    }

    /// <summary>
    /// <c>ForceStrictMode</c> makes the source THIS REPOSITORY hands over strict, and leaves what the
    /// page evaluates alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two providers disagreed about this in opposite directions and nothing asked either.</b>
    /// One forced strictness on both of the members the contract then had, so a page's own
    /// evaluation was strict when the host had only asked for its own to be; the other reached only
    /// its bootstrap unit, so nothing was strict whatever the host asked. Neither is arguable: an
    /// indirect <c>eval</c> evaluates a NEW script whose strictness comes from its own source, so a
    /// host that forced it strict would make one page behave differently here than anywhere else,
    /// and a host that could not force its own would have an option that did nothing.
    /// <c>docs/vm-javascript-profile.md</c> states the <c>eval</c> half for the script engines'
    /// <c>StrictModeEnabled</c>, which also makes a document's own scripts strict. This option does
    /// not reach <c>EvaluateClassicScript</c> on either provider, and this test does not ask.
    /// </para>
    /// <para>
    /// <b>A value probe, not an exception probe.</b> Strictness is read from what <c>this</c> is
    /// inside a plain call â€” <c>undefined</c> when strict, the global when not â€” so the assertion
    /// does not depend on which error a provider raises for an undeclared assignment, and a provider
    /// that refused the probe outright would fail rather than look strict.
    /// </para>
    /// <para>
    /// The third realm is the control that makes the first two mean something: without it, a
    /// provider that answered <c>"undefined"</c> for every plain call â€” because it never implemented
    /// sloppy <c>this</c> at all â€” would satisfy the host assertion having demonstrated nothing about
    /// <c>ForceStrictMode</c>.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void ForcedStrictModeReachesHostScriptAndNotWhatThePageEvaluates(string engine)
    {
        // `this` inside a plain call: undefined under strict mode, the global object otherwise.
        const string ThisInAPlainCall = "(function () { return typeof this; })()";

        using var forced = NewRealm(engine, new JsRealmOptions { ForceStrictMode = true });

        Assert.Equal("undefined", Eval(forced, ThisInAPlainCall, "test:strict-host"));

        Assert.Equal(
            "object",
            forced.ToJsString(forced.EvaluateDynamicSource(ThisInAPlainCall, "test:strict-does-not-reach-dynamic")));

        // The control: a realm that did not ask for it is sloppy on both sides, so the answers above
        // are ForceStrictMode's doing rather than the provider's fixed behaviour.
        using var relaxed = NewRealm(engine);

        Assert.Equal("object", Eval(relaxed, ThisInAPlainCall, "test:default-host"));
        Assert.Equal(
            "object",
            relaxed.ToJsString(relaxed.EvaluateDynamicSource(ThisInAPlainCall, "test:default-dynamic")));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ASyntaxErrorInSourceReachesTheHostAsAnEngineException(string engine)
    {
        using var realm = NewRealm(engine);

        // Not a capability failure: the host asked for something the realm can do, and the source
        // was wrong. A host that cannot tell those apart cannot report either usefully.
        Assert.Throws<JsEngineException>(() => realm.EvaluateHostScript("function (", "test:syntax"));
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.GlobalIsVariableScope | JsCapabilities.HostScriptSource)]
    public void ATopLevelDeclarationBecomesAPropertyOfTheGlobal(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.GlobalIsVariableScope | JsCapabilities.HostScriptSource);

        var before = realm.OwnPropertyNames(realm.Global);
        realm.EvaluateHostScript("var declaredAtTopLevel = 7; function declaredFunction() { return 8; }", "test:var");
        var after = realm.OwnPropertyNames(realm.Global);

        Assert.True(realm.GetProperty(realm.Global, "declaredAtTopLevel") == JsValue.Number(7d));

        // Both land, and in declaration-instantiation order rather than source order:
        // GlobalDeclarationInstantiation creates the function bindings before the var ones. The
        // sweep this contract exists for reads the diff, so the order it sees is worth pinning.
        Assert.Equal(new[] { "declaredFunction", "declaredAtTopLevel" }, after.Except(before).ToArray());
        Assert.True(realm.Global.IsObject);
    }

    /// <summary>
    /// A page that replaces <c>eval</c> does not intercept the host's own script.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>eval</c> is a writable global, which is the language's rule and not an engine's
    /// choice</b> - so <c>globalThis.eval = f</c> is a thing a page may legally do, and a provider
    /// that reached for the global when a host asked it to run a script would hand the page every
    /// polyfill the bridge installs, and take whatever the page returned as the result.
    /// </para>
    /// <para>
    /// This is the same claim <c>APageThatReplacesPromiseDoesNotCaptureTheHostsPromises</c> makes
    /// about a different intrinsic, and the same fix answers it: capture before any page script can
    /// run. It is a theory because it is a claim about every provider - one reaches its engine's
    /// compiler directly and cannot be intercepted at all, and that is a fine way to satisfy it.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.HostScriptSource)]
    public void APageThatReplacesEvalDoesNotInterceptTheHostsOwnScript(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.HostScriptSource);

        // The page replaces the global, exactly as it is entitled to.
        realm.EvaluateHostScript(
            "var intercepted = 'none';" +
            "globalThis.eval = function (text) { intercepted = text; return 'hijacked'; };",
            "test:eval-hijack");

        var answer = realm.EvaluateHostScript("var reached = 'host ran'; 6 * 7", "test:eval-after-hijack");

        Assert.True(answer == JsValue.Number(42d));
        Assert.Equal("host ran", Eval(realm, "reached", "test:eval-host-effect"));
        Assert.Equal("none", Eval(realm, "intercepted", "test:eval-not-intercepted"));
    }

    /// <summary>
    /// A page that replaces <c>eval</c> cannot get its own source compiled in a realm that forbids
    /// guest evaluation.
    /// </summary>
    /// <remarks>
    /// <b>This is the consequence of the interception rather than a second defect, and it is worth
    /// asserting separately because it is the one that matters.</b> A provider may mark its own
    /// evaluations so that a policy forbidding the page's <c>eval</c> does not forbid the bridge's
    /// polyfills. If a page can substitute a function for <c>eval</c>, that mark is held while the
    /// page's code runs - so the page reaches a compiler the policy took away, at a moment the
    /// provider believes it is talking to itself.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.HostScriptSource)]
    public void APageThatReplacesEvalCannotBorrowTheHostsPermissionToCompile(string engine)
    {
        var provider = Provider(engine);

        using var realm = provider.CreateRealm(new JsRealmOptions { AllowGuestEval = false });

        AssertHas(realm, JsCapabilities.HostScriptSource);

        realm.EvaluateHostScript(
            "var borrowed = 'not tried';" +
            "globalThis.eval = function (text) {" +
            "  try { borrowed = String(new Function('return 6 * 7')()); }" +
            "  catch (e) { borrowed = 'refused'; }" +
            "  return undefined;" +
            "};",
            "test:eval-borrow-setup");

        realm.EvaluateHostScript("var ran = true;", "test:eval-borrow-trigger");

        // Either the page's function was never reached, or it was reached and still refused. What
        // must not happen is 42.
        // READ AS A PROPERTY RATHER THAN EVALUATED, and that is not a style choice: if the
        // interception this asserts against were present, every EvaluateHostScript would BE the
        // page's function, so an evaluated read would report what the hijack returned rather than
        // what it did. Measured before the fix, an evaluated read answered "undefined" while the
        // property answered "42".
        //
        // "not tried" is the assertion, not merely "not 42": the page's substitute must never be
        // reached at all. A provider that reached it and happened to refuse the compile would be one
        // capture away from lending the permission.
        Assert.Equal("not tried", realm.GetProperty(realm.Global, "borrowed").AsString);
    }
}

