using System.Reflection;
using System.Text.Json;
using Broiler.JSeal;
#if CONSUMER_VM
using Broiler.JSeal.Vm;
#else
using Broiler.JSeal.BroilerJs;
#endif

// A fresh executable per provider, without test assemblies or engine-specific preload calls.
#if CONSUMER_VM
IJsEngineProvider provider = new VmEngineProvider();
string[] requiredAssemblies = ["Broiler.JSeal", "Broiler.JSeal.Vm", "Broiler.VM.Runtime",
    "Broiler.VM.Profile.JavaScript", "Broiler.VM.Profile.JavaScript.Compiler"];
const string otherProvider = "Broiler.JSeal.BroilerJs";
const string otherEnginePrefix = "Broiler.JavaScript.";
#else
IJsEngineProvider provider = new BroilerJsEngineProvider();
string[] requiredAssemblies = ["Broiler.JSeal", "Broiler.JSeal.BroilerJs", "Broiler.JavaScript.Runtime",
    "Broiler.JavaScript.BuiltIns", "Broiler.JavaScript.Globals", "Broiler.JavaScript.Modules",
    "Broiler.JavaScript.Storage"];
const string otherProvider = "Broiler.JSeal.Vm";
const string otherEnginePrefix = "Broiler.VM.";
#endif

var checks = new List<string>();
using (var realm = provider.CreateRealm(JsRealmOptions.Default))
{
    Require(realm.EngineName == provider.Name, "realm creation");
    Require(realm.EvaluateClassicScript("6 * 7", "consumer:number").AsNumber == 42, "primitive evaluation");
    // Parentheses keep this an expression, rather than a directive-prologue string.
    Require(realm.EvaluateClassicScript("('Grüße')", "consumer:string").AsString == "Grüße", "UTF-8 string");

    // Exercise arguments creation through the packaged provider, without preloading Modules.
    Require(realm.EvaluateClassicScript("(function(x){ return arguments[0]; })(7)",
        "consumer:arguments").AsNumber == 7, "ordinary arguments");
    Require(realm.EvaluateClassicScript("(function(){ return arguments.length; })()",
        "consumer:empty-arguments").AsNumber == 0, "zero-argument function");
    Require(realm.EvaluateClassicScript("(function(x){ 'use strict'; return arguments[0]; })(7)",
        "consumer:strict-arguments").AsNumber == 7, "strict arguments");
#if !CONSUMER_VM
    // VM's function-scope direct eval is a separate V13-V15 roadmap item.
    Require(realm.EvaluateClassicScript("(function(x){ return eval('arguments[0] + x'); })(7)",
        "consumer:eval-arguments").AsNumber == 14, "direct eval with arguments");
    Require((realm.Capabilities & (JsCapabilities.Modules | JsCapabilities.DynamicImport)) == 0,
        "module capabilities require host opt-in");
#endif

    var calls = 0;
    var method = realm.NewMethod("hostSum", (in JsCall call) =>
    {
        if (!ReferenceEquals(call.Realm, realm) || call.Length != 2)
            throw new InvalidOperationException("Incorrect callback realm or argument count.");
        calls++;
        return JsValue.Number(call[0].AsNumber + call[1].AsNumber);
    }, 2);
    realm.DefineValue(realm.Global, "hostSum", method);
    Require(realm.EvaluateClassicScript("hostSum(20, 22)", "consumer:callback").AsNumber == 42
        && calls == 1, "host callback");

    realm.EnqueueJob(() => throw new InvalidOperationException("Disposed realm ran a queued job."));
    realm.Dispose();
    ExpectDisposed(() => realm.DrainJobs());
    ExpectDisposed(() => realm.EnqueueJob(() => { }));
    realm.Dispose();
    checks.Add("disposal and idempotent teardown");
}

var directory = AppContext.BaseDirectory;
var shipped = Directory.GetFiles(directory, "*.dll").Select(Path.GetFileNameWithoutExtension).ToArray();
foreach (var name in requiredAssemblies)
    if (!shipped.Contains(name)) throw new InvalidOperationException($"Missing required assembly: {name}");
if (shipped.Any(name => name == otherProvider || name!.StartsWith(otherEnginePrefix, StringComparison.Ordinal)
        || name.Contains("xunit", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".Tests", StringComparison.Ordinal)))
    throw new InvalidOperationException("Consumer contains the other provider, its engine, or a test assembly.");

var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic
    && a.GetName().Name!.StartsWith("Broiler.", StringComparison.Ordinal)).OrderBy(a => a.FullName).ToArray();
foreach (var assembly in loaded)
    if (!Path.GetFullPath(assembly.Location).StartsWith(directory,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        throw new InvalidOperationException($"Assembly loaded outside the consumer: {assembly.FullName}");
checks.Add("package assembly isolation");

Console.WriteLine("J02\t" + JsonSerializer.Serialize(new
{
    provider = provider.Name,
    checks,
    requiredAssemblies,
    shippedAssemblies = shipped.Order(),
    loadedAssemblies = loaded.Select(a => new
    {
        name = a.GetName().Name,
        version = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        file = Path.GetFileName(a.Location)
    })
}));

void Require(bool condition, string check)
{
    if (!condition) throw new InvalidOperationException($"Failed: {check}");
    checks.Add(check);
}

static void ExpectDisposed(Action action)
{
    try { action(); }
    catch (ObjectDisposedException) { return; }
    throw new InvalidOperationException("Disposed realm accepted work.");
}
