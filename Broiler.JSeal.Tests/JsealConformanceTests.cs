using System.Numerics;
using System.Reflection;

using Broiler.JSeal.BroilerJs;
using Broiler.JSeal;

namespace Broiler.JSeal.Tests;

/// <summary>
/// Tests shared JSEAL behavior through the public contracts. Provider theories use
/// <see cref="Engines"/>; handle-only and coverage checks run as facts.
/// </summary>
/// <remarks>
/// Release registers Broiler.JS. Release-VM also registers Broiler.VM. Provider registration is
/// explicit setup; behavioral assertions use JSEAL values and realms. Engine-specific adoption
/// checks live separately in BroilerJsLifetimeTests.
/// </remarks>
public partial class JsealConformanceTests
{
    /// <summary>Register Broiler.JS before discovering provider theory data.</summary>
    /// <remarks>
    /// Module initializers run only after an assembly loads. Explicit registration ensures this
    /// provider is available at discovery time; VmProviderRegistration adds the VM in VM builds.
    /// </remarks>
    static JsealConformanceTests() => BroilerJsEngineProvider.Register();

    /// <summary>
    /// Every registered provider, by name.
    /// </summary>
    /// <remarks>
    /// The name rather than the provider instance so that a failing case names the engine it failed
    /// for â€” xUnit renders theory data into the test name, and an <see cref="IJsEngineProvider"/>
    /// renders as its type name at best. Reading a static member of this class is also what runs the
    /// type initializer above, so the registry is populated before it is enumerated.
    /// </remarks>
    public static IEnumerable<object[]> Engines =>
        JsEngineRegistry.All.Select(provider => new object[] { provider.Name });

    private static IJsEngineProvider Provider(string engine)
    {
        var provider = JsEngineRegistry.Find(engine);
        Assert.NotNull(provider);
        return provider!;
    }

#if BROILER_VM_JS
    /// <summary>VM configurations must discover both real providers.</summary>
    /// <remarks>
    /// Jseal/VmProviderRegistration.cs registers both providers from a module initializer. This check
    /// detects a missing conditional reference or registration that would silently omit VM theories.
    /// Registry mutation tests use isolated state and do not change the discovery registry.
    /// </remarks>
    [Fact]
    public void AVmBuildHasBothProvidersRegistered()
    {
        Assert.Contains(JsEngineRegistry.All, provider => provider.Name == "broiler-js");
        Assert.Contains(JsEngineRegistry.All, provider => provider.Name == "broiler-vm");

        // And the theories really do run for both, which is the property the suite is shaped around.
        Assert.Contains(Engines, row => (string)row[0] == "broiler-vm");
    }
#endif

    private static IJsRealm NewRealm(string engine, JsRealmOptions? options = null) =>
        Provider(engine).CreateRealm(options ?? JsRealmOptions.Default);

    /// <summary>Whether a realm lacks a capability, for the tests that need one.</summary>
    private static bool Lacks(IJsRealm realm, JsCapabilities capability) =>
        (realm.Capabilities & capability) == 0;

    /// <summary>
    /// Runs <paramref name="expression"/> as host script and answers it as a string, which is how
    /// most of the from-JavaScript assertions below read their result.
    /// </summary>
    private static string Eval(IJsRealm realm, string expression, string label = "test:probe") =>
        realm.ToJsString(realm.EvaluateHostScript(expression, label));

    // â”€â”€ values â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //
    // JsValue is a struct in the contract assembly with no provider involvement â€” a handle's kind,
    // its truthiness and its equality are decided by the contracts alone, which is the whole reason
    // those members exist rather than being realm calls. So these are Facts: running them once per
    // provider would assert the same code N times. The two value questions that DO need an engine â€”
    // whether an object is truthy, and whether two object handles compare by identity â€” are
    // theories further down, because minting an object is the provider's job.

    [Fact]
    public void EveryValueFactoryAnswersItsOwnKind()
    {
        Assert.Equal(JsValueKind.Missing, JsValue.Missing.Kind);
        Assert.Equal(JsValueKind.Undefined, JsValue.Undefined.Kind);
        Assert.Equal(JsValueKind.Null, JsValue.Null.Kind);
        Assert.Equal(JsValueKind.Boolean, JsValue.True.Kind);
        Assert.Equal(JsValueKind.Boolean, JsValue.False.Kind);
        Assert.Equal(JsValueKind.Boolean, JsValue.Boolean(false).Kind);
        Assert.Equal(JsValueKind.Number, JsValue.Number(0d).Kind);
        Assert.Equal(JsValueKind.String, JsValue.String("s").Kind);

        // A CLR null string is the absent DOM value, and the DOM says that is null rather than the
        // string "null" â€” JsValue.String documents this and 200-odd bridge sites depend on it.
        Assert.Equal(JsValueKind.Null, JsValue.String(null).Kind);
    }

