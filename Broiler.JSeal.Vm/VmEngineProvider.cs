using System.Collections.Immutable;
using System.Runtime.CompilerServices;

using Broiler.VM;
using Broiler.VM.Profile.JavaScript;
using Broiler.VM.Profile.JavaScript.Compiler;

namespace Broiler.JSeal.Vm;

/// <summary>The Broiler.VM JavaScript profile behind the JSEAL realm contracts.</summary>
/// <remarks>
/// Each created realm owns a runtime, verified bootstrap artifact and instance. The provider uses
/// the profile's in-realm host surface for objects, callbacks and guest calls. It declares Document
/// capabilities, but that declaration is not evidence that an external browser uses it for page loads.
/// Host integration is tracked separately in docs/roadmap.integration.md.
/// </remarks>
public sealed class VmEngineProvider : IJsEngineProvider
{
    /// <summary>The caller identity this provider verifies its artifacts under.</summary>
    private const string Caller = "broiler-jseal-vm://realm";

    /// <inheritdoc />
    public string Name => "broiler-vm";

    /// <inheritdoc />
    public string Description =>
        "The Broiler.VM JavaScript profile, bound through its in-realm host surface.";

    /// <summary>The capabilities available before realm-specific narrowing.</summary>
    /// <remarks>
    /// <para>
    /// The provider declares Document plus GuestEval and StructuredClone. Creation removes GuestEval
    /// when disallowed, and BinaryData or StructuredClone when the realm turns out not to have them.
    /// Modules and DynamicImport are absent; the module contract is reached only through
    /// <see cref="EnableModuleContract"/> until I13.
    /// </para>
    /// <para>
    /// <b>WorkerRealms is absent although both of its halves run (I18).</b> A realm of this provider
    /// may be built and driven on any thread, and <c>VmRealm.Detach</c>/<c>Adopt</c> move values
    /// between two such realms through the profile's own carrier. What does not meet the contract is
    /// <see cref="IJsClone.Adopt"/>'s promise that a carrier may be adopted more than once: the
    /// profile makes a carrier holding transferred bytes single-use (VM JSD-0024 section 17), and
    /// making it repeatable here would need a second serialization after the source buffers are
    /// already detached, which would break the transfer's failure atomicity. Until an owner decides
    /// the contract - HTML delivers a transfer to one receiver - the flag stays undeclared, and
    /// <see cref="EnableWorkerRealms"/> exercises the two halves in tests only.
    /// </para>
    /// </remarks>
    public JsCapabilities Capabilities =>
        JsCapabilities.HostScriptSource |
        JsCapabilities.ClassicScriptSource |
        JsCapabilities.GuestEval |
        JsCapabilities.Promises |
        JsCapabilities.ExoticObjects |
        JsCapabilities.GlobalIsVariableScope |
        JsCapabilities.ReentrantHostCalls |
        JsCapabilities.BinaryData |
        JsCapabilities.StructuredClone |
        (EnableWorkerRealms ? JsCapabilities.WorkerRealms : JsCapabilities.None);

    /// <summary>
    /// Declares <see cref="JsCapabilities.WorkerRealms"/>. Internal and test-only: it lets the shared
    /// worker-realm cases run against this provider's Detach and Adopt without the registered
    /// provider claiming a capability whose contract it does not fully meet (see
    /// <see cref="Capabilities"/>).
    /// </summary>
    internal bool EnableWorkerRealms { get; init; }

    /// <summary>
    /// Builds realms that implement the I09 module contract (<see cref="IJsModules"/>). Internal and
    /// test-only until I13, like <c>BroilerJsEngineProvider.EnableModuleContract</c>.
    /// </summary>
    /// <remarks>
    /// <b>The registered provider leaves it off, and then nothing changes.</b> A realm built with it
    /// has a bridge that offers guest <c>import()</c> to the realm's map, and the profile's resolver
    /// capability registered and answered from that map; one built without it has neither, so a guest
    /// <c>import()</c> is still refused as a module nobody can find. Neither kind declares
    /// <see cref="JsCapabilities.Modules"/> or <see cref="JsCapabilities.DynamicImport"/>: I13
    /// publishes them, and only for the contract behavior the adapter meets.
    /// </remarks>
    internal bool EnableModuleContract { get; init; }

