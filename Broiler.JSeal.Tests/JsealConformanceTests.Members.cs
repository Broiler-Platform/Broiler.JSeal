using System.Numerics;
using System.Reflection;

using Broiler.JSeal;

namespace Broiler.JSeal.Tests;

/// <summary>
/// Properties, host functions, constructors, and the job queue and promises.
/// </summary>
public partial class JsealConformanceTests
{
    // â”€â”€ members â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [MemberData(nameof(Engines))]
    public void DefineValueAndGetPropertyRoundTrip(string engine)
    {
        using var realm = NewRealm(engine);

        var target = realm.NewObject();
        realm.DefineValue(target, "answer", JsValue.Number(42d));
        realm.DefineValue(target, "label", JsValue.String("forty-two"));

        Assert.True(realm.GetProperty(target, "answer") == JsValue.Number(42d));
        Assert.True(realm.GetProperty(target, "label") == JsValue.String("forty-two"));
        Assert.True(realm.HasProperty(target, "answer"));

        // A property that was never installed reads as undefined, not as Missing: Missing means the
        // host supplied no value, and a read always supplies one.
        Assert.True(realm.GetProperty(target, "absent").IsUndefined);
        Assert.False(realm.HasProperty(target, "absent"));

        Assert.True(realm.DeleteProperty(target, "answer"));
        Assert.False(realm.HasProperty(target, "answer"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void AnAccessorsGetterAndSetterBothRun(string engine)
    {
        using var realm = NewRealm(engine);

        var stored = "initial";
        JsValue Get(in JsCall call) => JsValue.String(stored);
        JsValue Set(in JsCall call)
        {
            stored = call.Realm.ToJsString(call[0]);
            return JsValue.Undefined;
        }

        var target = realm.NewObject();
        realm.DefineAccessor(target, "value", Get, Set);

        Assert.True(realm.GetProperty(target, "value") == JsValue.String("initial"));

        realm.SetProperty(target, "value", JsValue.String("from-host"));
        Assert.Equal("from-host", stored);
        Assert.True(realm.GetProperty(target, "value") == JsValue.String("from-host"));

        // And from script, which is the direction a page uses.
        realm.DefineValue(realm.Global, "accessorProbe", target);
        Assert.Equal("from-script", Eval(realm, "(accessorProbe.value = 'from-script', accessorProbe.value)"));
        Assert.Equal("from-script", stored);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void AnAccessorWithNoSetterIgnoresAnAssignmentRatherThanThrowing(string engine)
    {
        using var realm = NewRealm(engine);

        // 216 read-only IDL attributes are spelled as a null setter, so what an assignment to one
        // does is a contract outcome and not an implementation detail. Sloppy-mode assignment to an
        // accessor with no setter is a silent no-op in the language, and this is what the provider
        // does; a strict-mode assignment is a TypeError, which the second half pins.
        var target = realm.NewObject();
        realm.DefineAccessor(target, "readOnly", static (in JsCall _) => JsValue.String("fixed"), setter: null);
        realm.DefineValue(realm.Global, "readOnlyProbe", target);

        realm.SetProperty(target, "readOnly", JsValue.String("ignored"));
        Assert.True(realm.GetProperty(target, "readOnly") == JsValue.String("fixed"));

        Assert.Equal("fixed", Eval(realm, "(readOnlyProbe.readOnly = 'ignored', readOnlyProbe.readOnly)"));

        Assert.Equal(
            "TypeError",
            Eval(
                realm,
                """
                (function () {
                  'use strict';
                  try { readOnlyProbe.readOnly = 'ignored'; return 'no-throw'; }
                  catch (e) { return e.constructor.name; }
                })()
                """,
                "test:readonly-strict"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void OwnPropertyNamesAnswersWhatWasInstalledInInstallationOrder(string engine)
    {
        using var realm = NewRealm(engine);

        var target = realm.NewObject();
        realm.DefineValue(target, "gamma", JsValue.Number(3d));
        realm.DefineValue(target, "alpha", JsValue.Number(1d));
        realm.DefineAccessor(target, "beta", static (in JsCall _) => JsValue.Number(2d), setter: null);

        // Creation order, not sorted order â€” the bridge's nested-browsing-context sweep diffs this
        // list across an evaluation and a reordering would make the diff report members that never
        // moved.
        Assert.Equal(new[] { "gamma", "alpha", "beta" }, realm.OwnPropertyNames(target));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void DefaultFlagsAreEnumerableAndNonEnumerableIsNot(string engine)
    {
        using var realm = NewRealm(engine);

        var target = realm.NewObject();
        realm.DefineValue(target, "visible", JsValue.Number(1d));
        realm.DefineValue(target, "hidden", JsValue.Number(2d), JsPropertyFlags.NonEnumerable);
        realm.DefineAccessor(target, "shown", static (in JsCall _) => JsValue.Number(3d), setter: null);
        realm.DefineAccessor(target, "unshown", static (in JsCall _) => JsValue.Number(4d), setter: null, JsPropertyFlags.NonEnumerable);
        realm.DefineValue(realm.Global, "flagsProbe", target);

        // Proved from JavaScript, because enumerability is a question the page asks and a host-side
        // answer would only be re-reading whatever the provider chose to record.
        Assert.Equal("visible,shown", Eval(realm, "Object.keys(flagsProbe).join(',')", "test:keys"));

        // The non-enumerable members are there; they just do not enumerate.
        Assert.Equal("2 4", Eval(realm, "flagsProbe.hidden + ' ' + flagsProbe.unshown", "test:hidden"));
        Assert.Equal(new[] { "visible", "shown" }, realm.OwnPropertyNames(target));

        // Configurable, which both flag sets carry: a page may delete or redefine a DOM member.
        Assert.Equal("true false", Eval(realm, "delete flagsProbe.hidden, (('visible' in flagsProbe) + ' ' + ('hidden' in flagsProbe))", "test:delete"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void AnIndexedPropertyIsFoundByAnArrayGenericAndNotOnlyByAnIndexRead(string engine)
    {
        using var realm = NewRealm(engine);

        var target = realm.NewObject();
        realm.DefineIndex(target, 0, JsValue.String("zero"));
        realm.DefineIndex(target, 1, JsValue.String("one"));
        realm.DefineValue(target, "length", JsValue.Number(2d), JsPropertyFlags.NonEnumerable);
        realm.DefineValue(realm.Global, "indexProbe", target);

        Assert.True(realm.GetIndex(target, 1) == JsValue.String("one"));

        // An array generic asks whether index i is PRESENT before reading it, which an index
        // installed under the string key "0" would answer no to â€” the defect that made a live
        // collection produce a hole per element under Array.prototype.map.call.
        Assert.Equal("zero|one", Eval(realm, "Array.prototype.join.call(indexProbe, '|')", "test:generic"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void SetPrototypeLinksAnObjectToAnInterface(string engine)
    {
        using var realm = NewRealm(engine);

        var prototype = realm.NewObject();
        realm.DefineValue(prototype, "inherited", JsValue.String("from-prototype"));

        var instance = realm.NewObject();
        realm.SetPrototype(instance, prototype);

        // GetProperty follows the chain, which is what makes a wrapper's interface members reachable.
        Assert.True(realm.GetProperty(instance, "inherited") == JsValue.String("from-prototype"));
        Assert.True(realm.GetPrototype(instance) == prototype);

        // â€¦and the chain is not the object's own names.
        Assert.DoesNotContain("inherited", realm.OwnPropertyNames(instance));

        realm.DefineValue(realm.Global, "protoProbe", instance);
        Assert.Equal("true", Eval(realm, "String(Object.getPrototypeOf(protoProbe) === Object.getPrototypeOf(protoProbe))", "test:proto"));
    }

    // â”€â”€ calls â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [MemberData(nameof(Engines))]
    public void AHostFunctionSeesItsArgumentsItsReceiverAndMissingPastTheEnd(string engine)
    {
        using var realm = NewRealm(engine);

        var seen = "not-called";
        JsValue Record(in JsCall call)
        {
            seen = string.Join(
                '|',
                call.Length.ToString(),
                call[0].AsString ?? "?",
                call[1].IsMissing ? "missing" : call[1].Kind.ToString(),
                call.Realm.ToJsString(call.Realm.GetProperty(call.This, "tag")),
                call.NewTarget.IsMissing ? "no-new-target" : "new-target");

            return JsValue.String("returned");
        }

        realm.DefineValue(realm.Global, "record", realm.NewMethod("record", Record, 2));

        Assert.Equal("returned", Eval(realm, "record.call({ tag: 'receiver' }, 'first')", "test:args"));
        Assert.Equal("1|first|missing|receiver|no-new-target", seen);

        // An argument explicitly passed as undefined is NOT missing, which is the distinction the
        // bridge's arity-sensitive operations turn on.
        Assert.Equal("returned", Eval(realm, "record.call({ tag: 'receiver' }, 'first', undefined)", "test:args-undefined"));
        Assert.Equal("2|first|Undefined|receiver|no-new-target", seen);

        // The declared name and length reach the page, because WebIDL says an operation has both.
        Assert.Equal("record 2", Eval(realm, "record.name + ' ' + record.length", "test:name-length"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void InvokeAndConstructRunAScriptFunctionFromTheHost(string engine)
    {
        using var realm = NewRealm(engine);

        var add = realm.EvaluateHostScript("(function (a, b) { return a + b; })", "test:add");
        Assert.True(add.IsFunction);
        Assert.True(realm.Invoke(add, JsValue.Undefined, [JsValue.Number(20d), JsValue.Number(22d)]) == JsValue.Number(42d));

        var receiver = realm.NewObject();
        realm.DefineValue(receiver, "tag", JsValue.String("mine"));
        var readTag = realm.EvaluateHostScript("(function () { return this.tag; })", "test:this");
        Assert.True(realm.Invoke(readTag, receiver) == JsValue.String("mine"));

        var point = realm.EvaluateHostScript("(function Point(x) { this.x = x; })", "test:ctor");
        var instance = realm.Construct(point, [JsValue.Number(7d)]);
        Assert.True(instance.IsObject);
        Assert.True(realm.GetProperty(instance, "x") == JsValue.Number(7d));
        Assert.True(realm.GetPrototype(instance) == realm.GetProperty(point, "prototype"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void AHostFunctionThatThrowsIsCatchableFromScriptAsTheErrorRealmErrorNamed(string engine)
    {
        using var realm = NewRealm(engine);

        realm.DefineValue(
            realm.Global,
            "boom",
            realm.NewMethod("boom", static (in JsCall call) => throw call.Realm.Error(JsErrorKind.TypeError, "no good")));

        Assert.Equal(
            "TypeError|no good|true",
            Eval(
                realm,
                """
                (function () {
                  try { boom(); return 'no-throw'; }
                  catch (e) { return e.constructor.name + '|' + e.message + '|' + (e instanceof TypeError); }
                })()
                """,
                "test:throw"));

        // Every kind reaches its own constructor, so a host that raises a RangeError does not get a
        // page branching on `instanceof TypeError` by accident.
        realm.DefineValue(
            realm.Global,
            "outOfRange",
            realm.NewMethod("outOfRange", static (in JsCall call) => throw call.Realm.Error(JsErrorKind.RangeError, "too big")));

        Assert.Equal(
            "RangeError|too big",
            Eval(
                realm,
                "(function () { try { outOfRange(); return 'no-throw'; } catch (e) { return e.constructor.name + '|' + e.message; } })()",
                "test:throw-range"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void AScriptThrowReachesAHostCallerAsAJsEngineExceptionCarryingTheThrownValue(string engine)
    {
        using var realm = NewRealm(engine);

        var thrower = realm.EvaluateHostScript("(function () { throw { code: 5 }; })", "test:thrower");

        // The value the page threw travels, not just its message: `throw 42` and `throw {code: 5}`
        // are both legal and a host that reported only a message would lose what the page said.
        var failure = Assert.Throws<JsEngineException>(() => realm.Invoke(thrower, JsValue.Undefined));
        Assert.True(failure.Thrown.IsObject);
        Assert.True(realm.GetProperty(failure.Thrown, "code") == JsValue.Number(5d));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void DomErrorIsConstructedThroughTheRealmsDomExceptionSoItsNameReachesThePage(string engine)
    {
        using var realm = NewRealm(engine);

        // A bare realm has no DOM globals â€” building them is the bridge's job, not the provider's â€”
        // so the constructor the contract names is installed here first. That is also the assertion:
        // DomError must go through the realm's own DOMException rather than mint an error of its
        // own, because a page branches on `name` and on `instanceof DOMException`.
        realm.EvaluateHostScript(
            "function DOMException(message, name) { this.message = message; this.name = name; }",
            "test:domexception");

        realm.DefineValue(
            realm.Global,
            "notFound",
            realm.NewMethod("notFound", static (in JsCall call) => throw call.Realm.DomError("NotFoundError", "no such node")));

        Assert.Equal(
            "NotFoundError|no such node|true",
            Eval(
                realm,
                """
                (function () {
                  try { notFound(); return 'no-throw'; }
                  catch (e) { return e.name + '|' + e.message + '|' + (e instanceof DOMException); }
                })()
                """,
                "test:domerror"));
    }

    // â”€â”€ method versus constructor â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [MemberData(nameof(Engines))]
    public void NewMethodIsNotConstructableAndHasNoPrototype(string engine)
    {
        using var realm = NewRealm(engine);

        realm.DefineValue(realm.Global, "operation", realm.NewMethod("operation", static (in JsCall _) => JsValue.String("called")));

        // WebIDL: only an interface object is a constructor. `el.setAttribute.prototype` is
        // undefined in a browser and `new el.setAttribute()` is a TypeError â€” and under this engine
        // the two are the same fact, because IsConstructor tests for the prototype object. It is
        // also the allocation DomFunction was introduced to stop: a prototype object plus its
        // constructor back-reference per member, on wrappers with ~149 members each.
        Assert.Equal("undefined", Eval(realm, "typeof operation.prototype", "test:method-prototype"));
        Assert.True(realm.GetProperty(realm.GetProperty(realm.Global, "operation"), "prototype").IsUndefined);

        Assert.Equal(
            "TypeError",
            Eval(
                realm,
                "(function () { try { new operation(); return 'constructed'; } catch (e) { return e.constructor.name; } })()",
                "test:method-new"));

        // It is still perfectly callable, which is the only thing an operation has to be.
        Assert.Equal("called", Eval(realm, "operation()", "test:method-call"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void NewConstructorIsConstructableAndCarriesTheInterfacePrototype(string engine)
    {
        using var realm = NewRealm(engine);

        var made = 0;
        JsValue Body(in JsCall call)
        {
            made++;
            call.Realm.SetProperty(call.This, "size", call[0].IsMissing ? JsValue.Number(0d) : call[0]);
            return JsValue.Undefined;
        }

        var constructor = realm.NewConstructor("Widget", Body, 1);
        var prototype = realm.GetProperty(constructor, "prototype");
        Assert.True(prototype.IsObject);

        // An interface's members are installed on that prototype, and an instance inherits them.
        realm.DefineValue(prototype, "kind", JsValue.String("widget"));
        realm.DefineValue(realm.Global, "Widget", constructor);

        var instance = realm.Construct(constructor, [JsValue.Number(3d)]);
        Assert.Equal(1, made);
        Assert.True(realm.GetProperty(instance, "size") == JsValue.Number(3d));
        Assert.True(realm.GetProperty(instance, "kind") == JsValue.String("widget"));

        Assert.Equal("widget|4|true", Eval(realm, "(function () { var w = new Widget(4); return w.kind + '|' + w.size + '|' + (w instanceof Widget); })()", "test:construct"));
        Assert.Equal(2, made);

        // Construct performs a real [[Construct]] and not a call with a fresh receiver, which is
        // visible to a scripted constructor as its own new.target.
        var scripted = realm.EvaluateHostScript(
            "(function Scripted() { this.seen = String(new.target && new.target.name); })",
            "test:new-target-script");

        Assert.True(realm.GetProperty(realm.Construct(scripted), "seen") == JsValue.String("Scripted"));
    }

    /// <summary>
    /// <c>new.target</c> inside a host constructor's own body.
    /// </summary>
    /// <remarks>
    /// <b>This test found a defect in the Broiler.JS provider, and the defect is fixed â€” the
    /// remark below is kept because it is the only record of what was wrong.</b>
    /// <c>JsCall.NewTarget</c> promises the construct target for a construct call, and
    /// <c>BroilerJsRealm.Dispatch</c> read it from <c>JSEngine.NewTarget</c> alone â€” which resolves
    /// <c>Frames.CurrentNewTarget</c>, the interpreter's frame stack. A native function's body is
    /// invoked as a delegate and pushes no such frame, so the read was null and every host
    /// constructor saw <see cref="JsValue.Missing"/>. The value was there to be had: the engine's
    /// own [[Construct]] sets <c>ec.CurrentNewTarget</c> to the constructor immediately before
    /// invoking the delegate, and its <c>Object</c> factory reads exactly that.
    /// <c>BroilerJsRealm.cs:339-352</c> now reads both, in that order, and states why. Custom-element
    /// construction is the caller that needed it, and was smuggling new.target through as argument
    /// zero from a JavaScript shim for want of it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void NewTargetIsTheConstructorInsideAHostConstructBody(string engine)
    {
        using var realm = NewRealm(engine);

        var target = "unset";
        realm.DefineValue(
            realm.Global,
            "Probe",
            realm.NewConstructor("Probe", (in JsCall call) =>
            {
                target = call.NewTarget.IsMissing
                    ? "missing"
                    : call.Realm.ToJsString(call.Realm.GetProperty(call.NewTarget, "name"));

                return JsValue.Undefined;
            }));

        realm.EvaluateHostScript("new Probe()", "test:new-target");
        Assert.Equal("Probe", target);
    }

    // â”€â”€ jobs â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [MemberData(nameof(Engines))]
    public void DrainJobsRunsWhatWasQueuedAndReportsHowMany(string engine)
    {
        using var realm = NewRealm(engine);

        var order = new List<string>();
        Assert.False(realm.HasPendingJobs);

        realm.EnqueueJob(() => order.Add("first"));
        realm.EnqueueJob(() => order.Add("second"));
        Assert.True(realm.HasPendingJobs);

        // Nothing has run yet: the host decides when a microtask checkpoint happens, which is the
        // whole reason this contract is pull-shaped.
        Assert.Empty(order);

        Assert.Equal(2, realm.DrainJobs());
        Assert.Equal(new[] { "first", "second" }, order);
        Assert.False(realm.HasPendingJobs);
        Assert.Equal(0, realm.DrainJobs());
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void AJobQueuedByAJobIsFollowedWithinTheSameDrain(string engine)
    {
        using var realm = NewRealm(engine);

        var order = new List<string>();
        realm.EnqueueJob(() =>
        {
            order.Add("outer");
            realm.EnqueueJob(() => order.Add("inner"));
        });

        Assert.Equal(2, realm.DrainJobs());
        Assert.Equal(new[] { "outer", "inner" }, order);

        // â€¦and the limit is what keeps a chain that re-queues itself from holding the checkpoint
        // forever. It is a bound on jobs run, not a bound on depth.
        var ran = 0;
        void Requeue()
        {
            ran++;
            realm.EnqueueJob(Requeue);
        }

        realm.EnqueueJob(Requeue);
        Assert.Equal(3, realm.DrainJobs(3));
        Assert.Equal(3, ran);
        Assert.True(realm.HasPendingJobs);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void APromiseSettlesFromTheHostAndItsReactionRunsAtTheNextDrain(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.Promises))
        {
            // An engine that cannot hand a pending promise to the host has no deferred result for
            // fetch or whenDefined to return, and must say so rather than hand back something that
            // never settles.
            Assert.Throws<JsCapabilityUnavailableException>(() => realm.NewPromise(out _, out _));
            return;
        }

        var promise = realm.NewPromise(out var resolve, out _);
        realm.DefineValue(realm.Global, "pending", promise);
        realm.EvaluateHostScript("var seen = 'none'; pending.then(function (v) { seen = 'got:' + v; });", "test:then");

        Assert.Equal("none", Eval(realm, "seen", "test:then-before"));

        resolve(JsValue.String("value"));

        // A reaction is a job, not a callback: it must not have run at the moment the promise was
        // settled, and it must run at the next checkpoint the host takes.
        Assert.Equal("none", Eval(realm, "seen", "test:then-settled"));
        Assert.True(realm.DrainJobs() > 0);
        Assert.Equal("got:value", Eval(realm, "seen", "test:then-after"));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ARejectedPromiseReachesItsRejectionHandler(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.Promises))
        {
            Assert.Throws<JsCapabilityUnavailableException>(() => realm.NewPromise(out _, out _));
            return;
        }

        var promise = realm.NewPromise(out _, out var reject);
        realm.DefineValue(realm.Global, "failing", promise);
        realm.EvaluateHostScript("var failure = 'none'; failing.then(null, function (e) { failure = 'caught:' + e; });", "test:catch");

        reject(JsValue.String("nope"));
        realm.DrainJobs();

        Assert.Equal("caught:nope", Eval(realm, "failure", "test:catch-after"));
    }

    /// <summary>
    /// A page cannot make the host build its deferred results out of a constructor the page wrote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>Promise</c> is a writable global, which is the language's rule and not an engine's
    /// choice.</b> So <c>globalThis.Promise = MyThing</c> is a thing a page may legally do, and a
    /// provider that read the global at the moment the bridge asked for a promise would hand that
    /// page every <c>fetch</c> result, every <c>whenDefined</c> and every stream the bridge is
    /// about to resolve â€” with the page's own code deciding what happens to each.
    /// </para>
    /// <para>
    /// The fix is to capture the intrinsic before any page script runs, and this is the assertion
    /// that the capture happened. It is a theory because it is a claim about every provider.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void APageThatReplacesPromiseDoesNotCaptureTheHostsPromises(string engine)
    {
        using var realm = NewRealm(engine);

        if (Lacks(realm, JsCapabilities.Promises))
            return;

        // The page replaces the global, exactly as it is entitled to.
        realm.EvaluateHostScript(
            "var hijacked = false;" +
            "globalThis.Promise = function (executor) { hijacked = true; this.then = function () {}; };",
            "test:hijack");

        var promise = realm.NewPromise(out var resolve, out _);
        realm.DefineValue(realm.Global, "deferred", promise);
        realm.EvaluateHostScript(
            "var reached = 'none'; deferred.then(function (v) { reached = 'got:' + v; });",
            "test:hijack-then");

        resolve(JsValue.String("value"));
        realm.DrainJobs();

        Assert.Equal("false", Eval(realm, "String(hijacked)", "test:hijack-flag"));
        Assert.Equal("got:value", Eval(realm, "reached", "test:hijack-after"));
    }

    /// <summary>
    /// A promise does not depend on the capability a page's Content-Security-Policy takes away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the case that decides whether a provider's promise is real or is a snippet.</b>
    /// A page whose policy forbids evaluation is the page most likely to reach for <c>fetch</c>,
    /// and a provider that built its promises by evaluating source would hand that page a promise
    /// assembled out of the one thing it had just refused â€” or refuse the <c>fetch</c>, which is
    /// worse, because the policy said nothing about network access.
    /// </para>
    /// <para>
    /// The Broiler.VM provider's <c>NewPromise</c> refused for exactly this reason until the
    /// argument was found to be about a route rather than about the engine. So the two claims are
    /// asserted together here: dynamic source is still refused, and a promise is still made.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Engines))]
    public void APromiseIsStillAvailableInARealmThatForbidsGuestEvaluation(string engine)
    {
        using var realm = NewRealm(engine, new JsRealmOptions { AllowGuestEval = false });

        if (Lacks(realm, JsCapabilities.Promises))
            return;

        Assert.False(realm.Capabilities.HasFlag(JsCapabilities.GuestEval));
        Assert.Throws<JsCapabilityUnavailableException>(
            () => realm.EvaluateDynamicSource("1", "test:dynamic-refused"));

        var promise = realm.NewPromise(out var resolve, out _);
        realm.DefineValue(realm.Global, "restricted", promise);
        realm.EvaluateHostScript(
            "var got = 'none'; restricted.then(function (v) { got = 'got:' + v; });",
            "test:restricted-then");

        resolve(JsValue.String("value"));
        realm.DrainJobs();

        Assert.Equal("got:value", Eval(realm, "got", "test:restricted-after"));
    }
}

