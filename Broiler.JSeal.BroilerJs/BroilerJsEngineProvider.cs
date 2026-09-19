using System.Runtime.CompilerServices;

namespace Broiler.JSeal.BroilerJs;

/// <summary>
/// Broiler.JS, as something the browser can choose.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registration happens because the assembly is linked, not because a host remembered to ask.</b>
/// <see cref="JsEngineRegistry"/> decides among the providers that are present and the build decides
/// which are present, so the moment the two must agree is assembly load â€” which is exactly what a
/// <see cref="ModuleInitializerAttribute"/> names. A host wiring step in its place would be a second
/// place for "which engine is linked" to be recorded, and the one that is a comment in a csproj
/// rather than a reference would drift.
/// </para>
/// <para>
/// <see cref="Register"/> is public anyway, for the host that needs the registration to have happened
/// by a particular point â€” a test that calls <see cref="JsEngineRegistry.Reset"/> and has to put the
/// process back, most of all, since a module initializer runs once and will not run again for it.
/// </para>
/// </remarks>
public sealed class BroilerJsEngineProvider : IJsEngineProvider, IJsRealmAdoption
{
    /// <inheritdoc />
    public string Name => "broiler-js";

    /// <inheritdoc />
    public string Description => "Broiler.JS â€” the from-scratch C# ECMAScript engine (Broiler.JavaScript).";

    /// <summary>
    /// A host's answer to whether this engine binds ES modules end to end, when the host has paid to
    /// find out. <see langword="null"/> â€” the default â€” means the question has not been asked, and
    /// the module capabilities are then reported absent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the provider cannot simply assert Modules and DynamicImport.</b> Whether a static
    /// import binds its value is not a fact about Broiler.JS the engine; it is a fact about the
    /// checkout â€” it became true only with submodule patches 0010 (top-level-await codegen) and 0011
    /// (module-orchestration completion), and this provider compiles unchanged against a submodule
    /// without them. Worse, the failure is not a refusal: on an unpatched engine the import resolves
    /// to <c>undefined</c>, or the module body never completes, which is why
    /// <c>Broiler.HtmlBridge.Dom/EngineModuleSupport.cs</c> probes on a worker thread behind a
    /// five-second timeout and treats a hang as "not supported".
    /// </para>
    /// <para>
    /// A capability is a claim a host is entitled to branch on without paying to verify it, so
    /// asserting one whose truth costs a five-second timeout to establish would make the declaration
    /// worth less than the probe it replaced. The provider therefore declares what it can always
    /// honour and lets the host that already ran the probe publish the result here â€” one line,
    /// <c>BroilerJsEngineProvider.ModuleSupport = () =&gt; EngineModuleSupport.Available;</c> â€” so the
    /// answer reaches the contract without the contract having to go and get it.
    /// </para>
    /// </remarks>
    public static Func<bool>? ModuleSupport { get; set; }

    /// <summary>
    /// What every realm from this provider can do before a host narrows it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each of these is true of Broiler.JS and is exercised by the bridge today:
    /// <see cref="JsCapabilities.HostScriptSource"/>, <see cref="JsCapabilities.ClassicScriptSource"/>
    /// and <see cref="JsCapabilities.GuestEval"/> each name an <see cref="IJsSource"/> member, and all
    /// three members reach <c>JSContext.Eval</c>, which compiles at run time and does not distinguish
    /// them â€” the distinction is the host's. GuestEval is the only one a realm can lose, and this
    /// provider enforces its absence in two places: at <c>EvaluateDynamicSource</c>
    /// (<c>BroilerJsRealm.Source.cs</c>), and at the page's own <c>eval</c>, <c>Function</c> at every
    /// arity and <c>ShadowRealm.prototype.evaluate</c> through the <c>EvalEvent</c> subscription in
    /// <c>BroilerJsRealm.cs</c>, an event the engine raises before each of them compiles;
    /// <see cref="JsCapabilities.Promises"/> is <c>JSPromise</c>, whose delegate constructor settles
    /// from host code; <see cref="JsCapabilities.ExoticObjects"/> is the <c>JSObject</c> lookup
    /// protocol <c>BroilerJsExoticObject</c> overrides; <see cref="JsCapabilities.GlobalIsVariableScope"/>
    /// is the engine's defining structural choice, that the <c>JSContext</c> <em>is</em> the global;
    /// <see cref="JsCapabilities.WorkerRealms"/> is a second <c>JSContext</c> on a second thread, which
    /// is what the bridge's Worker support already builds;
    /// <see cref="JsCapabilities.ReentrantHostCalls"/> is simply how the engine runs â€” a native
    /// function may call <c>InvokeFunction</c> while the engine is inside it, which is what every
    /// event dispatch in the bridge does; and <see cref="JsCapabilities.BinaryData"/> is
    /// <c>JSArrayBuffer</c>, which <c>NewArrayBuffer</c> mints over a copy of the host's bytes and
    /// <c>TryGetArrayBufferBytes</c> reads back, <c>SharedArrayBuffer</c> excluded
    /// (<c>BroilerJsRealm.Values.cs</c>).
    /// </para>
    /// <para>
    /// <see cref="JsCapabilities.Modules"/> and <see cref="JsCapabilities.DynamicImport"/> appear only
    /// when <see cref="ModuleSupport"/> says so; see its remarks.
    /// </para>
    /// </remarks>
    public JsCapabilities Capabilities
    {
        get
        {
            var capabilities =
                JsCapabilities.HostScriptSource |
                JsCapabilities.ClassicScriptSource |
                JsCapabilities.GuestEval |
                JsCapabilities.Promises |
                JsCapabilities.ExoticObjects |
                JsCapabilities.GlobalIsVariableScope |
                JsCapabilities.WorkerRealms |
                JsCapabilities.ReentrantHostCalls |
                JsCapabilities.BinaryData;

            // A probe that throws answers "no": the caller asked whether a feature works, and a
            // question that cannot be answered is not evidence that it does.
            try
            {
                if (ModuleSupport?.Invoke() == true)
                    capabilities |= JsCapabilities.Modules | JsCapabilities.DynamicImport;
            }
            catch (Exception)
            {
                // Deliberately swallowed; see above.
            }

            return capabilities;
        }
    }

    /// <inheritdoc />
    public IJsRealm CreateRealm(JsRealmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new BroilerJsRealm(this, options);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The type test is what makes this safe to offer to every provider in turn: a host with two
    /// engines linked hands the same object to both, and the one that did not make it must say so
    /// rather than wrap something it cannot drive. <c>JSModuleContext</c> derives from
    /// <c>JSContext</c>, so a module context adopts too â€” which is what the bridge's module path
    /// needs, since that is the realm a page's <c>&lt;script type="module"&gt;</c> runs in.
    /// </remarks>
    public bool TryAdopt(object engineRealm, JsRealmOptions options, out IJsRealm? realm)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (engineRealm is Broiler.JavaScript.Engine.JSContext context)
        {
            realm = new BroilerJsRealm(this, context, options);
            return true;
        }

        realm = null;
        return false;
    }

    /// <summary>
    /// Puts this provider in the process-wide registry. Idempotent â€” re-registering replaces the
    /// entry under the same name, which is what <see cref="JsEngineRegistry.Register"/> promises.
    /// </summary>
    public static void Register() => JsEngineRegistry.Register(new BroilerJsEngineProvider());

    [ModuleInitializer]
    internal static void AutoRegister() => Register();
}

