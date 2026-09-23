using Broiler.VM.Profile.JavaScript;

namespace Broiler.JSeal.Vm;

/// <summary>
/// The module-contract half of a realm: the one map a realm may open, and the rule that keeps the
/// host out of the realm while it is resolving or loading.
/// </summary>
/// <remarks>
/// <b>Reached only through <see cref="VmEngineProvider.EnableModuleContract"/> until I13.</b> A realm
/// built without it has no map, no module loader on its surface and no resolver registered, and none
/// of this runs for it: a guest <c>import()</c> there is refused as a module nobody can find, as it
/// always was.
/// </remarks>
internal partial class VmRealm
{
    private VmModuleMap? _moduleMap;
    private bool _moduleMapOpened;
    private int _moduleHostCalls;

    /// <summary>Whether the realm has been disposed, for work that must quietly do nothing afterwards.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>The bridge, for the map's captured intrinsic and its own loader identity.</summary>
    internal VmHostBridge Bridge => _bridge;

    /// <summary>The source provider, which answers the map's module requests.</summary>
    internal VmSourceProvider Sources => _sources;

    /// <summary>Opens the realm's one map; see <see cref="IJsModules.OpenModuleMap"/>.</summary>
    internal IJsModuleMap OpenModuleMapCore(IJsModuleHost host, JsModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();
        ThrowIfInModuleHost();

        if (_bridge is not VmModuleHostBridge moduleBridge)
            throw new NotSupportedException("This realm was not built with the module contract.");

        if (_moduleMapOpened)
            throw new InvalidOperationException("This realm already opened its module map; a realm has at most one.");

        _moduleMapOpened = true;
        _moduleMap = new VmModuleMap(this, host, options);
        _sources.Modules = _moduleMap;
        moduleBridge.Loader = _moduleMap;
        return _moduleMap;
    }

    /// <summary>
    /// Marks a call into the host's <see cref="IJsModuleHost"/>. While one is on the stack, every realm
    /// operation throws <see cref="InvalidOperationException"/> instead of entering the VM.
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
        private readonly VmRealm _realm;

        internal ModuleHostCall(VmRealm realm)
        {
            _realm = realm;
            realm._moduleHostCalls++;
        }

        public void Dispose() => _realm._moduleHostCalls--;
    }
}

/// <summary>
/// A realm that implements the module contract. Only <see cref="VmEngineProvider.CreateRealm"/> with
/// the gating option builds one.
/// </summary>
internal sealed class VmModuleRealm(
    Broiler.VM.VmRuntime runtime,
    Broiler.VM.VmVerifiedArtifact artifact,
    Broiler.VM.VmInstance instance,
    VmModuleHostBridge bridge,
    VmSourceProvider sources,
    JsRealmOptions options,
    JsCapabilities capabilities,
    string engineName)
    : VmRealm(runtime, artifact, instance, bridge, sources, options, capabilities, engineName), IJsModules
{
    /// <inheritdoc />
    public IJsModuleMap OpenModuleMap(IJsModuleHost host, JsModuleOptions options) => OpenModuleMapCore(host, options);
}

/// <summary>
/// The bridge of a module realm: it also offers every guest <c>import()</c> the realm cannot answer
/// from a module's own static requests to the realm's map (VM JSD-0024 section 15.2), and captures
/// the one intrinsic the map observes evaluation promises through.
/// </summary>
/// <remarks>
/// <para>
/// <b>The profile recognises the loader once, when the realm is handed over</b>, so the interface is
/// on this type from the start and the map is attached later. Before a map is open, and after it is
/// disposed, every import is answered <see cref="JsHostModuleLoad.Now"/>: the provider is asked in the
/// guest's step, finds no permit, answers not found, and the import rejects with a
/// <c>TypeError</c>, exactly as in a realm without the contract.
/// </para>
/// <para>
/// <b><c>Promise.prototype.then</c> is taken at realm creation</b>, before any page script runs, for
/// the same reason <see cref="VmHostBridge.Eval"/> is: the map attaches one reaction to each
/// evaluation promise it starts, to learn that the settlement has been delivered, and a page that
/// replaced <c>then</c> must not see or intercept those calls.
/// </para>
/// </remarks>
internal sealed class VmModuleHostBridge : VmHostBridge, IJsHostModuleLoader
{
    /// <summary>The intrinsic <c>Promise.prototype.then</c>.</summary>
    internal JsHostValue PromiseThen { get; private set; }

    /// <summary>The realm's open map, or null.</summary>
    internal VmModuleMap? Loader { get; set; }

    /// <inheritdoc />
    public override void OnRealmCreated(JsHostRealm realm)
    {
        base.OnRealmCreated(realm);

        var promise = realm.GetProperty(realm.Global, "Promise");
        PromiseThen = promise.Kind is JsHostValueKind.Function
            ? realm.GetProperty(realm.GetProperty(promise, "prototype"), "then")
            : JsHostValue.Undefined;
    }

    /// <inheritdoc />
    public JsHostModuleLoad OnImport(JsHostRealm realm, JsHostModuleRequest request) =>
        Loader?.OfferImport(request) ?? JsHostModuleLoad.Now;
}
