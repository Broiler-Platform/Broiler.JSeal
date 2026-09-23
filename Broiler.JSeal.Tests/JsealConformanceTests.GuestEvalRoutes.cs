using Broiler.JSeal;

namespace Broiler.JSeal.Tests;

/// <summary>
/// Every route by which guest code could reach a compiler in a realm built with
/// <c>AllowGuestEval = false</c>, and the invocation boundary a host call crosses.
/// </summary>
/// <remarks>
/// <para>
/// <b>An audit, not a list of the obvious doors.</b> The source theories pin <c>eval</c>,
/// <c>new Function</c> and <c>ShadowRealm.prototype.evaluate</c>. A restricted page does not have to
/// use those spellings: the same compiler is reachable through every function kind's constructor via
/// a prototype, through <c>call</c>, <c>apply</c>, <c>bind</c> and <c>Reflect</c>, through a
/// ShadowRealm constructed in a roundabout way or nested in another, from a promise job, through
/// string timer callbacks, a host interop module, or a realm the host made elsewhere. Each is a
/// separate case here so a regression names the route it reopened.
/// </para>
/// <para>
/// Every route evaluates to 42 when it compiles. Where evaluation is permitted a route that compiles
/// is the control; where it is forbidden the same route has to be refused with the
/// <c>SyntaxError</c> both providers raise for a refused compile. A route an engine cannot run at all
/// (Broiler.VM's manifest does not admit the generator constructors, and it has no ShadowRealm) has
/// to fail identically in both realms, so the restriction cannot be credited with an engine's gap.
/// Broiler.JS runs every route, so there each one must compile when permitted.
/// </para>
/// </remarks>
public partial class JsealConformanceTests
{
    /// <summary>The routes, each an expression that answers 42 exactly when it compiled a string.</summary>
    private static readonly string[] GuestCompilationRoutes =
    [
        // eval, directly and reached as a value
        "(0, eval)('6 * 7')",
        "globalThis.eval('6 * 7')",
        "eval.call(null, '6 * 7')",
        "Reflect.apply(eval, undefined, ['6 * 7'])",

        // Function, reached without the binding
        "Function('return 6 * 7')()",
        "(function () {}).constructor('return 6 * 7')()",
        "(() => 0).constructor('return 6 * 7')()",
        "({ m() {} }).m.constructor('return 6 * 7')()",
        "[].constructor.constructor('return 6 * 7')()",
        "Function.prototype.constructor('return 6 * 7')()",
        "Reflect.construct(Function, ['return 6 * 7'])()",
        "Function.apply(null, ['return 6 * 7'])()",
        "Function.call(null, 'return 6 * 7')()",
        "Function.bind(null, 'return 6 * 7')()()",
        "(function () {}).bind().constructor('return 6 * 7')()",

        // the other function kinds, each reached through its prototype
        "Object.getPrototypeOf(function* () {}).constructor('yield 6 * 7')().next().value",
        "(Object.getPrototypeOf(async function () {}).constructor('return 1'), 42)",
        "(Object.getPrototypeOf(async function* () {}).constructor('yield 1'), 42)",

        // ShadowRealm, as written, reached oddly, and nested
        "new ShadowRealm().evaluate('6 * 7')",
        "ShadowRealm.prototype.evaluate.call(new ShadowRealm(), '6 * 7')",
        "ShadowRealm.prototype.evaluate.call(Reflect.construct(ShadowRealm, [], Object), '6 * 7')",
        "new (class extends ShadowRealm {})().evaluate('6 * 7')",
        "new ShadowRealm().evaluate('new ShadowRealm().evaluate(\"6 * 7\")')",
        "new ShadowRealm().evaluate('(0, eval)(\"6 * 7\")')",
        "new ShadowRealm().evaluate('Function(\"return 6 * 7\")()')",
    ];

