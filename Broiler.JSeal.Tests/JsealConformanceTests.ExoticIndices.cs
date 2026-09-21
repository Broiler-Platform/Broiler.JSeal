namespace Broiler.JSeal.Tests;

public partial class JsealConformanceTests
{
    [Theory]
    [MemberData(nameof(Engines))]
    public void ExoticIndicesTrackDenseGrowthReplacementAndShrinkage(string engine)
    {
        using var realm = NewRealm(engine);
        var handler = new DenseExotic();
        var target = realm.NewExotic(handler);
        realm.DefineValue(realm.Global, "collection", target);
        handler.Values.AddRange([JsValue.String("a"), JsValue.String("b")]);
        realm.EvaluateClassicScript("function readIndex() { return collection[1]; } for (var i = 0; i < 20; i++) readIndex();", "test:warm-index-read");
        Assert.Equal("a,b", realm.ToJsString(realm.EvaluateClassicScript("Array.prototype.join.call(collection, ',')", "test:dense-start")));
        Assert.Equal(new[] { "0", "1" }, realm.OwnPropertyNames(target));

        handler.Values[0] = JsValue.String("replaced");
        handler.Values.Add(JsValue.Undefined);
        Assert.Equal(JsValue.String("replaced"), realm.GetIndex(target, 0));
        Assert.True(realm.HasProperty(target, "2"));
        Assert.True(realm.GetIndex(target, 2).IsUndefined);
        Assert.Equal("0,1,2", realm.EvaluateClassicScript("Object.keys(collection).join(',')", "test:dense-growth").AsString);

        handler.Values.RemoveRange(1, 2);
        Assert.False(realm.HasProperty(target, "1"));
        Assert.True(realm.GetIndex(target, 1).IsUndefined);
        Assert.True(realm.EvaluateClassicScript("readIndex() === undefined", "test:cached-withdrawal").AsBoolean);
        Assert.True(realm.EvaluateClassicScript("Object.getOwnPropertyDescriptor(collection, '1') === undefined", "test:withdrawn-descriptor").AsBoolean);
        Assert.Equal("0", realm.EvaluateClassicScript("Object.getOwnPropertyNames(collection).join(',')", "test:dense-shrink").AsString);
        Assert.Equal("0", realm.EvaluateClassicScript("Object.keys({...collection}).join(',')", "test:withdrawn-spread").AsString);

        handler.Values.Clear();
        Assert.Empty(realm.OwnPropertyNames(target));
        handler.Values.Add(JsValue.String("regrown"));
        Assert.Equal(JsValue.String("regrown"), realm.GetIndex(target, 0));
        Assert.Equal(new[] { "0" }, realm.OwnPropertyNames(target));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ExoticIndicesPreserveOrdinaryExpandosAcrossLengthChanges(string engine)
    {
        using var realm = NewRealm(engine);
        var handler = new DenseExotic();
        var target = realm.NewExotic(handler);
        realm.DefineValue(realm.Global, "collection", target);
        realm.DefineIndex(target, 1, JsValue.Undefined);
        realm.EvaluateClassicScript("collection[3] = 'expando'", "test:index-expando");
        handler.Values.AddRange([JsValue.String("a"), JsValue.String("b"), JsValue.String("c"), JsValue.String("d")]);
        Assert.True(realm.GetIndex(target, 1).IsUndefined);
        Assert.Equal(JsValue.String("expando"), realm.GetIndex(target, 3));
        Assert.Equal(JsValue.String("c"), realm.GetIndex(target, 2));

        // Redefining a previously supplied index turns it into an ordinary property too.
        realm.DefineIndex(target, 0, JsValue.String("defined"));
        handler.Values[0] = JsValue.String("changed handler");
        Assert.Equal(JsValue.String("defined"), realm.GetIndex(target, 0));
        handler.Values.Clear();
        Assert.Equal(new[] { "0", "1", "3" }, realm.OwnPropertyNames(target));
        Assert.True(realm.GetIndex(target, 1).IsUndefined);
        Assert.Equal(JsValue.String("defined"), realm.GetIndex(target, 0));
        Assert.Equal(JsValue.String("expando"), realm.GetIndex(target, 3));
        Assert.False(realm.HasProperty(target, "2"));

        handler.Values.Add(JsValue.String("revealed"));
        Assert.True(realm.DeleteProperty(target, "0"));
        Assert.Equal(JsValue.String("revealed"), realm.GetIndex(target, 0));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ExoticIndicesKeepHandlerSlotsReadOnlyAndOrdinaryAccessorsIntact(string engine)
    {
        using var realm = NewRealm(engine);
        var handler = new DenseExotic();
        handler.Values.Add(JsValue.String("supplied"));
        var target = realm.NewExotic(handler);
        realm.DefineValue(realm.Global, "collection", target);
        Assert.Equal(JsValue.String("supplied"), realm.GetIndex(target, 0));
        Assert.False(realm.EvaluateClassicScript("Object.getOwnPropertyDescriptor(collection, '0').writable", "test:index-flags").AsBoolean);
        realm.EvaluateClassicScript("collection[0] = 'ignored'", "test:index-assignment");
        Assert.Equal(JsValue.String("supplied"), realm.GetIndex(target, 0));
        var calls = 0;
        realm.DefineAccessor(target, "0", (in JsCall call) =>
        {
            Assert.Equal(target, call.This);
            calls++;
            return JsValue.Undefined;
        }, null);
        Assert.True(realm.HasProperty(target, "0"));
        Assert.Equal(0, calls);
        Assert.True(realm.GetIndex(target, 0).IsUndefined);
        Assert.Equal(1, calls);
        handler.Values.Clear();
        Assert.True(realm.GetIndex(target, 0).IsUndefined);
        Assert.Equal(2, calls);
        Assert.Equal(new[] { "0" }, realm.OwnPropertyNames(target));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ExoticIndicesRejectHolesInsteadOfReturningStaleValues(string engine)
    {
        using var realm = NewRealm(engine);
        var handler = new DenseExotic();
        handler.Values.AddRange([JsValue.String("a"), JsValue.String("b")]);
        var target = realm.NewExotic(handler);
        Assert.Equal(JsValue.String("b"), realm.GetIndex(target, 1));
        handler.Hole = 1;
        Assert.Throws<InvalidOperationException>(() => realm.GetIndex(target, 1));
        Assert.Throws<InvalidOperationException>(() => realm.HasProperty(target, "1"));
        Assert.Throws<InvalidOperationException>(() => realm.OwnPropertyNames(target));
        handler.Hole = null;
        handler.Values[1] = JsValue.String("restored");
        Assert.Equal(JsValue.String("restored"), realm.GetIndex(target, 1));
        handler.Values.RemoveAt(1);
        Assert.True(realm.GetIndex(target, 1).IsUndefined);
        Assert.Equal(new[] { "0" }, realm.OwnPropertyNames(target));
    }

    private sealed class DenseExotic : IJsExotic
    {
        public List<JsValue> Values { get; } = [];
        public uint? Hole { get; set; }
        public uint IndexedLength => (uint)Values.Count;
        public IReadOnlyList<string> SupportedNames => [];
        public bool TrySetNamed(string name, JsValue value) => false;
        public bool TryGetNamed(string name, out JsValue value)
        {
            value = name == "length" ? JsValue.Number(Values.Count) : JsValue.Missing;
            return name == "length";
        }
        public bool TryGetIndex(uint index, out JsValue value)
        {
            value = JsValue.Missing;
            if (index >= Values.Count || index == Hole) return false;
            value = Values[(int)index];
            return true;
        }
    }
}
