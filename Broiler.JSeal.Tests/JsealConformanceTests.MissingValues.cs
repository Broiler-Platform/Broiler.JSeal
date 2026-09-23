namespace Broiler.JSeal.Tests;

// JsValue.Missing means "the host supplied no value". Where an engine needs a value - an argument,
// a property value, a callback's result - the only thing it can become is undefined, which is the
// rule BroilerJsMarshal.Unwrap documents. The VM provider handed JsHostValue.Missing through, and
// the profile resolves that to its uninitialised-binding marker, so a guest reading the argument
// threw "Cannot access a binding before initialisation" instead of seeing undefined.
public partial class JsealConformanceTests
{
    [Theory]
    [MemberData(nameof(Engines))]
    public void MissingIsUndefinedWhereTheEngineNeedsAValue(string engine)
    {
        using var realm = NewRealm(engine);

        var probe = realm.EvaluateHostScript(
            "(function (a, b) { return [arguments.length, typeof a, a === undefined, typeof b].join(); })",
            "missing-arguments");
        Assert.Equal(
            "2,undefined,true,number",
            realm.ToJsString(realm.Invoke(probe, JsValue.Undefined, [JsValue.Missing, JsValue.Number(1d)])));

        var target = realm.NewObject();
        realm.SetProperty(target, "x", JsValue.Missing);
        Assert.True(realm.HasProperty(target, "x"));
        Assert.True(realm.GetProperty(target, "x").IsUndefined);

        var callback = realm.NewMethod("answersNothing", (in JsCall call) => JsValue.Missing);
        var reader = realm.EvaluateHostScript(
            "(function (f) { var v = f(); return typeof v + ',' + (v === undefined); })",
            "missing-callback-result");
        Assert.Equal("undefined,true", realm.ToJsString(realm.Invoke(reader, JsValue.Undefined, [callback])));

        var constructor = realm.EvaluateHostScript("(function (a) { this.kind = typeof a; })", "missing-construct");
        var made = realm.Construct(constructor, [JsValue.Missing]);
        Assert.Equal("undefined", realm.ToJsString(realm.GetProperty(made, "kind")));
    }

    /// <summary>
    /// The crossings the upstream host surface now refuses outright (VM JSD-0024 section 20.4):
    /// a receiver, a target, an array element and a thrown value. None of them may reach a JSEAL
    /// caller as the engine's own argument refusal - either the provider maps Missing to undefined
    /// before the crossing, or the answer is the contract's own exception.
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void MissingAtEveryOtherCrossingIsUndefinedOrTheContractsOwnRefusal(string engine)
    {
        using var realm = NewRealm(engine);

        // A receiver: a call with no receiver, so a strict body sees undefined.
        var receiver = realm.EvaluateHostScript(
            "(function () { 'use strict'; return this === undefined; })", "missing-receiver");
        Assert.True(realm.Invoke(receiver, JsValue.Missing).AsBoolean);

        // A target: undefined is not an object, so a member that reads or writes through it answers
        // the language's TypeError, and DefineValue - which has no language operation to run - the
        // shared boundary's own refusal with no guest value. Neither is the engine's argument check.
        foreach (var operation in new (string Name, bool Guest, Action Run)[]
        {
            ("GetProperty", true, () => realm.GetProperty(JsValue.Missing, "x")),
            ("GetIndex", true, () => realm.GetIndex(JsValue.Missing, 0)),
            ("SetProperty", true, () => realm.SetProperty(JsValue.Missing, "x", JsValue.Number(1d))),
            ("DefineValue", false, () => realm.DefineValue(JsValue.Missing, "x", JsValue.Number(1d))),
        })
        {
            var refused = Assert.Throws<JsEngineException>(operation.Run);

            if (!operation.Guest)
            {
                Assert.True(refused.Thrown.IsMissing, $"{operation.Name} answered a guest value for a Missing target");
                continue;
            }

            realm.DefineValue(realm.Global, "refusal", refused.Thrown);
            Assert.True(
                realm.EvaluateClassicScript("refusal instanceof TypeError", "missing-target").AsBoolean,
                $"{operation.Name} refused a Missing target with something other than a TypeError");
        }

        // An element: undefined, not a hole, and not the engine's refusal.
        realm.DefineValue(realm.Global, "made", realm.NewArray([JsValue.Missing, JsValue.Number(1d)]));
        Assert.Equal(
            "2,true,undefined",
            realm.ToJsString(realm.EvaluateClassicScript(
                "[made.length, 0 in made, typeof made[0]].join()", "missing-element")));

        // A thrown value: Missing is "no guest value", so it stays a host failure and only an
        // explicit undefined becomes a guest throw of undefined.
        var hostFailure = new JsEngineException("host refusal without a guest value", JsValue.Missing);
        var raises = realm.NewMethod("raises", (in JsCall _) => throw hostFailure);
        realm.DefineValue(realm.Global, "raises", raises);
        Assert.Same(hostFailure, Assert.Throws<JsEngineException>(
            () => realm.EvaluateClassicScript("raises()", "missing-thrown")));

        // The same callback with an explicit undefined is a guest throw instead. What each engine
        // gives the guest to catch is its own (the pinned Broiler.JS does not hand over the value
        // itself), so only the route is asserted here.
        var throwsUndefined = realm.NewMethod(
            "throwsUndefined", (in JsCall _) => throw new JsEngineException("guest undefined", JsValue.Undefined));
        realm.DefineValue(realm.Global, "throwsUndefined", throwsUndefined);
        Assert.Equal(
            "caught",
            realm.ToJsString(realm.EvaluateClassicScript(
                "(function () { try { throwsUndefined(); } catch (e) { return 'caught'; } return 'not thrown'; })()",
                "missing-thrown-undefined")));

        // The coercions answer for undefined.
        Assert.Equal("undefined", realm.ToJsString(JsValue.Missing));
        Assert.True(double.IsNaN(realm.ToNumber(JsValue.Missing)));
        Assert.False(realm.ToBoolean(JsValue.Missing));
    }
}
