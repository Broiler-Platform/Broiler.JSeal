using Broiler.JSeal;

namespace Broiler.JSeal.Tests;

/// <summary>
/// <see cref="IJsExoticIndexedSet"/>: an indexed write completed by the host, the shape WebIDL's
/// indexed property setter gives <c>HTMLOptionsCollection</c> and <c>HTMLSelectElement</c>
/// (<c>select.options[i] = option</c> replaces, appends, or with <c>null</c> removes).
/// </summary>
public partial class JsealConformanceTests
{
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.ExoticObjects)]
    public void AnIndexedSetterTakesEveryIndexedWriteToTheObject(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.ExoticObjects);

        var handler = new ListExotic(realm, "a", "b");
        realm.DefineValue(realm.Global, "list", realm.NewExotic(handler));

        // Inside the range it replaces, at the end it appends, past the end it pads -- the handler
        // decides, and the next read sees what it decided rather than an ordinary property.
        realm.EvaluateHostScript("list[0] = 'A'; list[2] = 'c'; list[5] = 'f';", "test:indexed-writes");
        Assert.Equal(["A", "b", "c", "", "", "f"], handler.Items);
        Assert.Equal(
            "A,b,c,,,f|6|undefined",
            Eval(realm, "[list[0], list[1], list[2], list[3], list[4], list[5]].join(',') + '|' + Object.keys(list).length + '|' + list[6]", "test:indexed-reads"));

        // A numeric name is an index, from script and from the host alike.
        realm.EvaluateHostScript("list['1'] = 'B';", "test:string-index");
        realm.SetProperty(realm.GetProperty(realm.Global, "list"), "2", JsValue.String("C"));
        Assert.Equal(["A", "B", "C", "", "", "f"], handler.Items);

        // Taking an entry away shrinks the range, and no ordinary property is left behind to answer
        // for it.
        realm.EvaluateHostScript("list[5] = null;", "test:indexed-remove");
        Assert.Equal("5|undefined|false", Eval(realm, "Object.keys(list).length + '|' + list[5] + '|' + ('5' in list)", "test:indexed-after-remove"));

        // "007" and "length" are names, not indices: they are ordinary writes here.
        realm.EvaluateHostScript("list['007'] = 'padded'; list.extra = 'expando';", "test:names");
        Assert.Equal(5, handler.Items.Count);
        Assert.Equal("padded|expando", Eval(realm, "list['007'] + '|' + list.extra", "test:names-read"));
        Assert.Equal([0u, 2u, 5u, 1u, 2u, 5u], handler.Writes);
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.ExoticObjects)]
    public void AnIndexedSetterRefusesWithTheErrorItRaises(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.ExoticObjects);

        var handler = new ListExotic(realm, "a");
        realm.DefineValue(realm.Global, "list", realm.NewExotic(handler));

        // WebIDL converts the value before the setter runs, so the refusal is an error in sloppy code
        // as well as in strict code.
        Assert.Equal(
            "TypeError: refused 42|TypeError: refused 42|a",
            Eval(
                realm,
                "(function () {" +
                "  var out = [];" +
                "  try { list[0] = 42; out.push('no throw'); } catch (e) { out.push(e.name + ': ' + e.message); }" +
                "  (function () { 'use strict'; try { list[1] = 42; out.push('no throw'); } catch (e) { out.push(e.name + ': ' + e.message); } })();" +
                "  out.push(list[0]);" +
                "  return out.join('|');" +
                "})()",
                "test:indexed-refusal"));
        Assert.Equal(["a"], handler.Items);
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.ExoticObjects)]
    public void AWriteToAnObjectThatInheritsIsItsOwn(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.ExoticObjects);

        var handler = new ListExotic(realm, "a");
        realm.DefineValue(realm.Global, "list", realm.NewExotic(handler));

        // WebIDL's [[Set]] runs the setter only when the receiver is the object itself; a write that
        // reaches it through the prototype chain is an ordinary write to the inheriting object.
        Assert.Equal(
            "x|a|true",
            Eval(
                realm,
                "(function () {" +
                "  var child = Object.create(list);" +
                "  child[3] = 'x';" +
                "  return child[3] + '|' + list[0] + '|' + Object.prototype.hasOwnProperty.call(child, '3');" +
                "})()",
                "test:inherited-write"));
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.ExoticObjects)]
    public void ADeclinedIndexedWriteIsAnOrdinaryOne(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.ExoticObjects);

        var handler = new ListExotic(realm, "a") { Declines = true };
        realm.DefineValue(realm.Global, "list", realm.NewExotic(handler));

        realm.EvaluateHostScript("list[3] = 'ordinary';", "test:declined");

        Assert.Equal(["a"], handler.Items);
        Assert.Single(handler.Writes);
        Assert.Equal("ordinary|a", Eval(realm, "list[3] + '|' + list[0]", "test:declined-read"));
    }

    /// <summary>
    /// What a delete hook raises reaches the page as the error it is, as an indexed setter's does.
    /// </summary>
    /// <remarks>
    /// One provider reaches both hooks through a proxy trap rather than a minted host function, and
    /// a trap is not passed through the translation a host function gets: an error the hook raised
    /// escaped the page's <c>try</c> and ended the evaluation.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.ExoticObjects)]
    public void ADeleteHookRefusesWithTheErrorItRaises(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.ExoticObjects);

        realm.DefineValue(realm.Global, "area", realm.NewExotic(new RefusingDeleteExotic(realm)));

        Assert.Equal(
            "TypeError: locked",
            Eval(realm, "(function () { try { delete area.key; return 'no throw'; } catch (e) { return e.name + ': ' + e.message; } })()", "test:delete-refusal"));
    }

    private sealed class RefusingDeleteExotic(IJsRealm realm) : IJsExotic, IJsExoticDelete
    {
        public bool TryGetNamed(string name, out JsValue value)
        {
            value = JsValue.Missing;
            return false;
        }

        public bool TryGetIndex(uint index, out JsValue value)
        {
            value = JsValue.Missing;
            return false;
        }

        public bool TrySetNamed(string name, JsValue value) => false;

        public bool TryDeleteNamed(string name) => throw realm.Error(JsErrorKind.TypeError, "locked");

        public IReadOnlyList<string> SupportedNames => [];

        public uint IndexedLength => 0;
    }

    /// <summary>
    /// A list whose indexed setter behaves as an option list's: a string replaces or adds (padding
    /// with empty entries), <c>null</c> removes, and anything else is refused with a <c>TypeError</c>.
    /// </summary>
    private sealed class ListExotic(IJsRealm realm, params string[] items) : IJsExotic, IJsExoticIndexedSet
    {
        internal List<string> Items { get; } = [.. items];

        /// <summary>Every index the handler was offered, taken or declined.</summary>
        internal List<uint> Writes { get; } = [];

        internal bool Declines { get; init; }

        public bool TryGetNamed(string name, out JsValue value)
        {
            value = JsValue.Missing;
            return false;
        }

        public bool TryGetIndex(uint index, out JsValue value)
        {
            if (index >= Items.Count)
            {
                value = JsValue.Missing;
                return false;
            }

            value = JsValue.String(Items[(int)index]);
            return true;
        }

        public bool TrySetNamed(string name, JsValue value) => false;

        public bool TrySetIndex(uint index, JsValue value)
        {
            Writes.Add(index);
            if (Declines)
                return false;

            if (value.IsNull || value.IsUndefined)
            {
                if (index < Items.Count)
                    Items.RemoveAt((int)index);
                return true;
            }

            if (value.AsString is not { } text)
                throw realm.Error(JsErrorKind.TypeError, $"refused {realm.ToJsString(value)}");

            while (Items.Count < index)
                Items.Add(string.Empty);

            if (index < Items.Count)
                Items[(int)index] = text;
            else
                Items.Add(text);
            return true;
        }

        public IReadOnlyList<string> SupportedNames => [];

        public uint IndexedLength => (uint)Items.Count;
    }
}
