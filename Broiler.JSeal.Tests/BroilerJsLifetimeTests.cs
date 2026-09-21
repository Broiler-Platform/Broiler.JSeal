using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JSeal.BroilerJs;

namespace Broiler.JSeal.Tests;

// Adoption is optional and only Broiler.JS exposes it; engine ownership belongs in this suite.
public class BroilerJsLifetimeTests
{
    [Fact]
    public void DisposingAnAdoptedRealmLeavesItsContextUsableAndRemovesItsPolicy()
    {
        using var context = new JSContext();
        var provider = new BroilerJsEngineProvider();
        Assert.True(provider.TryAdopt(context, new JsRealmOptions { AllowGuestEval = false }, out var adopted));
        using var realm = adopted!;
        var ran = 0;
        realm.EnqueueJob(() => ran++);
        realm.Dispose();
        realm.Dispose();

        Assert.Throws<ObjectDisposedException>(() => realm.EvaluateHostScript("42", "test:disposed-adoption"));
        Assert.Throws<ObjectDisposedException>(() => realm.DrainJobs());
        Assert.Equal(0, ran);
        Assert.Equal(42, context.Eval("eval('42')").DoubleValue);
    }

    [Fact]
    public void DisposingAPermissiveWrapperDoesNotRemoveAnotherWrappersPolicy()
    {
        using var context = new JSContext();
        var provider = new BroilerJsEngineProvider();
        Assert.True(provider.TryAdopt(context, new JsRealmOptions { AllowGuestEval = false }, out var restricted));
        using var owner = restricted!;
        Assert.True(provider.TryAdopt(context, JsRealmOptions.Default, out var permissive));
        using var wrapper = permissive!;
        wrapper.Dispose();

        Assert.Throws<JsEngineException>(() => owner.EvaluateClassicScript("eval('42')", "test:retained-policy"));
        owner.Dispose();
        Assert.Equal(42, context.Eval("eval('42')").DoubleValue);
    }

    [Fact]
    public void DisposingAnAdoptedRealmPreservesTheHostsPromiseQueue()
    {
        var pump = new HostPump();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            using var context = new JSContext(pump);
            var provider = new BroilerJsEngineProvider();
            Assert.True(provider.TryAdopt(context, JsRealmOptions.Default, out var adopted));
            using var realm = adopted!;
            realm.EvaluateClassicScript("var settled = 0; Promise.resolve().then(function () { settled = 42; });", "test:host-queue");
            Assert.False(realm.HasPendingJobs);
            Assert.NotEmpty(pump.Jobs);

            realm.Dispose();
            Assert.Same(pump, SynchronizationContext.Current);
            Assert.Equal(0, context.Eval("settled").DoubleValue);
            while (pump.Jobs.TryDequeue(out var job)) job();
            Assert.Equal(42, context.Eval("settled").DoubleValue);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class HostPump : SynchronizationContext, IJSJobPump
    {
        public Queue<Action> Jobs { get; } = new();
        public override void Post(SendOrPostCallback callback, object? state) => Jobs.Enqueue(() => callback(state));
    }
}