    [Fact]
    public void MissingIsNotUndefinedAndIsWhatADefaultValueMeans()
    {
        // The distinction 598 argument reads in the bridge depend on: `scrollTo()` and
        // `scrollTo(undefined)` are different calls.
        Assert.True(default(JsValue).IsMissing);
        Assert.True(JsValue.Missing != JsValue.Undefined);
        Assert.False(JsValue.Missing.IsUndefined);
        Assert.False(JsValue.Undefined.IsMissing);
    }

    [Fact]
    public void IsNullishCoversMissingNullAndUndefined()
    {
        Assert.True(JsValue.Missing.IsNullish);
        Assert.True(JsValue.Null.IsNullish);
        Assert.True(JsValue.Undefined.IsNullish);

        Assert.False(JsValue.False.IsNullish);
        Assert.False(JsValue.Number(0d).IsNullish);
        Assert.False(JsValue.String(string.Empty).IsNullish);
    }

    /// <remarks>
    /// Named for the kinds the handle can decide, because there is one it cannot: a BigInt is an
    /// opaque reference here, so <see cref="JsValue.AsBoolean"/> answers <see langword="true"/> for
    /// <c>0n</c>. That kind is <see cref="IJsValues.ToBoolean"/>'s, asserted per provider by
    /// <c>ToBooleanIsTheHandlesAnswerExceptForABigInt</c>.
    /// </remarks>
    [Fact]
    public void AsBooleanIsEcmaScriptTruthinessForEveryKindTheHandleCanDecide()
    {
        Assert.False(JsValue.String(string.Empty).AsBoolean);

        // "0" is a non-empty string and therefore truthy, while the number 0 is not. A host that
        // reached for ToNumber here would get the opposite answer for the string.
        Assert.True(JsValue.String("0").AsBoolean);
        Assert.False(JsValue.Number(0d).AsBoolean);
        Assert.False(JsValue.Number(-0d).AsBoolean);
        Assert.False(JsValue.Number(double.NaN).AsBoolean);
        Assert.True(JsValue.Number(double.NegativeInfinity).AsBoolean);
        Assert.False(JsValue.Missing.AsBoolean);
        Assert.False(JsValue.Null.AsBoolean);
        Assert.False(JsValue.Undefined.AsBoolean);
    }

    [Fact]
    public void AsNumberDoesNotCoerceAStringAndAsStringDoesNotCoerceANumber()
    {
        // The cheap conversions answer only what the handle already knows; the coercions are on the
        // realm because they can run page script â€” all but ToBoolean, which runs nothing and is on
        // the realm because a handle cannot see inside a BigInt.
        Assert.True(double.IsNaN(JsValue.String("42").AsNumber));
        Assert.Equal(1d, JsValue.True.AsNumber);
        Assert.Equal(0d, JsValue.False.AsNumber);
        Assert.Null(JsValue.Number(42d).AsString);
        Assert.Equal("42", JsValue.String("42").AsString);
    }

