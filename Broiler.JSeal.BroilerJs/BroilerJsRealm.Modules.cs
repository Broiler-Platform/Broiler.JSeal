using System.Runtime.ExceptionServices;

using Broiler.JavaScript.Ast.Misc;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Engine.Core;
using Broiler.JavaScript.Modules;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.JSeal.BroilerJs;

/// <summary>
/// The module-contract half of a realm: the engine's module context, the one map a realm may open,
/// and the turn discipline that keeps module work on the realm's own job queue.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reached only through <see cref="BroilerJsEngineProvider.EnableModuleContract"/> until I13.</b>
/// A realm built without it has a plain <c>JSContext</c>, no scheduler and no map, and none of this
/// file runs for it.
/// </para>
/// <para>
/// <b>Why module realms run their jobs inside a task.</b> The pinned engine turns each loaded module
/// into a guest promise through <c>new JSPromise(Task)</c>, which settles through a bare
/// <c>ContinueWith</c> on <see cref="TaskScheduler.Current"/>. Outside a task that is the thread pool,
/// which would settle the promise beside the realm's thread and after the host may already have seen
/// an empty queue. Running module work as a task on <see cref="ModuleTaskScheduler"/> makes the
/// engine's own continuation a job in this realm's queue, drained like any other.
/// </para>
/// </remarks>
internal partial class BroilerJsRealm
{
    private readonly ModuleTaskScheduler? _moduleScheduler;
    private BroilerJsModuleMap? _moduleMap;
    private bool _moduleMapOpened;
    private int _moduleHostCalls;

    /// <summary>The engine's module context, or null for a realm without the module contract.</summary>
    internal BroilerJsModuleContext? ModuleContext => _context as BroilerJsModuleContext;

    /// <summary>Whether the realm has been disposed, for work that must quietly do nothing afterwards.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>
    /// Builds the module context in place of a plain one, with this realm's pump and without CLR
    /// integration, and removes the two globals a plain realm does not have.
    /// </summary>
    private JSContext CreateModuleContext()
    {
        var context = new BroilerJsModuleContext(this, _pump);

        // JSModuleContext's constructor adds Node's `assert` and `global`. A module-capable realm has
        // the same global names as a classic realm from this provider.
        context.Delete((KeyString)"assert");
        context.Delete((KeyString)"global");
        return context;
    }

    /// <summary>Opens the realm's one map; see <see cref="IJsModules.OpenModuleMap"/>.</summary>
    internal IJsModuleMap OpenModuleMapCore(IJsModuleHost host, JsModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();
        ThrowIfInModuleHost();

        if (!BroilerJsModuleAnalysis.IsAvailable)
            throw new NotSupportedException("This Broiler.JS build does not expose the module parse goal JSEAL's module adapter needs.");

        if (_moduleMapOpened)
            throw new InvalidOperationException("This realm already opened its module map; a realm has at most one.");

        _moduleMapOpened = true;
        return _moduleMap = new BroilerJsModuleMap(this, host, options);
    }

    /// <summary>
    /// Runs module work as a task on this realm's scheduler, entered, so that engine continuations it
    /// creates become jobs in this realm's queue.
    /// </summary>
    internal T InModuleTurn<T>(Func<T> work)
    {
        var result = default(T)!;
        _moduleScheduler!.RunInline(() =>
        {
            using var scope = Enter();
            result = work();
        });

        return result;
    }

    /// <summary>Queues provider work behind the jobs already queued, unless the realm is gone.</summary>
    internal void EnqueueModuleJob(Action job)
    {
        if (!_disposed)
            _jobs.Enqueue(job);
    }

    /// <summary>
    /// Compiles module text the way the engine's module loader will, so a parse failure is reported
    /// before evaluation, and the loader's own compile of the same text reuses the engine's code cache.
    /// </summary>
    /// <returns>Null on success, or the failure through the shared exception boundary.</returns>
    internal JsEngineException? CompileModule(string text, string internalName, string label)
    {
        using var scope = Enter();
        try
        {
            using (BroilerJsModuleAnalysis.EnterTopLevelAwait())
            {
                StringSpan code = text;
                CoreScript.Compile(code, internalName, BroilerJsModuleContext.ModuleParameters, _context.CodeCache, null, isModule: true);
            }

            return null;
        }
        catch (JSException engineException)
        {
            var translated = Translate(engineException, internalName, text);
            return new JsEngineException(translated.Message, translated.Thrown, engineException)
            {
                ScriptStackTrace = translated.ScriptStackTrace,
                SourceLabel = label,
                SourceLine = translated.SourceLine,
            };
        }
    }

    /// <summary>A guest TypeError minted in this realm, for failures the engine will throw into guest code.</summary>
    internal Exception ModuleTypeError(string message)
    {
        using var scope = Enter();
        return JSEngine.NewTypeError(message);
    }

    /// <summary>The value a failed evaluation task threw, as a guest value.</summary>
    internal JsValue ThrownBy(Task task) => ThrownValueOf(task.Exception!);

    /// <summary>The guest value an engine exception carries, or an Error the engine mints for a host one.</summary>
    internal JsValue ThrownValueOf(Exception exception)
    {
        using var scope = Enter();
        return BroilerJsMarshal.Wrap(JSException.ErrorFrom(exception));
    }

    /// <summary>The engine's exports object for a module the engine has started evaluating.</summary>
    internal JsValue? EngineNamespace(string internalName)
    {
        using var scope = Enter();
        foreach (var module in ModuleContext!.All)
        {
            if (string.Equals(module.filePath, internalName, StringComparison.Ordinal))
                return BroilerJsMarshal.Wrap(module.Exports);
        }

        return null;
    }

