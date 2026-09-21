using System.Runtime.CompilerServices;

namespace Broiler.JSeal.BroilerJs;

/// <summary>The Broiler.JS implementation of the JSEAL realm contracts.</summary>
/// <remarks>
/// The module initializer registers on assembly load. Hosts can call Register explicitly to ensure
/// registration before choosing an engine or to restore it after clearing their registry.
/// </remarks>
public sealed class BroilerJsEngineProvider : IJsEngineProvider, IJsRealmAdoption
{
    /// <inheritdoc />
    public string Name => "broiler-js";

    /// <inheritdoc />
    public string Description => "Broiler.JS â€” the from-scratch C# ECMAScript engine (Broiler.JavaScript).";

    /// <summary>Optional host evidence for static and dynamic module support.</summary>
    /// <remarks>
    /// Null, false or a throwing callback leaves Modules and DynamicImport absent. A true result advertises
    /// both flags. JSEAL has no module-loader or module-evaluation contract; hosts using these flags must
    /// provide and validate their own integration. Referencing the Modules package alone is not evidence
    /// of module execution through JSEAL; that package also supplies ordinary arguments-object support.
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

