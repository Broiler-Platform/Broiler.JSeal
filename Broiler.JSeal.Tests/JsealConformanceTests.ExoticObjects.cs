using System.Numerics;
using System.Reflection;

using Broiler.JSeal;

namespace Broiler.JSeal.Tests;

/// <summary>
/// Exotic objects: indexed and named properties answered by a host handler, and deletion through one.
/// </summary>
public partial class JsealConformanceTests
{
    // ── exotic objects ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.ExoticObjects | JsCapabilities.HostScriptSource)]
    public void AnOrdinaryPropertyWinsOverTheExoticHandler(string engine)
    {
        using var realm = NewRealm(engine);

        var handler = new RecordingExotic();

        AssertHas(realm, JsCapabilities.ExoticObjects | JsCapabilities.HostScriptSource);

        var collection = realm.NewExotic(handler);

        // The case IJsExotic.cs describes in as many words: a collection containing an element
        // NAMED "item" must not shadow its own item() method. Ordinary properties are consulted
        // first and the handler answers only what they did not — getting this backwards is silently
        // wrong, and the failure is a page whose search box stops working, months later.
        realm.DefineValue(collection, "item", realm.NewMethod("item", static (in JsCall _) => JsValue.String("the method")));
        realm.DefineValue(realm.Global, "collection", collection);

        Assert.True(realm.GetProperty(collection, "item").IsFunction);
        Assert.Equal("the method", Eval(realm, "collection.item()", "test:exotic-item"));
        Assert.DoesNotContain("item", handler.AskedNames);

        // …and a name the ordinary properties do NOT have reaches the handler, from the host and
        // from script alike.
        Assert.True(realm.GetProperty(collection, "named") == JsValue.String("named:named"));
        Assert.Equal("named:named", Eval(realm, "collection.named", "test:exotic-named"));
        Assert.Contains("named", handler.AskedNames);

        // A name neither side has is undefined rather than an invention of the handler's.
        Assert.True(realm.GetProperty(collection, "nothing").IsUndefined);

        // Indexed lookup is asked of the handler at the moment of the read, so a live collection
        // reports what it holds now.
        Assert.True(realm.GetIndex(collection, 1) == JsValue.String("element 1"));
        Assert.Equal("element 0|element 1", Eval(realm, "collection[0] + '|' + collection[1]", "test:exotic-index"));

        handler.Count = 3;
        Assert.Equal("element 2", Eval(realm, "String(collection[2])", "test:exotic-grown"));

        // Shrinking matters as much as growing: an index whose element went away has to stop being
        // offered rather than keep answering with a stale handle, and it has to stop being a name
        // the object enumerates.
        handler.Count = 1;
        Assert.Equal(
            "undefined|undefined|0,item,named",
            Eval(
                realm,
                "String(collection[1]) + '|' + String(collection[2]) + '|' + Object.getOwnPropertyNames(collection).join(',')",
                "test:exotic-shrunk"));

        // A named write is offered to the handler before it becomes an ordinary property, because a
        // legacy platform object with a named setter has to see the value first.
        realm.EvaluateHostScript("collection.stored = 'written';", "test:exotic-set");
        Assert.Equal("stored=written", Assert.Single(handler.Writes));
    }

    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.ExoticObjects)]
    public void AnExoticObjectEnumeratesItsIndicesAndItsSupportedNames(string engine)
    {
        using var realm = NewRealm(engine);

        var handler = new RecordingExotic();

        AssertHas(realm, JsCapabilities.ExoticObjects);

        var collection = realm.NewExotic(handler);
        realm.DefineValue(collection, "item", JsValue.String("the method"));
        realm.DefineValue(realm.Global, "enumerable", collection);

        // Indices, then ordinary properties, then the handler's supported names — the order the
        // provider's enumerator produces, pinned so that a second provider has a shape to match
        // rather than a blank to fill in. The indices are in the list at all because presence,
        // enumeration and retrieval are separate entry points with no single hook between them, so
        // an index has to be a real own property for any generic algorithm to find it.
        Assert.Equal(
            "0,1,item,named",
            Eval(
                realm,
                "(function () { var seen = []; for (var k in enumerable) { seen.push(k); } return seen.join(','); })()",
                "test:exotic-forin"));

        Assert.Equal("0,1,item,named", Eval(realm, "Object.getOwnPropertyNames(enumerable).join(',')", "test:exotic-own"));

        // `in` reaches the handler too, which is what makes a named lookup answerable before it is
        // read rather than only when it is.
        Assert.Equal("true", Eval(realm, "String('named' in enumerable)", "test:exotic-in"));
        Assert.Equal("false", Eval(realm, "String('nothing' in enumerable)", "test:exotic-not-in"));
    }

    /// <summary>
    /// An exotic object's supported names in <c>Object.keys</c> and in a spread.
    /// </summary>
    /// <remarks>
    /// <b>This test found a defect in the Broiler.JS provider, and the defect is fixed — the
    /// remark below is kept because it is the only record of what was wrong.</b>
    /// <see cref="IJsExotic.SupportedNames"/> says in as many words that the names are "for
    /// <c>Object.keys</c>, <c>for…in</c> and spread", and the provider supplied them by appending to
    /// <c>GetAllKeys</c> alone — which is enough for <c>for…in</c> and
    /// <c>Object.getOwnPropertyNames</c> (both pass, above) and not enough for the other two.
    /// <c>Object.keys</c> implements EnumerableOwnProperties: it snapshots the own keys and then asks
    /// <c>[[GetOwnProperty]]</c> for each one, keeping only the enumerable ones — and a supported
    /// name had no own descriptor, so it was dropped. <c>Object.assign</c>, and therefore an object
    /// spread, filtered the same way, so a page enumerating a form's controls with
    /// <c>Object.keys(form.elements)</c> or <c>{...form.elements}</c> got the indices and the
    /// interface's own members but none of the named controls.
    /// <c>BroilerJsExoticObject.GetOwnPropertyDescriptor</c> synthesises the descriptor now, on each
    /// ask rather than installed, and states why installing would be the shorter wrong answer.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.ExoticObjects)]
    public void AnExoticObjectsSupportedNamesReachObjectKeysAndSpread(string engine)
    {
        using var realm = NewRealm(engine);

        var handler = new RecordingExotic();

        AssertHas(realm, JsCapabilities.ExoticObjects);

        var collection = realm.NewExotic(handler);
        realm.DefineValue(collection, "item", JsValue.String("the method"));
        realm.DefineValue(realm.Global, "enumerable", collection);

        Assert.Equal("0,1,item,named", Eval(realm, "Object.keys(enumerable).join(',')", "test:exotic-keys"));
        Assert.Equal("0,1,item,named", Eval(realm, "Object.keys(Object.assign({}, enumerable)).join(',')", "test:exotic-spread"));
    }

    /// <summary>
    /// A deletion on an exotic object reaches the handler, not only the ordinary property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case <c>Storage</c> is the only object in the bridge to have.</b>
    /// <c>delete localStorage.foo</c> has to take the ITEM out, so <c>getItem</c> stops answering
    /// and <c>length</c> stops counting; a deletion that reached only the property would leave the
    /// two disagreeing, which is a wrong answer rather than a missing feature.
    /// </para>
    /// <para>
    /// <b>The two providers reach it by different routes and must not be distinguishable here.</b>
    /// One overrides the virtual its engine dispatches a deletion through; the other has no such
    /// hook on its host-object surface and puts the object behind the realm's own <c>Proxy</c> with
    /// a single <c>deleteProperty</c> trap. This test names neither.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.ExoticObjects)]
    public void ADeletionOnAnExoticObjectReachesTheHandlerAndTakesTheNameWithIt(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.ExoticObjects);

        var handler = new DeletingExotic();
        var area = realm.NewExotic(handler);
        realm.DefineValue(realm.Global, "area", area);

        realm.EvaluateHostScript("area.stored = 'written';", "test:delete-write");
        Assert.Equal("written", Eval(realm, "area.stored", "test:delete-read"));
        Assert.Contains("stored", realm.OwnPropertyNames(area));

        Assert.Equal("true", Eval(realm, "String(delete area.stored)", "test:delete"));
        Assert.Equal("undefined", Eval(realm, "String(area.stored)", "test:delete-gone"));
        Assert.DoesNotContain("stored", realm.OwnPropertyNames(area));
        Assert.Equal(["stored"], handler.Deletions);

        // A name the handler DECLINED on the write is an expando the page put there, and deleting it
        // is the ordinary deletion. The handler is still asked - the ordering for a delete mirrors
        // the one for a write, not the one for a read - and declining leaves the property to go the
        // way it would on any object.
        Assert.Equal("true", Eval(realm, "(area.expando = 1, String(delete area.expando))", "test:delete-expando"));
        Assert.Equal("undefined", Eval(realm, "String(area.expando)", "test:expando-gone"));
        Assert.Equal(["stored", "expando"], handler.Deletions);
    }

    /// <summary>
    /// A digit-only key never reaches the delete hook, on either provider.
    /// </summary>
    /// <remarks>
    /// <b>The two engines route an integer-index key away from the named hooks by different
    /// machinery, and this pins that they agree.</b> One dispatches it to a separate indexed
    /// virtual; the other hands its proxy trap the string <c>"7"</c> like any other key, so the
    /// provider filters it. Without the filter <c>delete area[7]</c> would remove an item under one
    /// engine and not the other, which is worse than the gap they share - and that gap is why
    /// <c>WebStorageTests.ADigitOnlyKeyIsANamedPropertyLikeAnyOther</c> is still skipped.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.ExoticObjects)]
    public void ADigitOnlyKeyDoesNotReachTheDeleteHook(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.ExoticObjects);

        var handler = new DeletingExotic();
        realm.DefineValue(realm.Global, "area", realm.NewExotic(handler));

        realm.EvaluateHostScript("area[7] = 'seven'; area['007'] = 'padded';", "test:index-write");
        realm.EvaluateHostScript("delete area[7]; delete area['007'];", "test:index-delete");

        // "007" is a name and 7 is an index, which is the line the language draws and not one this
        // contract invents.
        Assert.Equal(["007"], handler.Deletions);
    }

    /// <summary>
    /// A symbol-keyed deletion works and is not offered to the handler.
    /// </summary>
    /// <remarks>
    /// <b>It is here because one provider's route can silently drop it.</b> That provider's trap is
    /// handed every key kind the guest can delete by, and the host surface it would naturally
    /// forward through deletes by string name only - so a symbol-keyed deletion routed that way
    /// would answer <see langword="true"/> and remove nothing, with the proxy's own invariant check
    /// unable to catch it because the property is configurable. Forwarding through the captured
    /// <c>Reflect.deleteProperty</c> is what closes it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.ExoticObjects)]
    public void ASymbolKeyedDeletionRemovesThePropertyAndIsNotOfferedToTheHandler(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.ExoticObjects);

        var handler = new DeletingExotic();
        realm.DefineValue(realm.Global, "area", realm.NewExotic(handler));

        Assert.Equal(
            "before=marked after=undefined deleted=true",
            Eval(
                realm,
                "(function () {" +
                "  var key = Symbol('mark');" +
                "  area[key] = 'marked';" +
                "  var before = area[key];" +
                "  var deleted = delete area[key];" +
                "  return 'before=' + before + ' after=' + area[key] + ' deleted=' + deleted;" +
                "})()",
                "test:symbol-delete"));

        Assert.Empty(handler.Deletions);
    }

    /// <summary>
    /// A deleting handler is read, written and enumerated exactly as a plain one is.
    /// </summary>
    /// <remarks>
    /// <b>One provider serves a deleting handler through a different kind of object, and this is the
    /// assertion that the difference is invisible.</b> Reads, the ordering rule, member installation
    /// by the host, <c>in</c>, <c>Object.keys</c> and object spread all have to answer what they
    /// answer for a handler with no deletion - otherwise converting <c>Storage</c> onto this
    /// contract would fix its deletions and break its enumeration.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.ExoticObjects)]
    public void ADeletingExoticIsReadAndEnumeratedExactlyAsAPlainOneIs(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.ExoticObjects);

        var handler = new DeletingExotic();
        var area = realm.NewExotic(handler);

        // The host installs a member on the object it just minted, which on the proxy route reaches
        // the target while the realm is installing. An installed member must not be offered to the
        // handler and must outrank a name the handler owns.
        realm.DefineValue(area, "getItem", JsValue.String("the method"), JsPropertyFlags.NonEnumerable);
        realm.DefineValue(realm.Global, "area", area);

        realm.EvaluateHostScript("area.alpha = 'one'; area.beta = 'two';", "test:plain-writes");

        Assert.Equal("one", Eval(realm, "area.alpha", "test:plain-read"));
        Assert.Equal("the method", Eval(realm, "area.getItem", "test:plain-ordinary-wins"));
        Assert.Equal("true", Eval(realm, "String('alpha' in area)", "test:plain-in"));
        Assert.Equal("false", Eval(realm, "String('gamma' in area)", "test:plain-not-in"));
        Assert.Equal("alpha,beta", Eval(realm, "Object.keys(area).join(',')", "test:plain-keys"));
        Assert.Equal("alpha,beta", Eval(realm, "Object.keys(Object.assign({}, area)).join(',')", "test:plain-spread"));
        Assert.Equal(["alpha", "beta"], realm.OwnPropertyNames(area));
    }

    /// <summary>
    /// An exotic handler with no delete hook is unchanged by this contract.
    /// </summary>
    /// <remarks>
    /// <b>Both engines answer <see langword="true"/> for a deletion nobody claimed, and the name
    /// goes on answering.</b> WebIDL would have a named property with no deleter return
    /// <see langword="false"/>; neither engine does, this contract deliberately does not change it,
    /// and this test is here so a later reader knows it was decided rather than missed.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.ExoticObjects)]
    public void AnExoticObjectWithNoDeleteHookIsUnchangedByThisContract(string engine)
    {
        using var realm = NewRealm(engine);

        AssertHas(realm, JsCapabilities.ExoticObjects);

        var handler = new RecordingExotic();
        realm.DefineValue(realm.Global, "collection", realm.NewExotic(handler));

        Assert.Equal("true", Eval(realm, "String(delete collection.named)", "test:no-hook-delete"));
        Assert.Equal("named:named", Eval(realm, "collection.named", "test:no-hook-after"));
    }

    /// <summary>
    /// An <see cref="IJsExotic"/> that owns its names and removes them - the shape <c>Storage</c>
    /// has and the other five lookup-completing objects do not.
    /// </summary>
    private sealed class DeletingExotic : IJsExotic, IJsExoticDelete
    {
        private readonly Dictionary<string, string> _items = new(StringComparer.Ordinal);

        /// <summary>Every name the handler was asked to delete, answered or declined.</summary>
        internal List<string> Deletions { get; } = [];

        public bool TryGetNamed(string name, out JsValue value)
        {
            if (!_items.TryGetValue(name, out var stored))
            {
                value = JsValue.Missing;
                return false;
            }

            value = JsValue.String(stored);
            return true;
        }

        /// <summary>None: this handler models a named-property object with no indexed ones.</summary>
        public bool TryGetIndex(uint index, out JsValue value)
        {
            value = JsValue.Missing;
            return false;
        }

        /// <summary>
        /// Claims every name but <c>expando</c>, so the tests have a property the handler owns and
        /// one it does not - the two sides a delete hook has to keep apart.
        /// </summary>
        public bool TrySetNamed(string name, JsValue value)
        {
            if (name is "expando")
                return false;

            _items[name] = value.AsString ?? string.Empty;
            return true;
        }

        /// <inheritdoc />
        public bool TryDeleteNamed(string name)
        {
            Deletions.Add(name);
            return _items.Remove(name);
        }

        public IReadOnlyList<string> SupportedNames => _items.Keys.ToArray();

        public uint IndexedLength => 0;
    }

    /// <summary>
    /// An <see cref="IJsExotic"/> that records what it was asked, so the ordering rule can be
    /// asserted from the handler's side as well as from the value's.
    /// </summary>
    private sealed class RecordingExotic : IJsExotic
    {
        private static readonly string[] Names = ["named"];

        /// <summary>Every name the handler was consulted about, answered or not.</summary>
        /// <remarks>
        /// Asked, not answered, because the ordering rule is about whether the handler is
        /// <em>reached</em>: a name the ordinary properties satisfied must never arrive here at all.
        /// </remarks>
        internal List<string> AskedNames { get; } = [];

        internal List<string> Writes { get; } = [];

        internal uint Count { get; set; } = 2;

        public bool TryGetNamed(string name, out JsValue value)
        {
            AskedNames.Add(name);

            if (!Names.Contains(name))
            {
                value = JsValue.Missing;
                return false;
            }

            value = JsValue.String($"named:{name}");
            return true;
        }

        public bool TryGetIndex(uint index, out JsValue value)
        {
            if (index >= Count)
            {
                value = JsValue.Missing;
                return false;
            }

            value = JsValue.String($"element {index}");
            return true;
        }

        public bool TrySetNamed(string name, JsValue value)
        {
            Writes.Add($"{name}={value.AsString}");
            return true;
        }

        public IReadOnlyList<string> SupportedNames => Names;

        public uint IndexedLength => Count;
    }

    /// <summary>
    /// A page that replaces <c>Proxy</c> or <c>Reflect.deleteProperty</c> changes nothing about an
    /// exotic object the host mints afterwards, or about how a deletion on it is completed.
    /// </summary>
    /// <remarks>
    /// <b>Both are writable globals, and the object is minted after the page has run.</b> A provider
    /// that reached either when a storage area was minted, or when a deletion was completed, would
    /// hand the page every exotic object the bridge builds, or let it decide what a deletion
    /// answers. The replacements count their calls and the count must stay zero, while the
    /// deletion still reaches the handler once and the ordinary deletion still runs.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.ExoticObjects | JsCapabilities.HostScriptSource)]
    public void APageThatReplacesProxyOrReflectDoesNotChangeAnExoticDeletion(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.ExoticObjects | JsCapabilities.HostScriptSource);

        realm.EvaluateHostScript(
            "globalThis.pageCalls = 0;" +
            "globalThis.Proxy = function () { globalThis.pageCalls++; return {}; };" +
            "Reflect.deleteProperty = function () { globalThis.pageCalls++; return false; };",
            "test:replace-proxy");

        var handler = new DeletingExotic();
        var area = realm.NewExotic(handler);
        realm.DefineValue(realm.Global, "area", area);

        // Identity: the value the host minted is the one the page sees.
        Assert.True(realm.GetProperty(realm.Global, "area") == area);

        realm.EvaluateHostScript("area.stored = 'written'; area.kept = 'too';", "test:replaced-write");
        Assert.Equal("true", Eval(realm, "String(delete area.stored)", "test:replaced-delete"));
        Assert.Equal("undefined", Eval(realm, "String(area.stored)", "test:replaced-gone"));
        Assert.Equal("too", Eval(realm, "area.kept", "test:replaced-kept"));
        Assert.Equal(["stored"], handler.Deletions);

        // The original Reflect.deleteProperty, kept by nobody, is not what completes this: the page's
        // replacement answering false must not have been consulted.
        Assert.Equal(0, realm.GetProperty(realm.Global, "pageCalls").AsNumber);
    }

    /// <summary>
    /// The named/index boundary at its edges: the largest index is not offered, the first
    /// non-index integer string and a negative one are, and each is offered once per deletion
    /// whichever route performs it.
    /// </summary>
    [Theory]
    [MemberData(nameof(EnginesDeclaring), JsCapabilities.ExoticObjects | JsCapabilities.HostScriptSource)]
    public void TheDeletionBoundaryHoldsAtTheArrayIndexLimitAndOnEveryRoute(string engine)
    {
        using var realm = NewRealm(engine);
        AssertHas(realm, JsCapabilities.ExoticObjects | JsCapabilities.HostScriptSource);

        var handler = new DeletingExotic();
        realm.DefineValue(realm.Global, "area", realm.NewExotic(handler));

        realm.EvaluateHostScript(
            "delete area['4294967294']; delete area['4294967295']; delete area['-1'];" +
            "Reflect.deleteProperty(area, 'viaReflect');" +
            "delete area[Symbol.iterator];",
            "test:boundary-delete");

        Assert.Equal(["4294967295", "-1", "viaReflect"], handler.Deletions);
    }
}
