using System.Text.Json;
using Broiler.JSeal;
using Broiler.JSeal.BroilerJs;
using Broiler.JSeal.Vm;

// An opt-in diagnostic executable, deliberately absent from the solution and normal test lane.
// Each provider runs in its own process so another engine cannot preload a missing dependency.
if (args.Length != 1 || args[0] is not ("broiler-js" or "broiler-vm" or "registry"))
{
    Console.Error.WriteLine("Usage: ProviderProbes <broiler-js|broiler-vm|registry>");
    return 2;
}

var count = 0;
if (args[0] == "registry")
{
    Observe("J10.default-after-removal", "broiler-vm", () =>
    {
        // Both changes are confined to this disposable diagnostic process.
        Environment.SetEnvironmentVariable(JsEngineRegistry.SelectionEnvironmentVariable, null);
        JsEngineRegistry.Reset();
        JsEngineRegistry.Register(new BroilerJsEngineProvider());
        JsEngineRegistry.Register(new VmEngineProvider());
        JsEngineRegistry.SetDefault("broiler-js");
        JsEngineRegistry.Unregister("broiler-js");
        try
        {
            return JsEngineRegistry.Default.Name;
        }
        catch (InvalidOperationException)
        {
            return $"default-unavailable;HasAny={JsEngineRegistry.HasAny}";
        }
    });
}
else
{
    IJsEngineProvider provider = args[0] == "broiler-js"
        ? new BroilerJsEngineProvider()
        : new VmEngineProvider();

    // Creation failure is infrastructure failure, not seven apparent reproduced defects.
    using (var readiness = provider.CreateRealm(JsRealmOptions.Default))
    {
        if (readiness.EvaluateClassicScript("6 * 7", "J00:readiness").AsNumber != 42)
            throw new InvalidOperationException("Provider readiness probe did not return 42.");
    }

    InRealm("J03.arguments", "7", realm =>
        Evaluate(realm, "(function(x){ return arguments[0]; })(7)"));

    InRealm("J04.eval-permissive-control", "42", EvalThroughHostCallback);
    InRealm("J04.eval-restricted", "SyntaxError", EvalThroughHostCallback,
        new JsRealmOptions { AllowGuestEval = false });

    InRealm("J05.callable-proxy", "Function;result=42", realm =>
    {
        var proxy = realm.EvaluateClassicScript("new Proxy(function(){return 42;},{})", "J00:proxy");
        return $"{proxy.Kind};result={realm.Invoke(proxy, JsValue.Undefined)}";
    });

    InRealm("J06.getter-exception", "JsEngineException;thrown=42", realm =>
    {
        var target = realm.EvaluateClassicScript("({get x(){throw 42;}})", "J00:getter");
        return GuestFailure(() => realm.GetProperty(target, "x").ToString());
    });

    InRealm("J07.coercion-exception", "JsEngineException;thrown=42", realm =>
    {
        var target = realm.EvaluateClassicScript("({toString(){throw 42;}})", "J00:coercion");
        return GuestFailure(() => realm.ToJsString(target));
    });

    InRealm("J08.undefined-precedence", "Undefined", realm =>
    {
        var target = realm.NewExotic(new NamedFallback());
        realm.DefineValue(target, "name", JsValue.Undefined);
        return realm.GetProperty(target, "name").Kind.ToString();
    });

    // Inventory only: these distinguish pinned engines from current source, not completeness.
    InRealm("inventory.Array.fromAsync", "function", realm => Evaluate(realm, "typeof Array.fromAsync"));
    InRealm("inventory.Object.groupBy", "function", realm => Evaluate(realm, "typeof Object.groupBy"));
    InRealm("inventory.Map.groupBy", "function", realm => Evaluate(realm, "typeof Map.groupBy"));

    void InRealm(string id, string expected, Func<IJsRealm, string> probe, JsRealmOptions? options = null)
    {
        // Keep creation outside Observe: inability to create the requested realm must fail the run.
        using var realm = provider.CreateRealm(options ?? JsRealmOptions.Default);
        Observe(id, expected, () => probe(realm));
    }
}

Console.WriteLine($"J00-DONE\t{count}");
return 0;

void Observe(string id, string expected, Func<string> probe)
{
    string actual;
    string? exceptionMessage = null;
    try
    {
        actual = probe();
    }
    catch (Exception error)
    {
        actual = "throw:" + error.GetType().FullName;
        exceptionMessage = error.Message;
    }

    Console.WriteLine("J00\t" + JsonSerializer.Serialize(new
    {
        id,
        expected,
        actual,
        outcome = actual == expected ? "matches-target" : "differs-from-target",
        exceptionMessage,
    }));
    count++;
}

static string Evaluate(IJsRealm realm, string source) =>
    realm.EvaluateClassicScript(source, "J00:probe").ToString();

static string EvalThroughHostCallback(IJsRealm realm)
{
    realm.EvaluateClassicScript("function page(){ return (0,eval)('42'); }", "J00:page");
    return realm.EvaluateHostScript(
        "var result; try { result = page(); } catch(e) { result = e.name; } result;",
        "J00:host").ToString();
}

static string GuestFailure(Func<string> operation)
{
    try
    {
        return "returned:" + operation();
    }
    catch (JsEngineException error)
    {
        return "JsEngineException;thrown=" + error.Thrown;
    }
    // Other exception types are recorded, without engine-specific references, by Observe.
}

sealed class NamedFallback : IJsExotic
{
    public uint IndexedLength => 0;

    // The fixture installs an ordinary 'name' property, so it must not repeat it here.
    public IReadOnlyList<string> SupportedNames => Array.Empty<string>();

    public bool TryGetNamed(string name, out JsValue value)
    {
        value = JsValue.String("named");
        return name == "name";
    }

    public bool TryGetIndex(uint index, out JsValue value)
    {
        value = JsValue.Missing;
        return false;
    }

    public bool TrySetNamed(string name, JsValue value) => false;
}
