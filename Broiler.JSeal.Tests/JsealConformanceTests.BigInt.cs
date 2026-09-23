using Broiler.JSeal.Providers;

namespace Broiler.JSeal.Tests;

/// <summary>
/// B06: a BigInt through the contracts - exact values across properties, callbacks and calls, the
/// language's truthiness, the realm's strict equality, foreign handles, the BigInt typed arrays and
/// DataView accessors, and structured clone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every row comes from <see cref="BigIntSurfaces"/>, a stated table, not from a probe.</b> BigInt is
/// not a capability (see <see cref="ToBooleanIsTheHandlesAnswerExceptForABigInt"/>), so no
/// <see cref="JsCapabilities"/> flag selects these engines; the table does, and
/// <see cref="EveryEngineIsClassifiedForBigIntAndTheClassificationHolds"/> checks it against every
/// registered engine, so an engine cannot drop out of a witness by losing a global.
/// </para>
/// <para>
/// <b>Results are compared by value through <see cref="IJsValues.IsStrictlyEqual"/> or read back as
/// strings,</b> never with <see cref="JsValue"/>'s own operator, which compares BigInt handles by
/// reference (see <see cref="TheHandleOperatorComparesBigIntHandlesByReferenceOnly"/>).
/// </para>
/// </remarks>
public partial class JsealConformanceTests
{
    public static IEnumerable<object[]> EnginesWithBigIntTypedArrays =>
        EnginesWhere(surface => surface.TypedArrays);

    public static IEnumerable<object[]> EnginesWithBigIntDataViewAccessors =>
        EnginesWhere(surface => surface.DataViewAccessors);

    /// <summary>Engines classified as having BigInt that also declare the named capabilities.</summary>
    public static IEnumerable<object[]> EnginesWithBigIntDeclaring(JsCapabilities required)
    {
        var rows = EnginesDeclaring(required)
            .Where(row => BigIntSurfaces.TryGetValue((string)row[0], out var surface) && surface.Value)
            .ToArray();
        return rows.Length > 0 ? rows : [[NoEngineInThisBuild]];
    }

    private static IEnumerable<object[]> EnginesWhere(Func<BigIntSurface, bool> selected)
    {
        var rows = JsEngineRegistry.All
            .Where(provider => BigIntSurfaces.TryGetValue(provider.Name, out var surface) && selected(surface))
            .Select(provider => new object[] { provider.Name })
            .ToArray();
        return rows.Length > 0 ? rows : [[NoEngineInThisBuild]];
    }

    /// <summary>
    /// A placeholder row stands for "no registered engine is classified this way"; it asserts exactly
    /// that, so a row can never pass by naming no engine while one exists.
    /// </summary>
    private static bool IsPlaceholder(string engine, Func<BigIntSurface, bool> selected)
    {
        if (engine != NoEngineInThisBuild)
            return false;

        Assert.Empty(JsEngineRegistry.All.Where(provider =>
            BigIntSurfaces.TryGetValue(provider.Name, out var surface) && selected(surface)));
        return true;
    }