    /// <summary>
    /// Marks a call into the host's <see cref="IJsModuleHost"/>. While one is on the stack, every realm
    /// operation throws <see cref="InvalidOperationException"/> instead of re-entering the engine.
    /// </summary>
    internal ModuleHostCall EnterModuleHost() => new(this);

    internal void ThrowIfInModuleHost()
    {
        if (_moduleHostCalls != 0)
        {
            throw new InvalidOperationException(
                "The module host called back into the realm from Resolve or LoadAsync. Queue the work " +
                "with IJsModuleHost.QueueRealmTask instead.");
        }
    }

    internal readonly struct ModuleHostCall : IDisposable
    {
        private readonly BroilerJsRealm _realm;

        internal ModuleHostCall(BroilerJsRealm realm)
        {
            _realm = realm;
            realm._moduleHostCalls++;
        }

        public void Dispose() => _realm._moduleHostCalls--;
    }

    /// <summary>
    /// What the engine calls for every <c>import</c> a module body reaches, and for <c>require</c>.
    /// </summary>
    internal Task<JSValue> ImportForEngine(string? referrer, string specifier, bool esModule)
    {
        if (!esModule)
        {
            return Task.FromException<JSValue>(ModuleTypeError(
                "require() is not available to modules loaded through JSEAL: CommonJS interoperability is not part of the module contract"));
        }

        return _moduleMap is { } map
            ? map.ImportForEngine(referrer, specifier)
            : Task.FromException<JSValue>(ModuleTypeError("this realm has no open module map"));
    }

    /// <summary>
    /// A scheduler whose tasks run inline or as jobs in the realm's queue, never on the thread pool.
    /// </summary>
    internal sealed class ModuleTaskScheduler(JobQueue queue) : TaskScheduler
    {
        /// <summary>Runs <paramref name="action"/> now, as a task on this scheduler, and rethrows what it threw.</summary>
        internal void RunInline(Action action)
        {
            var task = new Task(action);
            task.RunSynchronously(this);

            if (task.Exception is { } failure)
                ExceptionDispatchInfo.Throw(failure.InnerException ?? failure);
        }

        protected override void QueueTask(Task task) => queue.Enqueue(() => TryExecuteTask(task));

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => TryExecuteTask(task);

        protected override IEnumerable<Task>? GetScheduledTasks() => null;
    }
}

/// <summary>
/// A realm that implements the module contract. Only <see cref="BroilerJsEngineProvider.CreateRealm"/>
/// with the gating option builds one; adoption never does.
/// </summary>
internal sealed class BroilerJsModuleRealm(BroilerJsEngineProvider provider, JsRealmOptions options)
    : BroilerJsRealm(provider, options, moduleContract: true), IJsModules
{
    /// <inheritdoc />
    public IJsModuleMap OpenModuleMap(IJsModuleHost host, JsModuleOptions options) => OpenModuleMapCore(host, options);
}

/// <summary>
/// The engine's module loader, with every decision the contract gives the host taken away from it.
/// </summary>
/// <remarks>
/// <para>
/// <b>No specifier reaches the engine's cache.</b> <c>JSModuleContext.LoadModuleAsync</c> looks up
/// the raw specifier in its module cache before it calls <c>Resolve</c>, which is how it answers
/// <c>module</c> and <c>clr</c> without asking anyone. The override replaces it: every request a
/// module makes was resolved by the host when the graph was loaded, and the engine is only ever
/// handed the adapter's internal name for the module that request names. <c>Resolve</c> accepts
/// exactly those names.
/// </para>
/// <para>
/// An internal name is the module's source label, made unique within the map and never
/// <c>module</c> or <c>clr</c>; it is what the engine compiles under, so its frames name the label.
/// </para>
/// </remarks>
internal sealed class BroilerJsModuleContext(BroilerJsRealm realm, SynchronizationContext pump)
    : JSModuleContext(pump, enableClrIntegration: false)
{
    /// <summary>The parameter names the engine compiles a module body with, in its own order.</summary>
    internal static readonly string[] ModuleParameters = ["exports", "require", "module", "import", "__fileame", "__dirname"];

    /// <summary>Starts, or joins, the engine's evaluation of the module compiled under <paramref name="internalName"/>.</summary>
    internal Task<JSValue> LoadThroughEngine(string internalName) =>
        base.LoadModuleAsync(null, internalName, esModule: true, requiredType: null);

    protected override Task<JSValue> LoadModuleAsync(string? currentPath, string name, bool esModule = true, string? requiredType = null) =>
        realm.ImportForEngine(currentPath, name, esModule);

    protected override string? Resolve(string? dirPath, string relativePath) =>
        realm.ModuleSourceFor(relativePath) is null ? null : relativePath;

    // A module's own requests are looked up by the requesting module, so its "directory" is its name.
    protected override string GetModuleDirectory(string fullPath) => fullPath;

    // import.meta carries no url: filling it is a later host hook, and an invented one would be wrong.
    protected override string? GetModuleUrl(string moduleKey) => null;

    protected override Task<string> ReadModuleSourceAsync(JSModule module) =>
        realm.ModuleSourceFor(module.filePath) is { } source
            ? Task.FromResult(source)
            : Task.FromException<string>(realm.ModuleTypeError($"'{module.filePath}' is not a module this realm's map loaded"));
}
