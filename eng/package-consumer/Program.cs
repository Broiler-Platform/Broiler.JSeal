using System.Reflection;
using System.Text.Json;
using Broiler.JSeal;
#if CONSUMER_VM || CONSUMER_BOTH
using Broiler.JSeal.Vm;
#endif
#if !CONSUMER_VM
using Broiler.JSeal.BroilerJs;
#endif

// A fresh executable per provider, without test assemblies or engine-specific preload calls.
// J19's combined consumer runs both providers, and both engine families, in one process.
string[] jsAssemblies = ["Broiler.JSeal.BroilerJs", "Broiler.JavaScript.Runtime", "Broiler.JavaScript.BuiltIns",
    "Broiler.JavaScript.Globals", "Broiler.JavaScript.Modules", "Broiler.JavaScript.Storage"];
string[] vmAssemblies = ["Broiler.JSeal.Vm", "Broiler.VM.Runtime", "Broiler.VM.Profile.JavaScript",
    "Broiler.VM.Profile.JavaScript.Compiler"];
#if CONSUMER_BOTH
IJsEngineProvider[] providers = [new BroilerJsEngineProvider(), new VmEngineProvider()];
string[] requiredAssemblies = ["Broiler.JSeal", .. jsAssemblies, .. vmAssemblies];
string[] excludedPrefixes = [];
#elif CONSUMER_VM
IJsEngineProvider[] providers = [new VmEngineProvider()];
string[] requiredAssemblies = ["Broiler.JSeal", .. vmAssemblies];
string[] excludedPrefixes = ["Broiler.JSeal.BroilerJs", "Broiler.JavaScript."];
#else
IJsEngineProvider[] providers = [new BroilerJsEngineProvider()];
string[] requiredAssemblies = ["Broiler.JSeal", .. jsAssemblies];
string[] excludedPrefixes = ["Broiler.JSeal.Vm", "Broiler.VM."];
#endif

var checks = new List<string>();
foreach (var provider in providers)
    Exercise(provider);

#if CONSUMER_BOTH
// Both providers registered and live at once: realms of each engine interleave in one process.
foreach (var provider in providers)
    JsEngineRegistry.Register(provider);
using (var jsRealm = JsEngineRegistry.Find("broiler-js")!.CreateRealm(JsRealmOptions.Default))
using (var vmRealm = JsEngineRegistry.Find("broiler-vm")!.CreateRealm(JsRealmOptions.Default))
{
    jsRealm.EvaluateClassicScript("var shared = 20;", "consumer:both-js");
    vmRealm.EvaluateClassicScript("var shared = 22;", "consumer:both-vm");
    Require(jsRealm.EvaluateClassicScript("shared", "consumer:both-js-read").AsNumber == 20
        && vmRealm.EvaluateClassicScript("shared", "consumer:both-vm-read").AsNumber == 22
        && jsRealm.EngineName != vmRealm.EngineName, "both providers: concurrent isolated realms");
}
#endif

var directory = AppContext.BaseDirectory;
var shipped = Directory.GetFiles(directory, "*.dll").Select(Path.GetFileNameWithoutExtension).ToArray();
foreach (var name in requiredAssemblies)
    if (!shipped.Contains(name)) throw new InvalidOperationException($"Missing required assembly: {name}");
if (shipped.Any(name => excludedPrefixes.Any(prefix => name!.StartsWith(prefix, StringComparison.Ordinal))
        || name!.Contains("xunit", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".Tests", StringComparison.Ordinal)))
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
    provider = string.Join("+", providers.Select(p => p.Name)),
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