    /// <summary>
    /// A guest BigInt reaches the host as a BigInt handle and goes back exactly: through a property,
    /// as a callback argument and result, and through <see cref="IJsCalls.Invoke"/>.
    /// </summary>
    /// <remarks>
    /// The values are wider than a double's 53-bit mantissa and than 64 bits, and negative, so a
    /// provider boxing a BigInt as a Number, or narrowing it, fails on the digits rather than passing
    /// by rounding luck.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesWithBigInt))]
    public void ABigIntCrossesPropertiesCallbacksAndCallsExactly(string engine)
    {
        if (IsPlaceholder(engine, surface => surface.Value))
            return;

        using var realm = NewRealm(engine);

        var wide = realm.EvaluateHostScript("2n ** 200n + 1n", "test:bigint-wide");
        Assert.Equal(JsValueKind.BigInt, wide.Kind);

        // A property: the host writes the handle it was given and the guest reads the same value.
        realm.SetProperty(realm.Global, "held", wide);
        Assert.Equal("true", Eval(realm, "held === 2n ** 200n + 1n && typeof held === 'bigint'", "test:bigint-property"));
        Assert.Equal(JsValueKind.BigInt, realm.GetProperty(realm.Global, "held").Kind);

        // A callback: the argument arrives as a BigInt handle and the result goes back unchanged.
        var kinds = new List<JsValueKind>();
        realm.SetProperty(realm.Global, "echo", realm.NewMethod("echo", (in JsCall call) =>
        {
            kinds.Add(call[0].Kind);
            return call[0];
        }, 1));
        Assert.Equal("true", Eval(realm, "echo(-(2n ** 100n) - 7n) === -(2n ** 100n) - 7n", "test:bigint-callback"));
        Assert.Equal([JsValueKind.BigInt], kinds);

        // A result the callback did not receive: a handle minted by an earlier evaluation.
        realm.SetProperty(realm.Global, "give", realm.NewMethod("give", (in JsCall _) => wide));
        Assert.Equal("true", Eval(realm, "give() === held", "test:bigint-callback-result"));

        // Invoke: a BigInt argument and a BigInt result.
        var twice = realm.EvaluateHostScript("(function (x) { return x * 2n; })", "test:bigint-invoke");
        var doubled = realm.Invoke(twice, JsValue.Undefined, [wide]);
        Assert.Equal(JsValueKind.BigInt, doubled.Kind);
        realm.SetProperty(realm.Global, "doubled", doubled);
        Assert.Equal("true", Eval(realm, "doubled === 2n ** 201n + 2n", "test:bigint-invoke-result"));
        Assert.Equal("3213876088517980551083924184682325205044405987565585670602754", realm.ToJsString(doubled));
    }

    /// <summary>The handle's truthiness is wrong for a zero BigInt; the realm's is the language's.</summary>
    [Theory]
    [MemberData(nameof(EnginesWithBigInt))]
    public void ToBooleanOfEveryZeroBigIntIsFalseAndOfABigIntObjectTrue(string engine)
    {
        if (IsPlaceholder(engine, surface => surface.Value))
            return;

        using var realm = NewRealm(engine);

        foreach (var zero in new[] { "0n", "-0n", "2n ** 64n - 2n ** 64n", "BigInt(0)" })
        {
            var value = realm.EvaluateHostScript(zero, "test:bigint-zero");
            Assert.Equal((zero, JsValueKind.BigInt), (zero, value.Kind));
            Assert.False(realm.ToBoolean(value), zero);
        }

        foreach (var nonzero in new[] { "1n", "-1n", "2n ** 64n" })
            Assert.True(realm.ToBoolean(realm.EvaluateHostScript(nonzero, "test:bigint-nonzero")), nonzero);

        // A BigInt object is an object, and every object is true.
        var boxed = realm.EvaluateHostScript("Object(0n)", "test:bigint-object");
        Assert.Equal(JsValueKind.Object, boxed.Kind);
        Assert.True(realm.ToBoolean(boxed));
    }

