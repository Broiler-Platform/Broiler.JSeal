using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Broiler.JSeal;
using Broiler.JSeal.Vm;
using Broiler.VM;
using Broiler.VM.Profile.JavaScript;

if (args.Length != 2)
    throw new ArgumentException("Usage: BufferTransfer <phase> <output.json> (run from repository root)");

const int samples = 3;
var rows = new List<object>();

// Sizes straddle the old implementation's 8000-byte chunk. Iterations shrink with size so that the
// old route stays inside one realm's lifetime fuel allowance.
foreach (var (size, iterations) in new[] { (0, 64), (1, 64), (64, 64), (8000, 16), (8001, 16), (65536, 8), (1048576, 1) })
{
    foreach (var operation in new[] { "read", "construct" })
    {
        foreach (var mode in new[] { "host-turn", "in-step" })
        {
            using var realm = new VmEngineProvider().CreateRealm(JsRealmOptions.Default);
            var bytes = new byte[size];
            for (var i = 0; i < size; i++)
                bytes[i] = (byte)(i * 31 % 256);

            var buffer = realm.NewArrayBuffer(bytes);

            // Diagnostic-only reflection: the realm's own runtime, whose budget snapshot is the one
            // public place consumption is readable. Read outside any step (the host-turn mode) the
            // figures include the turn invocation itself, which both implementations pay alike.
            var runtime = (VmRuntime)realm.GetType()
                .GetField("_runtime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(realm)!;
            var allocated = new long[samples];
            var hostCalls = new ulong[samples];
            var fuel = new ulong[samples];
            var checksum = 0L;

            void Once()
            {
                if (operation == "read")
                {
                    if (!realm.TryGetArrayBufferBytes(buffer, out var read) || read.Length != size ||
                        (size > 0 && read[size - 1] != bytes[size - 1]))
                    {
                        throw new InvalidOperationException("A read answered the wrong bytes.");
                    }

                    checksum += read.Length;
                }
                else
                {
                    checksum += realm.NewArrayBuffer(bytes).IsObject ? size : -1;
                }
            }

            void Measure()
            {
                Once();

                for (var sample = 0; sample < samples; sample++)
                {
                    var snapshot = runtime.GetBudgetSnapshot();
                    var before = GC.GetTotalAllocatedBytes(precise: true);

                    for (var i = 0; i < iterations; i++)
                        Once();

                    allocated[sample] = GC.GetTotalAllocatedBytes(precise: true) - before;
                    var after = runtime.GetBudgetSnapshot();
                    hostCalls[sample] = after.Consumed(VmBudgetDimension.HostCalls) - snapshot.Consumed(VmBudgetDimension.HostCalls);
                    fuel[sample] = after.Consumed(VmBudgetDimension.Fuel) - snapshot.Consumed(VmBudgetDimension.Fuel);
                }
            }

            if (mode == "host-turn")
            {
                Measure();
            }
            else
            {
                realm.Invoke(realm.NewMethod("driver", (in JsCall _) =>
                {
                    Measure();
                    return JsValue.Undefined;
                }), JsValue.Undefined);
            }

            if (checksum != (long)size * (1 + samples * iterations))
                throw new InvalidOperationException("An operation did not run.");

            rows.Add(new
            {
                size,
                operation,
                mode,
                iterations,
                allocatedBytes = allocated,
                bytesPerOperation = allocated.Select(n => (double)n / iterations).ToArray(),
                hostCallsPerOperation = hostCalls.Select(n => (double)n / iterations).ToArray(),
                fuelPerOperation = fuel.Select(n => (double)n / iterations).ToArray(),
            });
        }
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
    samples,
    providerSha256 = Hash(typeof(VmEngineProvider).Assembly.Location),
    valuesSourceSha256 = Hash("Broiler.JSeal.Vm/VmRealm.Values.cs"),
    vmAssembly = typeof(JsHostRealm).Assembly.GetName().FullName,
    vmInformationalVersion = typeof(JsHostRealm).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    vmAssemblySha256 = Hash(typeof(JsHostRealm).Assembly.Location),
    measurements = rows,
};
File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
Console.WriteLine($"Wrote {args[1]}");

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
