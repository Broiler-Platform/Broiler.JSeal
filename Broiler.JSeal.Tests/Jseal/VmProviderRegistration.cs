using System.Runtime.CompilerServices;
using Broiler.JSeal.BroilerJs;
using Broiler.JSeal.Vm;

namespace Broiler.JSeal.Tests;

/// <summary>
/// Names the Broiler.VM JSEAL provider so this build's conformance run includes it.
/// </summary>
internal static class VmProviderRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        BroilerJsEngineProvider.Register();
        VmEngineProvider.Register();
    }
}

