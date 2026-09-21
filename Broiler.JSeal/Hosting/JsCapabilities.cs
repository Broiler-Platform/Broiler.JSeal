namespace Broiler.JSeal;

/// <summary>Features a provider declares and a realm may narrow.</summary>
/// <remarks>
/// Hosts check capabilities before choosing an operation or fallback. A declared flag describes
/// the JSEAL provider, not complete language conformance or downstream browser integration.
/// </remarks>
[Flags]
public enum JsCapabilities : uint
{
    /// <summary>An engine that can do none of the below. Not useful; the zero value exists so a
    /// provider under construction has something to return.</summary>
    None = 0,

    /// <summary>
    /// Runs trusted host-authored JavaScript through IJsSource.EvaluateHostScript.
    /// </summary>
    HostScriptSource = 1 << 0,

    /// <summary>
    /// Allows dynamic compilation through EvaluateDynamicSource and guest eval/Function.
    /// Realm options may remove this permission without disabling host or classic script evaluation.
    /// </summary>
    GuestEval = 1 << 1,

    /// <summary>
    /// Supports static module integration. JSEAL currently has no module-evaluation contract.
    /// </summary>
    Modules = 1 << 2,

    /// <summary>
    /// Supports dynamic import integration. JSEAL currently has no module-loader contract.
    /// </summary>
    DynamicImport = 1 << 3,

    /// <summary>
    /// Exposes promises to the host: a pending promise can be created and settled from host code.
    /// Without it, <c>fetch</c>, <c>customElements.whenDefined</c> and the streams polyfill have no
    /// deferred result to hand back.
    /// </summary>
    Promises = 1 << 4,

    /// <summary>
    /// Supports host-completed property lookup through IJsExotic.
    /// </summary>
    ExoticObjects = 1 << 5,

    /// <summary>
    /// Top-level var and function declarations become properties of the global object.
    /// </summary>
    GlobalIsVariableScope = 1 << 6,

    /// <summary>
    /// Supports separate realms on separate threads and structured clone exchange through IJsClone.
    /// This does not permit concurrent execution of one realm.
    /// </summary>
    WorkerRealms = 1 << 7,

    /// <summary>
    /// A host callback may invoke guest code while its realm is already executing.
    /// </summary>
    ReentrantHostCalls = 1 << 8,

    /// <summary>
    /// Creates and reads ArrayBuffer data through IJsValues; SharedArrayBuffer is excluded.
    /// </summary>
    BinaryData = 1 << 9,

    /// <summary>
    /// Runs supplied classic script text after the host authorizes that script.
    /// This is independent of the GuestEval permission and does not apply ForceStrictMode.
    /// </summary>
    ClassicScriptSource = 1 << 10,

    /// <summary>
    /// The baseline composite for document hosts. GuestEval is optional and excluded.
    /// Declaring this composite does not establish browser integration or complete JavaScript support.
    /// </summary>
    Document = HostScriptSource | ClassicScriptSource | Promises | ExoticObjects |
               GlobalIsVariableScope | ReentrantHostCalls | BinaryData,
}

