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
    /// The provider declares Document plus GuestEval. Creation removes GuestEval when disallowed and
    /// checks for the required binary and evaluation intrinsics. WorkerRealms, Modules and DynamicImport
    /// are absent. Cloning refuses WorkerRealms; module execution has no JSEAL contract yet.
    /// </remarks>
    public JsCapabilities Capabilities =>
        JsCapabilities.HostScriptSource |
        JsCapabilities.ClassicScriptSource |
        JsCapabilities.GuestEval |
        JsCapabilities.Promises |
        JsCapabilities.ExoticObjects |
        JsCapabilities.GlobalIsVariableScope |
        JsCapabilities.ReentrantHostCalls |
        JsCapabilities.BinaryData;

    /// <inheritdoc />
    public IJsRealm CreateRealm(JsRealmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var bridge = new VmHostBridge();
        var sources = new VmSourceProvider(options.AllowGuestEval, options.ForceStrictMode);

        var catalog = VmCatalog.CreateBuilder()
            .Add(JavaScriptProfile.DescriptorHostingRealms(bridge))
            .Build();

        var created = VmRuntime.Create(catalog, Options(sources));

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

            // AND THE SAME FOR SOURCE. `eval` is behind the profile's dynamic surface the way the
            // binary intrinsics are behind its binary one, and all three kinds of evaluation reach
            // it - it is the only thing that evaluates INTO an existing realm, which is what a host
            // asking a realm to run a script means. A realm without it can run none of them, so it
            // declares none; the bootstrap program that built this realm was compiled and
            // instantiated rather than evaluated, so getting this far proves nothing about `eval`.
            if (bridge.Eval.Kind is not JsHostValueKind.Function)
                capabilities &= ~(JsCapabilities.HostScriptSource |
                                  JsCapabilities.ClassicScriptSource |
                                  JsCapabilities.GuestEval);

            return new VmRealm(runtime, artifact, instance, bridge, sources, capabilities, Name);
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
    private static VmRuntimeCreationOptions Options(VmSourceProvider sources)
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