    /// <summary>
    /// <see cref="IJsValues.IsStrictlyEqual"/> compares BigInts by mathematical value, and a BigInt
    /// never equals a Number.
    /// </summary>
    [Theory]
    [MemberData(nameof(EnginesWithBigInt))]
    public void IsStrictlyEqualComparesBigIntsByValue(string engine)
    {
        if (IsPlaceholder(engine, surface => surface.Value))
            return;

        using var realm = NewRealm(engine);

        // Two separate evaluations, so no provider can answer from a shared reference.
        var first = realm.EvaluateHostScript("10n ** 30n", "test:bigint-first");
        var second = realm.EvaluateHostScript("10n ** 29n * 10n", "test:bigint-second");
        var next = realm.EvaluateHostScript("10n ** 30n + 1n", "test:bigint-next");

        Assert.True(realm.IsStrictlyEqual(first, second));
        Assert.True(realm.IsStrictlyEqual(second, first));
        Assert.False(realm.IsStrictlyEqual(first, next));
        Assert.True(realm.IsStrictlyEqual(first, first));

        Assert.True(realm.IsStrictlyEqual(
            realm.EvaluateHostScript("0n", "test:bigint-zero"),
            realm.EvaluateHostScript("-0n", "test:bigint-negative-zero")));

        // `1n === 1` is false: strict equality never converts, and a Number is a different type.
        var one = realm.EvaluateHostScript("1n", "test:bigint-one");
        Assert.False(realm.IsStrictlyEqual(one, JsValue.Number(1d)));
        Assert.False(realm.IsStrictlyEqual(JsValue.Number(1d), one));
        Assert.False(realm.IsStrictlyEqual(one, JsValue.String("1")));

        // A BigInt object is an object: it equals only itself.
        var boxed = realm.EvaluateHostScript("Object(1n)", "test:bigint-object");
        Assert.False(realm.IsStrictlyEqual(boxed, one));
        Assert.True(realm.IsStrictlyEqual(boxed, boxed));
    }

    /// <summary>
    /// For every kind but BigInt, <see cref="IJsValues.IsStrictlyEqual"/> is the handle's own
    /// <c>==</c>, which is already <c>===</c> for those kinds, with <see cref="JsValue.Missing"/> taken
    /// as <c>undefined</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public void IsStrictlyEqualIsTheHandleOperatorForEveryOtherKind(string engine)
    {
        using var realm = NewRealm(engine);

        var @object = realm.NewObject();
        var symbol = realm.GetProperty(realm.GetProperty(realm.Global, "Symbol"), "iterator");
        var symbolAgain = realm.GetProperty(realm.GetProperty(realm.Global, "Symbol"), "iterator");
        Assert.Equal(JsValueKind.Symbol, symbol.Kind);

        JsValue[] values =
        [
            JsValue.Missing, JsValue.Undefined, JsValue.Null, JsValue.True, JsValue.False,
            JsValue.Number(0d), JsValue.Number(-0d), JsValue.Number(double.NaN), JsValue.Number(1d),
            JsValue.String(string.Empty), JsValue.String("1"), JsValue.String(new string('1', 1)),
            symbol, symbolAgain, realm.EvaluateHostScript("Symbol('probe')", "test:symbol"),
            @object, realm.NewObject(), realm.NewArray(),
        ];

        static JsValue AsEngineSeesIt(JsValue value) => value.IsMissing ? JsValue.Undefined : value;

        foreach (var left in values)
            foreach (var right in values)
                Assert.Equal((left.Kind, right.Kind, AsEngineSeesIt(left) == AsEngineSeesIt(right)), (left.Kind, right.Kind, realm.IsStrictlyEqual(left, right)));

        // Missing is no JS value; to === it is undefined, although the handles differ.
        Assert.NotEqual(JsValue.Missing, JsValue.Undefined);
        Assert.True(realm.IsStrictlyEqual(JsValue.Missing, JsValue.Undefined));
        Assert.True(realm.IsStrictlyEqual(JsValue.Undefined, JsValue.Missing));
        Assert.False(realm.IsStrictlyEqual(JsValue.Missing, JsValue.Null));

        // The two answers the language fixes and a reflexive comparison would get wrong.
        Assert.False(realm.IsStrictlyEqual(JsValue.Number(double.NaN), JsValue.Number(double.NaN)));
        Assert.True(realm.IsStrictlyEqual(JsValue.Number(0d), JsValue.Number(-0d)));
        Assert.True(realm.IsStrictlyEqual(symbol, symbolAgain));
    }

