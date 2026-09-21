namespace Broiler.JSeal.Tests;

public partial class JsealConformanceTests
{
    public static IEnumerable<object[]> ExoticUndefinedProperties =>
        from engine in Engines
        from inherited in new[] { false, true }
        from kind in new[] { "data", "getter", "setter-only" }
        from supplied in new[] { false, true }
        select new object[] { engine[0], inherited, kind, supplied };

    [Theory]
    [MemberData(nameof(ExoticUndefinedProperties))]
    public void ExoticNamedPropertiesRespectOrdinaryUndefined(string engine, bool inherited, string kind, bool supplied)
    {
        using var realm = NewRealm(engine);
        var handler = new PrecedenceExotic { SuppliesName = supplied };
        var target = realm.NewExotic(handler);
        var owner = inherited ? realm.NewObject() : target;
        if (inherited) realm.SetPrototype(target, owner);
        realm.DefineValue(realm.Global, "target", target);
        realm.DefineValue(realm.Global, "owner", owner);
        realm.EvaluateClassicScript("var getterCalls = 0; var getterReceiver; Object.defineProperty(owner, 'name', " + (kind switch
        {
            "data" => "{ value: undefined, configurable: true }",
            "getter" => "{ get: function () { getterCalls++; getterReceiver = this; return undefined; }, configurable: true }",
            "setter-only" => "{ set: function (value) {}, configurable: true }",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        }) + ");", "test:ordinary-undefined");
        handler.Reads = 0;

        Assert.True(realm.HasProperty(target, "name"));
        Assert.Equal(0, realm.GetProperty(realm.Global, "getterCalls").AsNumber);
        Assert.True(realm.GetProperty(target, "name").IsUndefined);
        Assert.True(realm.EvaluateClassicScript("target.name === undefined", "test:undefined-precedence").AsBoolean);
        Assert.Equal(kind == "getter" ? 2 : 0, realm.GetProperty(realm.Global, "getterCalls").AsNumber);
        if (kind == "getter")
            Assert.Equal(target, realm.GetProperty(realm.Global, "getterReceiver"));
        Assert.Equal(0, handler.Reads);

        Assert.True(realm.DeleteProperty(owner, "name"));
        var expected = supplied ? JsValue.String("from handler") : JsValue.Undefined;
        Assert.Equal(expected, realm.GetProperty(target, "name"));
        Assert.Equal(expected, realm.EvaluateClassicScript("target.name", "test:after-delete"));
        Assert.Equal(2, handler.Reads);

        // An ordinary method still wins after the handler has supplied the same name.
        var method = realm.NewMethod("name", static (in JsCall _) => JsValue.Number(42));
        realm.DefineValue(owner, "name", method);
        handler.Reads = 0;
        Assert.Equal(method, realm.GetProperty(target, "name"));
        Assert.Equal(JsValue.Number(42), realm.EvaluateClassicScript("target.name()", "test:method-precedence"));
        Assert.Equal(0, handler.Reads);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ExoticNamedGetterCanDeleteItselfWithoutChangingTheCurrentRead(string engine)
    {
        using var realm = NewRealm(engine);
        foreach (var inherited in new[] { false, true })
        {
            var handler = new PrecedenceExotic { SuppliesName = true };
            var target = realm.NewExotic(handler);
            var owner = inherited ? realm.NewObject() : target;
            if (inherited) realm.SetPrototype(target, owner);
            realm.DefineValue(realm.Global, "target", target);
            realm.DefineValue(realm.Global, "owner", owner);
            realm.EvaluateClassicScript("Object.defineProperty(owner, 'name', { get: function () { delete owner.name; return undefined; }, configurable: true });", "test:self-deleting-getter");
            handler.Reads = 0;
            Assert.True(realm.EvaluateClassicScript("target.name === undefined", "test:first-read").AsBoolean);
            Assert.Equal(0, handler.Reads);
            Assert.Equal(JsValue.String("from handler"), realm.GetProperty(target, "name"));
            Assert.Equal(1, handler.Reads);
        }
    }

    private sealed class PrecedenceExotic : IJsExotic
    {
        public bool SuppliesName { get; init; }
        public int Reads { get; set; }
        public bool TryGetNamed(string name, out JsValue value)
        {
            value = JsValue.Undefined;
            if (name != "name") return false;
            Reads++;
            if (!SuppliesName) return false;
            value = JsValue.String("from handler");
            return true;
        }
        public bool TryGetIndex(uint index, out JsValue value) { value = JsValue.Missing; return false; }
        public bool TrySetNamed(string name, JsValue value) => false;
        public uint IndexedLength => 0;
        // The tests install the ordinary name themselves and do not enumerate handler names.
        public IReadOnlyList<string> SupportedNames => [];
    }
}