    /// <summary>
    /// No route compiles a string in a realm that forbids guest evaluation.
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void EveryGuestRouteToTheCompilerIsRefusedInARealmThatForbidsGuestEvaluation(string engine)
    {
        var provider = Provider(engine);

        using var permissive = provider.CreateRealm(JsRealmOptions.Default);
        using var restricted = provider.CreateRealm(new JsRealmOptions { AllowGuestEval = false });

        for (var i = 0; i < GuestCompilationRoutes.Length; i++)
        {
            var route = GuestCompilationRoutes[i];
            var permitted = ProbeRoute(permissive, $"routePermitted{i}", route);
            var refused = ProbeRoute(restricted, $"routeRefused{i}", route);

            if (engine == "broiler-js" || permitted == "made:42")
            {
                Assert.Equal((route, "made:42"), (route, permitted));
                Assert.Equal((route, "refused:SyntaxError"), (route, refused));
            }
            else
            {
                Assert.Equal((route, permitted), (route, refused));
            }
        }
    }

    /// <summary>
    /// <c>ShadowRealm.prototype.importValue</c> is still the pinned engine's unimplemented stub in a
    /// restricted realm.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This pins why the one route Broiler.JS leaves open is unreachable, not that it is closed.</b>
    /// Code running inside a ShadowRealm asks the child context whether it may compile, and nothing
    /// subscribes to the child (VM decision JSD-0030, follow-up SR-6). With <c>evaluate</c> refused,
    /// the only other way to put code into the child is <c>importValue</c>, which the pinned engine
    /// answers with a synchronous <c>TypeError</c> saying it is not implemented. Any change to that
    /// answer - a different error, a promise, a fulfilment - fails here, so a package that starts
    /// loading modules through <c>importValue</c> cannot arrive without SR-6 being reviewed again.
    /// </para>
    /// <para>
    /// It cannot tell a fixed <c>importValue</c> from an unfixed one: no realm this repository builds
    /// can serve the child a module whose body calls <c>eval</c>. When importValue is implemented,
    /// this test has to be replaced by one that serves such a module and asserts the
    /// <c>SyntaxError</c>. Broiler.VM has no ShadowRealm, so there it must still be absent.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.Promises)]
    public void ShadowRealmImportValueIsStillUnimplementedInARealmThatForbidsGuestEvaluation(string engine)
    {
        using var restricted = NewRealm(engine, new JsRealmOptions { AllowGuestEval = false });
        AssertHas(restricted, JsCapabilities.Promises);

        restricted.EvaluateClassicScript(
            "var imported = 'absent';" +
            "if (typeof ShadowRealm === 'function') {" +
            "  try {" +
            "    imported = 'returned';" +
            "    var answer = new ShadowRealm().importValue('./module.js', 'value');" +
            "    if (answer && typeof answer.then === 'function') {" +
            "      imported = 'pending';" +
            "      answer.then(" +
            "        function (v) { imported = 'fulfilled:' + typeof v; }," +
            "        function (e) { imported = 'rejected:' + ((e && e.name) || 'unnamed') + ':' + (e && e.message); });" +
            "    }" +
            "  } catch (e) { imported = 'threw:' + ((e && e.name) || 'unnamed') + ':' + (e && e.message); }" +
            "}",
            "test:shadow-import");
        restricted.DrainJobs();

        var outcome = restricted.ToJsString(restricted.GetProperty(restricted.Global, "imported"));
        var expected = engine == "broiler-js"
            ? "threw:TypeError:ShadowRealm.prototype.importValue is not implemented"
            : "absent";
        Assert.Equal((engine, expected), (engine, outcome));
    }