    /// <summary>
    /// The handle operator compares BigInt handles by reference: one handle equals itself and hashes
    /// with itself, and that is all it promises.
    /// </summary>
    /// <remarks>
    /// Whether two handles for one mathematical value compare equal under <c>==</c> is deliberately
    /// not asserted: it depends on whether the provider hands back the engine's own value or a new
    /// box per crossing, and the contract leaves it unspecified. Changing that operator would change
    /// the hash of every dictionary a host keys on a handle.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesWithBigInt))]
    public void TheHandleOperatorComparesBigIntHandlesByReferenceOnly(string engine)
    {
        if (IsPlaceholder(engine, surface => surface.Value))
            return;

        using var realm = NewRealm(engine);

        var value = realm.EvaluateHostScript("123n", "test:bigint-handle");
        var copy = value;
#pragma warning disable CS1718 // Comparing a handle to itself IS the assertion here.
        Assert.True(value == value);
#pragma warning restore CS1718
        Assert.True(value.Equals(copy));
        Assert.Equal(value.GetHashCode(), copy.GetHashCode());

        // A handle never equals a Number or a String under the operator either, whatever its value.
        Assert.False(value == JsValue.Number(123d));
        Assert.False(value == JsValue.String("123"));

        // And the handle's cheap truthiness is not ToBoolean: it answers true for every BigInt.
        Assert.True(realm.EvaluateHostScript("0n", "test:bigint-zero").AsBoolean);
    }

    /// <summary>
    /// A BigInt handle this engine did not mint is refused with <see cref="JsEngineException"/> by
    /// every member that would unwrap it, never dereferenced or answered from the handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A handle over a reference no engine recognises stands for "another engine's"; it runs on every
    /// engine, BigInt or not. Where a second engine with BigInt is registered (Release-VM), a real
    /// handle from it is refused too.
    /// </para>
    /// <para>
    /// This is the foreign-engine refusal the providers already apply to object handles, extended to
    /// BigInt: before B06 the Broiler.VM provider refused every BigInt handle because its engine had
    /// none, and Broiler.JS cast the reference and raised an <see cref="InvalidCastException"/>.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void AForeignBigIntHandleIsRefusedAsAnotherEnginesValue(string engine)
    {
        using var realm = NewRealm(engine);

        var foreigners = new List<(string From, JsValue Value)> { ("an unknown engine", JsProviderValue.BigInt(new object())) };
        foreach (var other in JsEngineRegistry.All.Where(provider => provider.Name != engine))
        {
            if (!BigIntSurfaces.TryGetValue(other.Name, out var surface) || !surface.Value)
                continue;
            using var otherRealm = other.CreateRealm(JsRealmOptions.Default);
            foreigners.Add((other.Name, otherRealm.EvaluateHostScript("5n", "test:bigint-foreign")));
        }

        var one = JsValue.Number(1d);
        foreach (var (from, foreign) in foreigners)
        {
            Assert.Equal(JsValueKind.BigInt, foreign.Kind);
            AssertRefused(from, () => realm.ToBoolean(foreign));
            AssertRefused(from, () => realm.IsStrictlyEqual(foreign, one));
            AssertRefused(from, () => realm.IsStrictlyEqual(foreign, foreign));
            AssertRefused(from, () => realm.ToJsString(foreign));
            AssertRefused(from, () => realm.SetProperty(realm.Global, "foreign", foreign));
        }

        // Nothing was stored along the way.
        Assert.Equal(JsValueKind.Undefined, realm.GetProperty(realm.Global, "foreign").Kind);

        static void AssertRefused(string from, Action operation)
        {
            var refusal = Record.Exception(operation);
            Assert.True(refusal is JsEngineException, $"a BigInt from {from}: {refusal?.GetType().Name ?? "no exception"}");
        }
    }