void Exercise(IJsEngineProvider provider)
{
    using var realm = provider.CreateRealm(JsRealmOptions.Default);
    var name = provider.Name;
    Require(realm.EngineName == provider.Name, $"{name}: realm creation");
    Require(realm.EvaluateClassicScript("6 * 7", "consumer:number").AsNumber == 42, $"{name}: primitive evaluation");
    // Parentheses keep this an expression, rather than a directive-prologue string.
    Require(realm.EvaluateClassicScript("('Grüße')", "consumer:string").AsString == "Grüße", $"{name}: UTF-8 string");

    // Exercise arguments creation through the packaged provider, without preloading Modules.
    Require(realm.EvaluateClassicScript("(function(x){ return arguments[0]; })(7)",
        "consumer:arguments").AsNumber == 7, $"{name}: ordinary arguments");
    Require(realm.EvaluateClassicScript("(function(){ return arguments.length; })()",
        "consumer:empty-arguments").AsNumber == 0, $"{name}: zero-argument function");
    Require(realm.EvaluateClassicScript("(function(x){ 'use strict'; return arguments[0]; })(7)",
        "consumer:strict-arguments").AsNumber == 7, $"{name}: strict arguments");
#if !CONSUMER_VM
    if (provider is BroilerJsEngineProvider)
    {
        // VM's function-scope direct eval is a separate V13-V15 roadmap item.
        Require(realm.EvaluateClassicScript("(function(x){ return eval('arguments[0] + x'); })(7)",
            "consumer:eval-arguments").AsNumber == 14, $"{name}: direct eval with arguments");
        Require((realm.Capabilities & (JsCapabilities.Modules | JsCapabilities.DynamicImport)) == 0,
            $"{name}: module capabilities require host opt-in");
    }
#endif

    // I10: the module contract ships in the contracts package, but no packaged realm implements it
    // and no provider declares Modules or DynamicImport before I13.
    Require(typeof(IJsModules).IsInterface && typeof(IJsModuleMap).IsPublic && typeof(JsModuleException).IsPublic
        && realm is not IJsModules
        && (provider.Capabilities & (JsCapabilities.Modules | JsCapabilities.DynamicImport)) == 0,
        $"{name}: module contract types present, no module realm before I13");

    // I18: structured clone through the package alone - same-realm where StructuredClone is
    // declared, a second realm on a second thread where WorkerRealms is, and the contract's explicit
    // refusal where either is not.
    var original = realm.EvaluateClassicScript("({ n: 1, list: [2] })", "consumer:clone-source");
    if (realm.Capabilities.HasFlag(JsCapabilities.StructuredClone))
    {
        var copy = realm.Clone(original);
        var buffer = realm.EvaluateClassicScript("new ArrayBuffer(4)", "consumer:clone-buffer");
        var moved = realm.Clone(buffer, [buffer]);
        Require(copy != original && realm.GetProperty(copy, "n").AsNumber == 1
            && realm.ClassifyTransferable(buffer) == JsTransferKind.Detached
            && realm.TryGetArrayBufferBytes(moved, out var movedBytes) && movedBytes.Length == 4,
            $"{name}: same-realm structured clone and transfer");
    }
    else
    {
        ExpectUnavailable(() => realm.Clone(original), JsCapabilities.StructuredClone);
        checks.Add($"{name}: same-realm structured clone refused explicitly");
    }

    if (realm.Capabilities.HasFlag(JsCapabilities.WorkerRealms))
    {
        var carrier = realm.Detach(original);
        var received = double.NaN;
        var worker = new Thread(() =>
        {
            using var second = provider.CreateRealm(JsRealmOptions.Default);
            received = second.GetProperty(second.Adopt(carrier), "n").AsNumber;
        });
        worker.Start();
        Require(worker.Join(TimeSpan.FromSeconds(30)) && received == 1, $"{name}: second-thread structured clone transport");
    }
    else
    {
        ExpectUnavailable(() => realm.Detach(original), JsCapabilities.WorkerRealms);
        checks.Add($"{name}: second-thread transport refused explicitly");
    }

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
        && calls == 1, $"{name}: host callback");

    realm.EnqueueJob(() => throw new InvalidOperationException("Disposed realm ran a queued job."));
    realm.Dispose();
    ExpectDisposed(() => realm.DrainJobs());
    ExpectDisposed(() => realm.EnqueueJob(() => { }));
    realm.Dispose();
    checks.Add($"{name}: disposal and idempotent teardown");
}

void Require(bool condition, string check)
{
    if (!condition) throw new InvalidOperationException($"Failed: {check}");
    checks.Add(check);
}

static void ExpectUnavailable(Action action, JsCapabilities missing)
{
    try { action(); }
    catch (JsCapabilityUnavailableException refusal) when (refusal.Missing == missing) { return; }
    throw new InvalidOperationException($"An undeclared {missing} was not refused explicitly.");
}

static void ExpectDisposed(Action action)
{
    try { action(); }
    catch (ObjectDisposedException) { return; }
    throw new InvalidOperationException("Disposed realm accepted work.");
}