    /// <summary>
    /// A promise job and an async continuation are still the restricted realm's code.
    /// </summary>
    /// <remarks>
    /// A job runs after the classic script that queued it has returned, from the host's drain rather
    /// than from inside a source member, so a policy held only for the duration of a host call would
    /// miss it. Indirect eval rather than direct: Broiler.VM does not admit a direct eval inside a
    /// function, and that gap must not answer for the restriction.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.Promises)]
    public void JobsQueuedByARestrictedRealmCompileNothing(string engine)
    {
        const string script =
            "var reaction = 'none', continuation = 'none';" +
            "Promise.resolve().then(function () {" +
            "  try { reaction = 'made:' + (0, eval)('6 * 7'); } catch (e) { reaction = 'refused:' + e.name; }" +
            "});" +
            "(async function () {" +
            "  await null;" +
            "  try { continuation = 'made:' + Function('return 6 * 7')(); } catch (e) { continuation = 'refused:' + e.name; }" +
            "})();";

        foreach (var allow in new[] { true, false })
        {
            using var realm = NewRealm(engine, new JsRealmOptions { AllowGuestEval = allow });
            AssertHas(realm, JsCapabilities.Promises);

            realm.EvaluateClassicScript(script, "test:jobs-compile");
            realm.DrainJobs();

            var expected = allow ? "made:42" : "refused:SyntaxError";
            Assert.Equal((allow, expected), (allow, realm.ToJsString(realm.GetProperty(realm.Global, "reaction"))));
            Assert.Equal((allow, expected), (allow, realm.ToJsString(realm.GetProperty(realm.Global, "continuation"))));
        }
    }

    /// <summary>
    /// No string callback, host interop global or interop module is a way around the refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Broiler.JS defines <c>setTimeout</c>, <c>setInterval</c> and <c>setImmediate</c>; a browser's
    /// accept a string and compile it, which <c>'unsafe-eval'</c> governs. Here they reject a string
    /// with a <c>TypeError</c> in every realm, so there is nothing to govern, and nothing runs; on
    /// Broiler.JS that <c>TypeError</c> is required, so a package that drops the timers is noticed. A
    /// provider without them passes by lacking them in both realms.
    /// </para>
    /// <para>
    /// The engine's CLR bridge is a module named <c>clr</c>. A realm this repository builds does not
    /// enable it, defines no interop global, and a dynamic <c>import('clr')</c> from a classic script
    /// is rejected without loading anything.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.Promises)]
    public void StringCallbacksAndHostInteropCompileNothingInARealmThatForbidsGuestEvaluation(string engine)
    {
        using var permissive = NewRealm(engine);
        using var restricted = NewRealm(engine, new JsRealmOptions { AllowGuestEval = false });
        AssertHas(restricted, JsCapabilities.Promises);

        foreach (var timer in new[] { "setTimeout", "setInterval", "setImmediate" })
        {
            var permitted = ProbeRoute(permissive, $"{timer}Permitted", $"typeof {timer} === 'function' ? {timer}('globalThis.leaked = 42', 0) : 'absent'");
            var refused = ProbeRoute(restricted, $"{timer}Refused", $"typeof {timer} === 'function' ? {timer}('globalThis.leaked = 42', 0) : 'absent'");

            if (engine == "broiler-js")
                Assert.Equal((timer, "refused:TypeError"), (timer, permitted));
            else
                Assert.True(permitted is "made:absent" or "refused:TypeError", $"{engine} {timer}: {permitted}");
            Assert.Equal((timer, permitted), (timer, refused));
        }

        foreach (var name in new[] { "clr", "require", "importScripts", "System", "Worker", "process" })
            Assert.Equal((name, "made:undefined"), (name, ProbeRoute(restricted, $"interop{name}", $"typeof {name}")));

        restricted.EvaluateClassicScript(
            "var clrImport = 'pending';" +
            "try { import('clr').then(function () { clrImport = 'loaded'; }, function (e) { clrImport = 'rejected:' + e.name; }); }" +
            "catch (e) { clrImport = 'threw:' + e.name; }",
            "test:clr-import");
        restricted.DrainJobs();
        restricted.DrainJobs();

        var clrImport = restricted.ToJsString(restricted.GetProperty(restricted.Global, "clrImport"));
        Assert.True(
            clrImport.StartsWith("rejected:", StringComparison.Ordinal) || clrImport.StartsWith("threw:", StringComparison.Ordinal),
            $"{engine}: {clrImport}");

        Assert.True(permissive.GetProperty(permissive.Global, "leaked").IsUndefined);
        Assert.True(restricted.GetProperty(restricted.Global, "leaked").IsUndefined);
    }