    /// <summary>
    /// B07: <c>BigInt64Array</c> and <c>BigUint64Array</c> store modulo 2^64 with their signedness,
    /// refuse a Number, and hand their elements to the host as exact BigInts.
    /// </summary>
    [Theory]
    [MemberData(nameof(EnginesWithBigIntTypedArrays))]
    public void TheBigIntTypedArraysWrapAndHandOutExactBigInts(string engine)
    {
        if (IsPlaceholder(engine, surface => surface.TypedArrays))
            return;

        using var realm = NewRealm(engine);

        Assert.Equal(
            "-1|-9223372036854775808|18446744073709551615|0|TypeError",
            Eval(realm,
                "(function () {" +
                " var signed = new BigInt64Array([-1n, 2n ** 63n]);" +
                " var unsigned = new BigUint64Array(signed.buffer);" +
                " var wrapped = new BigUint64Array([2n ** 64n]);" +
                " var refused; try { signed[0] = 1; } catch (e) { refused = e.name; }" +
                " return [signed[0], signed[1], unsigned[0], wrapped[0], refused].join('|'); })()",
                "test:bigint-typed-arrays"));

        var element = realm.EvaluateHostScript("new BigUint64Array([2n ** 64n - 1n])[0]", "test:bigint-element");
        Assert.Equal(JsValueKind.BigInt, element.Kind);
        Assert.True(realm.IsStrictlyEqual(element, realm.EvaluateHostScript("18446744073709551615n", "test:bigint-max")));
    }

    /// <summary>
    /// B08: the DataView BigInt accessors in both byte orders, with a BigInt the host handed in.
    /// </summary>
    [Theory]
    [MemberData(nameof(EnginesWithBigIntDataViewAccessors))]
    public void TheDataViewBigIntAccessorsHonourByteOrderAndSignedness(string engine)
    {
        if (IsPlaceholder(engine, surface => surface.DataViewAccessors))
            return;

        using var realm = NewRealm(engine);

        realm.SetProperty(realm.Global, "hostValue", realm.EvaluateHostScript("-2n", "test:bigint-host-value"));
        Assert.Equal(
            "18446744073709551614|-2|254|-72057594037927937|TypeError",
            Eval(realm,
                "(function () {" +
                " var view = new DataView(new ArrayBuffer(9));" +
                " view.setBigInt64(1, hostValue, true);" +
                " var refused; try { view.setBigUint64(0, 1); } catch (e) { refused = e.name; }" +
                " return [view.getBigUint64(1, true), view.getBigInt64(1, true), view.getUint8(1)," +
                " view.getBigInt64(1, false), refused].join('|'); })()",
                "test:bigint-dataview"));
    }

    /// <summary>
    /// A BigInt primitive, and a BigInt object, survive a same-realm clone with their values; the
    /// object is rebuilt as the realm's own BigInt object.
    /// </summary>
    [Theory]
    [MemberData(nameof(EnginesWithBigIntDeclaring), JsCapabilities.StructuredClone | JsCapabilities.HostScriptSource)]
    public void ABigIntSurvivesAStructuredClone(string engine)
    {
        if (IsPlaceholder(engine, surface => surface.Value))
            return;

        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.StructuredClone | JsCapabilities.HostScriptSource);

        var primitive = realm.Clone(realm.EvaluateHostScript("-(2n ** 70n)", "test:clone-bigint"));
        Assert.Equal(JsValueKind.BigInt, primitive.Kind);
        Assert.True(realm.IsStrictlyEqual(primitive, realm.EvaluateHostScript("-(2n ** 70n)", "test:clone-bigint-expected")));

        // The object half is the one a recorded gap may cover.
        AssertCloneCase(engine, nameof(ABigIntSurvivesAStructuredClone), () =>
        {
            var original = realm.EvaluateHostScript(
                "(function () { var boxed = Object(3n); return { value: 2n ** 65n, boxed: boxed, again: boxed }; })()",
                "test:clone-bigint-graph");
            realm.DefineValue(realm.Global, "cloneBigInt", realm.Clone(original));
            Assert.Equal(
                "true|true|true|true|true",
                Eval(realm,
                    "[cloneBigInt.value === 2n ** 65n, typeof cloneBigInt.boxed === 'object'," +
                    " Object.getPrototypeOf(cloneBigInt.boxed) === BigInt.prototype," +
                    " (function () { try { return BigInt.prototype.valueOf.call(cloneBigInt.boxed) === 3n; } catch (e) { return e.name; } })()," +
                    " cloneBigInt.boxed === cloneBigInt.again].join('|')",
                    "test:clone-bigint-read"));
        });
    }
}
