using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Broiler.JSeal;
using Broiler.JSeal.Vm;
using Broiler.VM.Profile.JavaScript;

if (args.Length != 2)
    throw new ArgumentException("Usage: CallbackAllocations <phase> <output.json> (run from repository root)");

const int warmup = 128;
const int iterations = 512;
const int samples = 3;
var rows = new List<object>();
foreach (var arity in new[] { 0, 1, 8, 9, 32 })
{
    foreach (var mode in new[] { "adapter", "vm-host-roundtrip", "jseal-roundtrip" })
    {
        using var realm = new VmEngineProvider().CreateRealm(JsRealmOptions.Default);
        var rawArguments = Enumerable.Range(0, arity).Select(i => JsHostValue.Number(i)).ToArray();
        var arguments = Enumerable.Range(0, arity).Select(i => JsValue.Number(i)).ToArray();
        JsNativeFunction body = static (in JsCall call) => JsValue.Number(call.Length);
        var callback = realm.NewMethod("probe", body);

        // Diagnostic-only reflection obtains the real adapter and host realm once, outside the
        // measured loop. All callbacks below run inside an actual VM host turn.
        var type = realm.GetType();
        var host = (JsHostRealm)type.GetProperty("Host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(realm)!;
        var adapter = (JsHostFunction)type.GetMethod("Trampoline", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(realm, [body])!;
        var allocated = new long[samples];
        double checksum = 0;
        var driver = realm.NewMethod("driver", (in JsCall _) =>
        {
            var rawCallback = host.NewMethod("rawProbe", static (_, _, values) => JsHostValue.Number(values.Length));
            Func<double> invoke = mode switch
            {
                "adapter" => () => adapter(host, JsHostValue.Undefined, rawArguments).AsNumber(),
                "vm-host-roundtrip" => () => host.Invoke(rawCallback, JsHostValue.Undefined, rawArguments).AsNumber(),
                _ => () => realm.Invoke(callback, JsValue.Undefined, arguments).AsNumber,
            };
            for (var i = 0; i < warmup; i++) checksum += invoke();
            for (var sample = 0; sample < samples; sample++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < iterations; i++) checksum += invoke();
                allocated[sample] = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            return JsValue.Undefined;
        });
        realm.Invoke(driver, JsValue.Undefined);
        if (checksum != arity * (warmup + samples * iterations))
            throw new InvalidOperationException("A callback did not return its argument count.");
        rows.Add(new { arity, mode, allocatedBytes = allocated, bytesPerCall = allocated.Select(n => (double)n / iterations).ToArray() });
    }
}

var report = new
{
    phase = args[0],
    timestampUtc = DateTimeOffset.UtcNow,
    framework = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
    warmup, iterations, samples,
    jsValueSize = Unsafe.SizeOf<JsValue>(),
    jsHostValueSize = Unsafe.SizeOf<JsHostValue>(),
    providerSha256 = Hash(typeof(VmEngineProvider).Assembly.Location),
    adapterSourceSha256 = Hash("Broiler.JSeal.Vm/VmRealm.Values.cs"),
    vmAssembly = typeof(JsHostRealm).Assembly.GetName().FullName,
    vmAssemblySha256 = Hash(typeof(JsHostRealm).Assembly.Location),
    measurements = rows,
};
File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
Console.WriteLine($"Wrote {args[1]}");

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