    /// <summary>
    /// A restricted realm's own code compiles nothing even when another realm's host API calls it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The policy belongs to the realm a function was made in, not to whichever realm's API the host
    /// happened to call through. Broiler.JS enters a function's own realm to run it, so the restricted
    /// realm's refusal fires and the host sees its <c>SyntaxError</c>; Broiler.VM refuses the foreign
    /// handle at the crossing, before any guest code runs. Both reach the host as a
    /// <see cref="JsEngineException"/> and neither compiles.
    /// </para>
    /// <para>
    /// The reverse direction is the host's grant, not a bypass, and is documented rather than tested
    /// here: ECMAScript asks the realm of the <c>eval</c> or <c>Function</c> being called, so a host
    /// that hands a permissive realm's <c>eval</c>, <c>Function</c> or ShadowRealm into a restricted
    /// one has given it compilation on Broiler.JS; Broiler.VM refuses the handle.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void ARestrictedRealmsFunctionCompilesNothingWhenAnotherRealmCallsIt(string engine)
    {
        var provider = Provider(engine);

        using var permissive = provider.CreateRealm(JsRealmOptions.Default);
        using var restricted = provider.CreateRealm(new JsRealmOptions { AllowGuestEval = false });

        var compileIndirectly = restricted.EvaluateClassicScript(
            "(function (source) { return (0, eval)(source); })", "test:restricted-function");

        var refusal = Assert.Throws<JsEngineException>(
            () => permissive.Invoke(compileIndirectly, JsValue.Undefined, [JsValue.String("6 * 7")]));

        if (engine == "broiler-js")
        {
            // The function ran in its own realm and its compile was refused there.
            Assert.True(refusal.Thrown.IsObject, refusal.Message);
            Assert.Equal(
                "SyntaxError",
                restricted.ToJsString(restricted.GetProperty(refusal.Thrown, "name")));
        }
        else
        {
            // The foreign handle never crossed, so nothing ran at all.
            Assert.Contains("different realm", refusal.Message, StringComparison.Ordinal);
        }

        // The control: the same function in the permissive realm compiles.
        var permittedFunction = permissive.EvaluateClassicScript(
            "(function (source) { return (0, eval)(source); })", "test:permissive-function");
        Assert.True(
            permissive.Invoke(permittedFunction, JsValue.Undefined, [JsValue.String("6 * 7")]) == JsValue.Number(42d));
    }