    [Fact]
    public void StrictEqualityAndReflexiveEqualityDifferOnNaNAlone()
    {
        var nan = JsValue.Number(double.NaN);

        // `===` says a NaN is not itself, and JsValue.Equals says it is. Both are deliberate and the
        // suite asserts both, because a provider or a refactor that "fixed" either one would break
        // the other's caller: the language on one side, List.Contains on the other.
#pragma warning disable CS1718 // Comparing a value to itself IS the assertion here.
        Assert.False(nan == nan);
        Assert.True(nan != nan);
#pragma warning restore CS1718
        Assert.True(nan.Equals(nan));

        Assert.True(JsValue.String("a") == JsValue.String("a"));
        Assert.True(JsValue.Number(1d) == JsValue.Number(1d));
        Assert.False(JsValue.Number(1d) == JsValue.String("1"));
        Assert.True(JsValue.Missing == default);

        // Reflexive equality has to be usable as a dictionary key, so the hash has to agree with it.
        Assert.Equal(JsValue.String("a").GetHashCode(), JsValue.String("a").GetHashCode());
        Assert.Equal(nan.GetHashCode(), JsValue.Number(double.NaN).GetHashCode());
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void AnObjectIsTruthyAndTwoObjectHandlesCompareByIdentity(string engine)
    {
        using var realm = NewRealm(engine);

        var first = realm.NewObject();
        var second = realm.NewObject();

        Assert.Equal(JsValueKind.Object, first.Kind);
        Assert.True(first.IsObject);
        Assert.True(first.AsBoolean);

        // `{} === {}` is false and `x === x` is true. Across read paths a provider keeps identity only by
        // handing back one canonical reference per guest object â€” the engine's own, or a box made once per
        // identity (TwoHandlesForOneObjectAreEqualAndHashTheSame asserts it route by route) â€” which is
        // JsValue.ObjectIdentity, what the bridge's seven weak per-object tables key on.
        Assert.False(first == second);
#pragma warning disable CS1718 // Comparing a handle to itself IS the assertion here.
        Assert.True(first == first);
#pragma warning restore CS1718
        Assert.False(first.Equals(second));
        Assert.True(first.Equals(first));

        Assert.Equal(JsValueKind.Array, realm.NewArray().Kind);
        Assert.True(realm.NewArray().IsArray);
        Assert.Equal(JsValueKind.Function, realm.NewMethod("f", static (in JsCall _) => JsValue.Undefined).Kind);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void TheCoercionsEnterTheEngineWhereTheCheapConversionsCannot(string engine)
    {
        using var realm = NewRealm(engine);

        // The one correctness trap of the whole JSEAL migration: JsValue.ToString() is a diagnostic
        // rendering that never enters the engine, and realm.ToJsString is the observable ECMAScript
        // coercion, which on an object runs the toString the page wrote.
        var speaks = realm.EvaluateHostScript("({ toString: function () { return 'spoken'; } })", "test:tostring");

        Assert.Equal("[object]", speaks.ToString());
        Assert.Equal("spoken", realm.ToJsString(speaks));

        Assert.Equal(42d, realm.ToNumber(JsValue.String("42")));
        Assert.True(double.IsNaN(JsValue.String("42").AsNumber));
    }

    /// <summary>
    /// <see cref="IJsValues.ToBoolean"/> gives the handle's answer for every kind the handle can
    /// decide, and the language's answer for a BigInt, where the handle's is wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Agreement is half the contract.</b> If the two members could differ on an ordinary kind, no
    /// caller could safely keep the cheap one, and the DOM bridge reads the cheap one throughout. So every
    /// kind both providers can mint is asserted equal across the two, and the loop is bracketed by one
    /// falsy and one truthy answer, because a member answering a constant would agree with about half
    /// of it by accident.
    /// </para>
    /// <para>
    /// <b>The BigInt half is guarded on the realm having a <c>BigInt</c> global, not on a capability,</b>
    /// because a BigInt is not a capability: one engine implements the value kind and the other
    /// implements it nowhere, and no <see cref="JsCapabilities"/> flag tells them apart. Past the guard
    /// each value's kind is asserted before anything is asked of it, so a mint that produced something
    /// else fails rather than passing on a number. The guard is also the one way this test can pass
    /// vacuously: an engine that had BigInts and lost the global would skip the half that matters.
    /// </para>
    /// <para>
    /// <b>The handle's wrong answer is asserted too, on purpose.</b> If the handle is ever taught to
    /// answer <see langword="false"/> for <c>0n</c>, that line should fail, so that the reason this
    /// member exists is argued again rather than kept by habit.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void ToBooleanIsTheHandlesAnswerExceptForABigInt(string engine)
    {
        using var realm = NewRealm(engine);

        var symbol = realm.EvaluateHostScript("Symbol('probe')", "test:symbol");
        Assert.Equal(JsValueKind.Symbol, symbol.Kind);

        JsValue[] decidable =
        [
            JsValue.Missing, JsValue.Undefined, JsValue.Null,
            JsValue.True, JsValue.False,
            JsValue.Number(0d), JsValue.Number(-0d), JsValue.Number(double.NaN),
            JsValue.Number(1d), JsValue.Number(double.NegativeInfinity),
            JsValue.String(string.Empty), JsValue.String("0"), JsValue.String("false"),
            symbol,
            realm.NewObject(),
            realm.NewArray(),
            realm.NewMethod("f", static (in JsCall _) => JsValue.Undefined),
        ];

        Assert.False(realm.ToBoolean(JsValue.String(string.Empty)));

        // The kind rides along in the tuple so a failure names which value disagreed.
        foreach (var value in decidable)
            Assert.Equal((value.Kind, value.AsBoolean), (value.Kind, realm.ToBoolean(value)));

        Assert.True(realm.ToBoolean(realm.NewObject()));

        // A provider WITHOUT BigInt skips the half below, and the VM profile is one. But a provider that
        // HAS it must not be able to lose it and stay green: a skip nobody can see is a test that never
        // ran. Broiler.JS declares BigInt, so there the absence is a failure rather than a skip.
        var hasBigInt = realm.GetProperty(realm.Global, "BigInt").IsFunction;
        if (engine == "broiler-js")
            Assert.True(hasBigInt, "broiler-js exposes a BigInt global; the BigInt half must run there.");

        if (!hasBigInt)
            return;

        var zero = realm.EvaluateHostScript("0n", "test:bigint-zero");
        var one = realm.EvaluateHostScript("1n", "test:bigint-one");

        Assert.Equal(JsValueKind.BigInt, zero.Kind);
        Assert.Equal(JsValueKind.BigInt, one.Kind);

        // Wrong, and pinned as wrong: see the remarks.
        Assert.True(zero.AsBoolean);

        Assert.False(realm.ToBoolean(zero));
        Assert.True(realm.ToBoolean(one));
    }

    /// <summary>
    /// An object handle carries an identity a weak per-object table can key on, and it is the same
    /// one every time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what the bridge's seven per-object registries need, and it was believed impossible
    /// for a year.</b> Six doc comments said a <c>ConditionalWeakTable</c> could not be keyed from a
    /// handle because <see cref="JsValue"/> is a struct. The struct is not the key; the reference it
    /// carries is, and a provider already has to make that canonical per guest object because handle
    /// equality is defined by it.
    /// </para>
    /// <para>
    /// Asserted per provider rather than once, because "one reference per object, for the life of the
    /// realm" is a promise each provider keeps its own way - one hands back the engine's own object,
    /// the other boxes once per identity - and a provider that stopped keeping it would break every
    /// wrapper registry in the bridge with no other symptom.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void AnObjectHandleCarriesAWeakTableKeyThatIsTheSameEveryTime(string engine)
    {
        using var realm = NewRealm(engine);

        var made = realm.NewObject();
        realm.DefineValue(realm.Global, "kept", made);

        var identity = made.ObjectIdentity;
        Assert.NotNull(identity);

        // The same object read back by another route answers the same identity. This is the whole
        // claim: a table keyed on it finds the entry a different read path put there.
        var read = realm.GetProperty(realm.Global, "kept");
        Assert.True(read == made);
        Assert.Same(identity, read.ObjectIdentity);

        // And it actually works as a key, which is the use the bridge has for it.
        var table = new System.Runtime.CompilerServices.ConditionalWeakTable<object, string>();
        table.Add(identity!, "the entry");
        Assert.True(table.TryGetValue(read.ObjectIdentity!, out var found));
        Assert.Equal("the entry", found);

        // A different object is a different key.
        Assert.NotSame(identity, realm.NewObject().ObjectIdentity);

        // Functions and arrays are objects and carry one too - the bridge keys registries on all
        // three kinds.
        Assert.NotNull(realm.NewArray().ObjectIdentity);
        Assert.NotNull(realm.NewMethod("f", static (in _) => JsValue.Undefined).ObjectIdentity);

        // Nothing else does. A string carries its TEXT in the same field, and two equal literals are
        // usually one interned instance - so a table that accepted a string would let one string's
        // entry answer for another's.
        Assert.Null(JsValue.String("text").ObjectIdentity);
        Assert.Null(JsValue.Number(1d).ObjectIdentity);
        Assert.Null(JsValue.True.ObjectIdentity);
        Assert.Null(JsValue.Null.ObjectIdentity);
        Assert.Null(JsValue.Undefined.ObjectIdentity);
        Assert.Null(JsValue.Missing.ObjectIdentity);
    }

    /// <summary>
    /// Two handles for one guest object are equal, hash the same, and find one dictionary entry â€”
    /// whichever route each of them arrived by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The contract already named this test, and nothing was asserting it.</b>
    /// <see cref="JsValue.ObjectIdentity"/>'s remarks say a provider that did not hand back one
    /// handle per guest object "would already be failing
    /// <c>TwoHandlesForOneObjectAreEqualAndHashTheSame</c>" â€” and until this method existed that
    /// sentence was the only occurrence of the name in the repository. What it promises is what
    /// every retype in the bridge's migration lands on: a registry keyed on a handle finds its entry
    /// only if the handle the page hands back equals the handle the host put in.
    /// </para>
    /// <para>
    /// <b>The three routes are asserted separately because a provider can canonicalise on one and
    /// not on another.</b> <c>GetProperty</c> is the host reading its own object back; an evaluated
    /// expression is the engine handing one over; a call argument is how a listener actually
    /// arrives â€” <c>Features/EventTargetBinding.cs</c> stores <c>call[1]</c> and
    /// <c>removeEventListener</c> then has to find it again. A provider that minted a fresh wrapper
    /// on the argument path alone would break every <c>removeEventListener</c> in the browser and
    /// still pass a test that only read properties back.
    /// </para>
    /// <para>
    /// <b>Crossed with the other axis: who minted the object.</b> Three factories hand back
    /// something the HOST made and three hand back something the PAGE made, because a provider can
    /// canonicalise one and not the other - and the listener a page registers is page-minted, so
    /// the second three are the ones the browser depends on.
    /// </para>
    /// <para>
    /// <b>The four clauses in the loop are one question written four ways, and that is deliberate
    /// rather than redundant.</b> For an object kind, <see cref="JsValue.op_Equality"/>,
    /// <see cref="JsValue.Equals(JsValue)"/>, <see cref="JsValue.GetHashCode"/> and
    /// <see cref="JsValue.ObjectIdentity"/> all reduce to the identity of one field, so once the
    /// first passes the other three cannot fail. They are written out because each is a different
    /// caller's spelling - <c>==</c> in a binding, <c>Equals</c> in a <c>List.Contains</c>, the hash
    /// in a <c>Dictionary</c> bucket, the identity in a <c>ConditionalWeakTable</c> - and a change to
    /// any one of those branches should have to delete an assertion that names it. Nothing here
    /// claims they are four independent facts.
    /// </para>
    /// <para>
    /// <b>Every sameness claim below is guarded, because most of them pass on a handle that carries
    /// nothing.</b> <c>Missing == Missing</c> is <see langword="true"/>, two Missings hash alike
    /// (<see cref="JsValue.GetHashCode"/> answers the kind alone for them), and
    /// <c>Missing.ObjectIdentity</c> is <see langword="null"/> â€” so a realm whose reads silently
    /// answered Missing satisfies an identity test written only in the positive direction. Each
    /// route opens on the kind and the identity of what came back, before it compares anything.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void TwoHandlesForOneObjectAreEqualAndHashTheSame(string engine)
    {
        using var realm = NewRealm(engine);

        // Route 3's receiver, installed once. Script hands the object to a host function, which is
        // the shape a listener registration has.
        var passed = JsValue.Missing;
        realm.DefineValue(
            realm.Global,
            "handOver",
            realm.NewMethod("handOver", (in JsCall call) => { passed = call[0]; return JsValue.Undefined; }, 1));

        // The three kinds the bridge keys registries on. A provider that boxes once per identity has
        // three paths to get right, and an array and a function are objects with their own kinds.
        OneKind("anObject", JsValueKind.Object, realm.NewObject);
        OneKind("anArray", JsValueKind.Array, () => realm.NewArray());
        OneKind("aMethod", JsValueKind.Function, () => realm.NewMethod("minted", static (in JsCall _) => JsValue.Undefined));

        // AND THE SAME THREE MINTED BY THE PAGE RATHER THAN BY THE HOST, which is the half that
        // matters and the half a host-only test cannot see. A provider is free to canonicalise the
        // objects it was handed and mint a fresh wrapper for anything that originates in script; the
        // listener a page registers is page-minted, so that provider would break every
        // removeEventListener while passing all three cases above.
        OneKind("pageObject", JsValueKind.Object, () => realm.EvaluateClassicScript("({})", "test:page-object"));
        OneKind("pageArray", JsValueKind.Array, () => realm.EvaluateClassicScript("([])", "test:page-array"));
        OneKind("pageMethod", JsValueKind.Function, () => realm.EvaluateClassicScript("(function () {})", "test:page-method"));

        void OneKind(string name, JsValueKind kind, Func<JsValue> mint)
        {
            var made = mint();
            Assert.Equal(kind, made.Kind);
            Assert.NotNull(made.ObjectIdentity);

            realm.DefineValue(realm.Global, name, made);

            var read = realm.GetProperty(realm.Global, name);
            var evaluated = realm.EvaluateHostScript(name, $"test:identity-expression:{name}");

            // Reset before the call rather than after it, so that a provider which never invoked the
            // host function is caught by the guard below instead of inheriting the previous kind's
            // object â€” which would pass for two of the three kinds.
            passed = JsValue.Missing;
            realm.EvaluateHostScript($"handOver({name})", $"test:identity-argument:{name}");

            foreach (var (route, arrived) in new[] { ("property", read), ("expression", evaluated), ("argument", passed) })
            {
                // THE GUARD, BEFORE ANY SAMENESS CLAIM.
                Assert.Equal(kind, arrived.Kind);
                Assert.NotNull(arrived.ObjectIdentity);

                Assert.True(arrived == made, $"{name} via {route}: two handles for one object must be ==");
                Assert.True(arrived.Equals(made), $"{name} via {route}: and reflexively equal");
                Assert.Equal(made.GetHashCode(), arrived.GetHashCode());
                Assert.Same(made.ObjectIdentity, arrived.ObjectIdentity);
            }

            // And it works as a strong dictionary key, which is the use the bridge's registries have
            // for it. The default comparer reaches IEquatable<JsValue>.Equals rather than
            // op_Equality; the two differ on NaN alone and neither is reachable for an object, but
            // the registries are written against the comparer, so that is what this asserts.
            var table = new Dictionary<JsValue, string> { [made] = "the entry" };
            Assert.True(table.TryGetValue(read, out var found));
            Assert.Equal("the entry", found);
            Assert.True(table.TryGetValue(evaluated, out _));
            Assert.True(table.TryGetValue(passed, out _));

            // A DIFFERENT object of THE SAME KIND is a different key. Minted through the same factory
            // on purpose: a negative arm that compared an object against a function would be true on
            // the kind alone, and a provider answering one shared object for every mint would pass
            // everything above it.
            var other = mint();
            realm.DefineValue(realm.Global, name + "Other", other);
            Assert.NotNull(other.ObjectIdentity);
            Assert.Equal(kind, other.Kind);
            Assert.False(other == made);
            Assert.NotSame(other.ObjectIdentity, made.ObjectIdentity);
            Assert.False(table.ContainsKey(other));

            // ONE HANDLE PER OBJECT FOR THE LIFE OF THE REALM, which is the contract's wording and
            // is stronger than anything above. Every comparison so far is against the local copy of
            // `made`, and a provider holding a ONE-ENTRY cache satisfies all of them: it answers the
            // newest object correctly and forgets the one before it. So read the first object again
            // now that a second exists and the provider has been handed it.
            var reread = realm.GetProperty(realm.Global, name);
            Assert.Equal(kind, reread.Kind);
            Assert.True(reread == made, $"{name}: the first object keeps its handle after a second is minted");
            Assert.True(table.TryGetValue(reread, out _));
        }
    }

    // â”€â”€ capability coverage â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Which test above demonstrates each capability a provider may declare.
    /// </summary>
    /// <remarks>
    /// A capability is a claim a host is entitled to branch on without verifying it, so a suite that
    /// lets one go unexercised is letting a provider make a claim nothing checks. The meta-test
    /// below reads this table, so a capability added to <see cref="JsCapabilities"/> and declared by
    /// a provider fails the suite until a test for it exists â€” and the table cannot rot, because the
    /// test names are resolved by reflection rather than trusted.
    /// </remarks>
    private static readonly IReadOnlyDictionary<JsCapabilities, string> CapabilityCoverage =
        new Dictionary<JsCapabilities, string>
        {
            [JsCapabilities.HostScriptSource] = nameof(EvaluatingHostScriptAnswersTheValueOfTheLastExpression),
            [JsCapabilities.GuestEval] = nameof(ARealmBuiltWithoutGuestEvalRefusesDynamicSourceAndStillRunsHostScript),
            [JsCapabilities.ClassicScriptSource] = nameof(AClassicScriptRunsInARealmThatForbidsGuestEvaluation),
            [JsCapabilities.Promises] = nameof(APromiseSettlesFromTheHostAndItsReactionRunsAtTheNextDrain),
            [JsCapabilities.ExoticObjects] = nameof(AnOrdinaryPropertyWinsOverTheExoticHandler),
            [JsCapabilities.GlobalIsVariableScope] = nameof(ATopLevelDeclarationBecomesAPropertyOfTheGlobal),
            // Covers both halves of what the capability claims: the second realm on the second
            // thread, and values moved between the two by structured clone (IJsClone).
            [JsCapabilities.WorkerRealms] = nameof(ASecondRealmRunsOnASecondThread),
            [JsCapabilities.ReentrantHostCalls] = nameof(AHostFunctionMayCallBackIntoScriptWhileTheEngineIsInsideIt),
            [JsCapabilities.BinaryData] = nameof(AMintedArrayBufferIsTheRealmsOwnAndAViewOverItSeesTheBytes),
        };

    /// <summary>
    /// The capabilities no JSEAL contract can exercise, and why.
    /// </summary>
    /// <remarks>
    /// <b>This is a gap in the contracts, recorded here rather than papered over.</b>
    /// <see cref="JsCapabilities.Modules"/> and <see cref="JsCapabilities.DynamicImport"/> describe
    /// binding an ES-module import end to end, and <see cref="IJsSource"/> â€” the only contract that
    /// takes source â€” has no module entry point at all: its three members run a script, which is a
    /// different thing from instantiating and evaluating a module in a module map. So a provider can
    /// declare both and a host written against JSEAL alone has no way to use either, and no way to
    /// tell whether the claim is true. Nothing here can close that; a <c>EvaluateModule</c> on
    /// <see cref="IJsSource"/> would, and until there is one these two stay listed as unexercised
    /// on purpose rather than silently uncovered.
    /// </remarks>
    private static readonly IReadOnlySet<JsCapabilities> NotExpressibleThroughTheContracts =
        new HashSet<JsCapabilities> { JsCapabilities.Modules, JsCapabilities.DynamicImport };

    [Theory]
    [MemberData(nameof(Engines))]
    public void EveryCapabilityAProviderDeclaresIsExercisedBySomeTest(string engine)
    {
        var declared = Provider(engine).Capabilities;

        var unexercised = SingleCapabilities()
            .Where(capability => declared.HasFlag(capability))
            .Where(capability => !CapabilityCoverage.ContainsKey(capability))
            .Where(capability => !NotExpressibleThroughTheContracts.Contains(capability))
            .ToArray();

        Assert.True(
            unexercised.Length == 0,
            $"'{engine}' declares {string.Join(", ", unexercised)}, which no test in this suite exercises. " +
            "Add the test and name it in CapabilityCoverage, or record it in " +
            $"{nameof(NotExpressibleThroughTheContracts)} with the reason no contract reaches it.");
    }

    [Fact]
    public void EveryClaimOfCoverageNamesATestThatActuallyExists()
    {
        // The table above is a claim about this class, so it is checked against this class rather than
        // believed. A test renamed without updating the table would otherwise leave a capability
        // reported as covered by nothing at all.
        foreach (var (capability, testName) in CapabilityCoverage)
        {
            var method = typeof(JsealConformanceTests).GetMethod(
                testName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            Assert.True(method is not null, $"{capability} names '{testName}', which is not a method of this class.");
            Assert.True(
                method!.GetCustomAttributes(typeof(TheoryAttribute), inherit: false).Length == 1,
                $"{capability} names '{testName}', which is not a [Theory] over the registered providers.");
        }

        // And the two halves must not overlap: a capability cannot be both exercised and declared
        // inexpressible.
        Assert.Empty(CapabilityCoverage.Keys.Intersect(NotExpressibleThroughTheContracts));
    }

    /// <summary>Every capability that names one thing, so the composite <c>Document</c> is skipped.</summary>
    private static IEnumerable<JsCapabilities> SingleCapabilities() =>
        Enum.GetValues<JsCapabilities>().Where(value => BitOperations.PopCount((uint)value) == 1);
}

