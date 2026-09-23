namespace Broiler.JSeal;

/// <summary>Features a provider declares and a realm may narrow.</summary>
/// <remarks>
/// <para>
/// Hosts check capabilities before choosing an operation or fallback. A declared flag describes
/// the JSEAL provider, not complete language conformance or downstream browser integration.
/// </para>
/// <para>
/// <b>Provider ability and realm permission are separate.</b> IJsEngineProvider.Capabilities is
/// what the engine can do; IJsRealm.Capabilities is that ability narrowed, and is never wider. The
/// host narrows it through the permissions it chooses when building the realm
/// (JsRealmOptions.AllowGuestEval removes GuestEval); a provider may also withhold a flag from a realm
/// that turns out to lack what the flag needs, rather than declare something that realm cannot do.
/// A contract member used without its capability throws JsCapabilityUnavailableException.
/// </para>
/// <para>
/// Numeric values are public contract and are never renumbered; a new flag takes a new bit.
/// </para>
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
    /// Supports static module graphs through the optional <see cref="IJsModules"/> contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The contract types exist since I10, but no provider declares this flag for the contract
    /// before I13, which publishes it only for the contract behavior a provider actually meets. A
    /// realm without the flag does not implement <see cref="IJsModules"/> outside the providers'
    /// internal test gate.
    /// </para>
    /// <para>
    /// Until an owner decision retires it, the Broiler.JS provider's host-asserted
    /// <c>BroilerJsEngineProvider.ModuleSupport</c> callback can still set this flag and
    /// <see cref="DynamicImport"/> for the host's own module integration, on realms that do not
    /// implement <see cref="IJsModules"/>. Check for the contract type as well as the flag.
    /// </para>
    /// </remarks>
    Modules = 1 << 2,

    /// <summary>
    /// Supports guest <c>import()</c> through the realm's <see cref="IJsModuleMap"/>. Requires
    /// <see cref="Modules"/>.
    /// </summary>
    /// <remarks>
    /// Routing <c>import()</c> through the map is I12; no provider declares this flag for the
    /// contract before I13. The host-asserted callback described under <see cref="Modules"/> can
    /// still set it.
    /// </remarks>
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
    /// Supports separate realms on separate threads and structured clone exchange between them
    /// through IJsClone.Detach and IJsClone.Adopt. This does not permit concurrent execution of one realm.
    /// </summary>
    /// <remarks>
    /// Same-realm IJsClone.Clone is <see cref="StructuredClone"/> (I18, adopting the J18 decision), and
    /// every provider that declares this flag also declares that one: a message crossing to a worker
    /// is cloned by the same algorithm a same-document message is. Before I18 this flag gated Clone
    /// as well; a host that checks it before calling Clone is still right, only narrower than it
    /// needs to be.
    /// </remarks>
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
    /// Structured clone within one realm through IJsClone.Clone, including its transfer list, and
    /// IJsClone.ClassifyTransferable's answers about what may be transferred.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Added by I18 on a new bit, as the J18 decision required before any provider supports only one
    /// half of structured clone. It says nothing about a second realm or a second thread; that is
    /// <see cref="WorkerRealms"/>, which gates IJsClone.Detach and IJsClone.Adopt.
    /// </para>
    /// <para>
    /// The algorithm, its supported brands and its refusals are the engine's. A value the provider
    /// refuses fails with JsEngineException (the host's DataCloneError). The flag does not certify
    /// that every clone matches HTML's StructuredSerialize: where a declaring provider's engine copies
    /// a value HTML refuses, or keeps less of a value than HTML keeps, the deviation is listed per
    /// provider in docs/jseal.md and pinned by a recorded gap in the shared clone cases, so a host
    /// that needs HTML's exact answers can see which values to check itself.
    /// </para>
    /// </remarks>
    StructuredClone = 1 << 11,

    /// <summary>
    /// The baseline composite for document hosts. GuestEval is optional and excluded.
    /// Declaring this composite does not establish browser integration or complete JavaScript support.
    /// </summary>
    /// <remarks>
    /// It asserts exactly its seven flags, each witnessed separately by the conformance suite, and no
    /// language feature beyond what those flags name.
    /// </remarks>
    Document = HostScriptSource | ClassicScriptSource | Promises | ExoticObjects |
               GlobalIsVariableScope | ReentrantHostCalls | BinaryData,
}