    /// <summary>
    /// A worker realm has only the policy its host gave it, and compilation does not cross by clone.
    /// </summary>
    /// <remarks>
    /// Structured clone refuses functions, so no compiled code or compiler travels between realms by
    /// messaging; a restricted realm that adopts a message from a permissive one stays restricted, and
    /// so does a restricted realm built on a second thread.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.WorkerRealms | JsCapabilities.ClassicScriptSource)]
    public void WorkerRealmsCarryNoCompilationIntoARealmThatForbidsGuestEvaluation(string engine)
    {
        var provider = Provider(engine);

        using var permissive = provider.CreateRealm(JsRealmOptions.Default);
        AssertHas(permissive, JsCapabilities.WorkerRealms | JsCapabilities.ClassicScriptSource);

        Assert.Throws<JsEngineException>(() => permissive.Detach(permissive.GetProperty(permissive.Global, "eval")));
        Assert.Throws<JsEngineException>(() => permissive.Detach(permissive.GetProperty(permissive.Global, "Function")));

        var message = permissive.EvaluateClassicScript("({ source: '6 * 7' })", "test:worker-message");
        var sent = permissive.Detach(message);

        string? outcome = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var restricted = provider.CreateRealm(new JsRealmOptions { AllowGuestEval = false });
                AssertHas(restricted, JsCapabilities.WorkerRealms | JsCapabilities.ClassicScriptSource);
                restricted.DefineValue(restricted.Global, "message", restricted.Adopt(sent));
                outcome = ProbeRoute(restricted, "workerCompile", "(0, eval)(message.source)");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(30)), "the worker realm did not finish");
        Assert.Null(failure);
        Assert.Equal("refused:SyntaxError", outcome);
    }

    /// <summary>
    /// <see cref="IJsCalls.Invoke"/> and <see cref="IJsCalls.Construct"/> refuse a value that is not
    /// callable, with a guest <c>TypeError</c>, before any of its traps can run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The J05 follow-up.</b> In ECMAScript a Proxy is callable exactly when its target is (ES2026
    /// 10.5), so an <c>apply</c> trap on a Proxy of a plain object is never consulted by a call. The
    /// pinned Broiler.JS engine does not follow that for guest calls either: <c>proxy()</c> and
    /// <c>Function.prototype.call.call(proxy)</c> run the trap, which is an engine defect recorded
    /// upstream and outside this contract. Broiler.JS's host invocation ran it too, so <c>realm.Invoke</c> answered 42 for a value its own classification called
    /// noncallable, while Broiler.VM refused the same call as a host API mistake. ECMAScript's
    /// <c>Call</c> throws a <c>TypeError</c> for a noncallable callee, and that is the answer both
    /// providers now give: a <see cref="JsEngineException"/> whose thrown value is the realm's
    /// <c>TypeError</c>, so a host callback that lets it propagate hands the page the error the
    /// language would have raised.
    /// </para>
    /// <para>
    /// The guard is the handle's classification, which the provider answered with the engine's own
    /// callability predicate. A revoked callable Proxy keeps its classification, reaches the engine,
    /// and fails there with the engine's <c>TypeError</c> (the callable-proxy theories pin that).
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void InvokingOrConstructingANoncallableValueIsAGuestTypeError(string engine)
    {
        using var realm = NewRealm(engine);

        realm.EvaluateClassicScript("var trapRuns = 0;", "test:trap-counter");
        string[] sources =
        [
            "new Proxy({}, { apply: function () { trapRuns++; return 42; } })",
            "new Proxy({}, { construct: function () { trapRuns++; return {}; } })",
            "({ call: function () { trapRuns++; return 42; } })",
        ];

        var candidates = new List<(string Label, JsValue Value)>();
        foreach (var source in sources)
            candidates.Add((source, realm.EvaluateClassicScript(source, "test:noncallable")));
        candidates.Add(("42", JsValue.Number(42d)));
        candidates.Add(("'text'", JsValue.String("text")));
        candidates.Add(("undefined", JsValue.Undefined));

        foreach (var (label, value) in candidates)
        {
            Assert.False(value.IsFunction, label);

            var invoked = Assert.Throws<JsEngineException>(() => realm.Invoke(value, JsValue.Undefined));
            AssertGuestTypeError(realm, invoked, $"Invoke({label})");

            var constructed = Assert.Throws<JsEngineException>(() => realm.Construct(value));
            AssertGuestTypeError(realm, constructed, $"Construct({label})");
        }

        Assert.Equal(0d, realm.GetProperty(realm.Global, "trapRuns").AsNumber);

        // The control: a callable Proxy with an apply trap still runs it.
        var callable = realm.EvaluateClassicScript(
            "new Proxy(function () {}, { apply: function () { trapRuns++; return 42; } })", "test:callable");
        Assert.True(realm.Invoke(callable, JsValue.Undefined) == JsValue.Number(42d));
        Assert.Equal(1d, realm.GetProperty(realm.Global, "trapRuns").AsNumber);
    }

    private static void AssertGuestTypeError(IJsRealm realm, JsEngineException error, string label)
    {
        Assert.True(error.Thrown.IsObject, $"{label}: the refusal carried no guest error ({error.Message})");
        realm.DefineValue(realm.Global, "invocationError", error.Thrown);
        Assert.True(
            realm.EvaluateClassicScript("invocationError instanceof TypeError", "test:invocation-error").AsBoolean,
            $"{label}: {error.Message}");
    }

    /// <summary>
    /// Evaluates <paramref name="route"/> inside a <c>try</c> in a classic script and answers
    /// <c>made:</c> and its value, or <c>refused:</c> and the error's name.
    /// </summary>
    private static string ProbeRoute(IJsRealm realm, string name, string route)
    {
        realm.EvaluateClassicScript(
            $"var {name} = (function () {{" +
            $"  try {{ return 'made:' + ({route}); }}" +
            "  catch (e) { return 'refused:' + ((e && e.name) || 'unnamed'); }" +
            "})();",
            "test:route");
        return realm.ToJsString(realm.GetProperty(realm.Global, name));
    }
}