    /// <inheritdoc />
    public IJsRealm CreateRealm(JsRealmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var bridge = EnableModuleContract ? new VmModuleHostBridge() : new VmHostBridge();
        var sources = new VmSourceProvider(options.AllowGuestEval);

        var catalog = VmCatalog.CreateBuilder()
            .Add(JavaScriptProfile.DescriptorHostingRealms(bridge))
            .Build();

        var created = VmRuntime.Create(catalog, Options(sources, EnableModuleContract));

        if (!created.TryGetRuntime(out var runtime))
        {
            throw new JsEngineException(
                $"the Broiler.VM runtime refused creation: {created.Outcome}/{created.Reason}");
        }

        try
        {
            // THE REALM NEEDS AN INSTANCE AND AN INSTANCE NEEDS AN ARTIFACT, so the smallest legal
            // program is compiled to make one. Nothing in it runs: what matters is that
            // instantiating it is the moment the profile builds a realm and hands it over.
            var compiled = JsCompiler.Compile(
                [new JsScriptUnit("bootstrap", string.Empty, SliceParseOptions.Script, options.ForceStrictMode, Caller)],
                [],
                new JsCompileRequest());

            if (!compiled.Succeeded || compiled.Artifact is null)
                throw new JsEngineException("the Broiler.VM front end refused an empty program");

            var descriptor = new VmArtifactDescriptor(
                JavaScriptProfile.Id,
                Broiler.VM.Profile.JavaScript.Format.JsFormat.FormatVersion,
                JavaScriptProfile.WideManifest,
                default,
                VmCallerIdentity.FromCanonicalIdentity(Caller));

            var verified = runtime.Verify(in descriptor, compiled.Artifact, CancellationToken.None);

            if (!verified.TryGetArtifact(out var artifact))
            {
                throw new JsEngineException(
                    $"the Broiler.VM verifier refused a bootstrap program: "
                        + $"{verified.Outcome}/{verified.Reason}");
            }

            var instantiated = runtime.Instantiate(artifact, CancellationToken.None);

            if (!instantiated.TryGetInstance(out var instance))
            {
                artifact.Dispose();

                throw new JsEngineException(
                    $"the Broiler.VM runtime refused to instantiate: "
                        + $"{instantiated.Outcome}/{instantiated.Reason}");
            }

            if (bridge.Realm is null)
            {
                instance.Dispose();
                artifact.Dispose();

                throw new JsEngineException(
                    "the Broiler.VM instance was created without handing over a realm, which means "
                        + "the host-surface capability was not bound");
            }

            var capabilities = options.AllowGuestEval
                ? Capabilities
                : Capabilities & ~JsCapabilities.GuestEval;

            // NARROWED TO WHAT THE REALM ACTUALLY HAS. The profile builds ArrayBuffer and the typed
            // arrays only for a composition that admits its binary surface; this provider asks for
            // every surface, so they are there - but a realm is entitled to answer for itself rather
            // than for the composition that usually builds it, and a capability that is declared
            // where it is not true is worse than one that is absent.
            if (!bridge.HasBinary)
                capabilities &= ~JsCapabilities.BinaryData;

            // AND THE SAME FOR DYNAMIC SOURCE. `eval` is behind the profile's dynamic surface the way
            // the binary intrinsics are behind its binary one, and dynamic source is evaluated
            // through it. Host and classic scripts are not: they go through
            // `JsHostRealm.EvaluateScript`, which needs only the source provider registered above.
            if (bridge.Eval.Kind is not JsHostValueKind.Function)
                capabilities &= ~JsCapabilities.GuestEval;

            // AND FOR CLONE: declared only when the realm cloned a value at handover.
            if (!bridge.HasClone)
                capabilities &= ~(JsCapabilities.StructuredClone | JsCapabilities.WorkerRealms);

            return bridge is VmModuleHostBridge moduleBridge
                ? new VmModuleRealm(runtime, artifact, instance, moduleBridge, sources, options, capabilities, Name)
                : new VmRealm(runtime, artifact, instance, bridge, sources, options, capabilities, Name);
        }
        catch
        {
            runtime.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The runtime this provider builds: the host-surface permission, and a compiler behind
    /// <c>eval</c>.
    /// </summary>
    /// <remarks>
    /// <b>The host-surface capability is registered with a handler that does nothing, and that is
    /// what it is.</b> The profile asks whether the slot is bound and never invokes it; registering
    /// it is a composition saying that this runtime's realms may have host objects in them, which is
    /// the permission the whole seam hangs on.
    /// </remarks>
    private static VmRuntimeCreationOptions Options(VmSourceProvider sources, bool moduleContract)
    {
        var ceilings = ImmutableArray.CreateBuilder<VmCeilingSpec>();

        foreach (var dimension in VmBudgetDimensions.All)
        {
            ceilings.Add(dimension is VmBudgetDimension.LiveRuntimes
                ? VmCeilingSpec.AdoptParentRemaining(dimension)
                : VmCeilingSpec.AdoptProfileDefault(dimension));
        }

        var capabilities = ImmutableArray.CreateBuilder<VmCapabilityRegistration>();

        capabilities.Add(VmCapabilityRegistration.Value(
            JavaScriptProfile.HostSurfaceCapability,
            static (VmBytes argument, out VmOpaqueRef answer) =>
            {
                answer = default;
                return VmHostCallOutcome.Completed;
            }));

        capabilities.Add(VmCapabilityRegistration.ArtifactProvider(
            JavaScriptProfile.SourceProviderCapability, sources));

        // THE RESOLVER ONLY FOR A MODULE REALM. The profile asks it to confirm every resolution of
        // a module graph before linking it (JSD-0024 section 15.1), and a realm that answers module
        // requests from no map has none to confirm; leaving it unregistered keeps such a realm's
        // module artifacts refused exactly as before the contract existed.
        if (moduleContract)
        {
            capabilities.Add(VmCapabilityRegistration.Value(
                JavaScriptProfile.ResolveCapability,
                (VmBytes argument, out VmOpaqueRef answer) =>
                {
                    answer = default;
                    return sources.ConfirmResolution(argument.Span) ? VmHostCallOutcome.Completed : VmHostCallOutcome.Refused;
                }));
        }

        return new VmRuntimeCreationOptions(
            aggregateBudget: null,
            ceilings: ceilings.ToImmutable(),
            maxSuspendedResidency: TimeSpan.FromMinutes(1),
            maxLiveSuspendedOperations: 1,
            guestLoadBounds: VmGuestLoadBoundsSpec.AdoptProfileMaxima,
            externalSuspension: VmExternalSuspensionMode.Disabled,
            capabilities: capabilities.ToImmutable());
    }

    /// <summary>Registers this provider. Idempotent.</summary>
    public static void Register() => JsEngineRegistry.Register(new VmEngineProvider());

    /// <summary>
    /// Registers on assembly load, for a host that reaches this assembly without naming this type.
    /// </summary>
    /// <remarks>
    /// A module initializer runs when the CLR first loads the assembly, and the CLR loads it when a
    /// type in it is first touched - so this is a convenience for a host that touches one, and not a
    /// substitute for a host that names the provider deliberately.
    /// </remarks>
    [ModuleInitializer]
    internal static void AutoRegister() => Register();
}

