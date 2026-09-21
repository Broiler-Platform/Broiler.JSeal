using Broiler.VM;
using Broiler.VM.Profile.JavaScript;
using Broiler.VM.Profile.JavaScript.Compiler;

namespace Broiler.JSeal.Vm;

/// <summary>
/// Compiles source for the realm and enforces guest compilation policy.
/// </summary>
/// <remarks>
/// The VM request carries source bytes, not the host/classic/dynamic distinction. The adapter
/// supplies that distinction immediately before invoking the captured eval intrinsic, with a
/// string argument and no intervening guest code. The next compilation consumes it before
/// parsing or executing anything. Guest eval and Function therefore cannot borrow permission
/// or forced strictness from a host script that calls them. Nested host API calls obtain their
/// own permits. All permit access occurs inside the realm's execution step.
/// </remarks>
internal sealed class VmSourceProvider : IVmArtifactProvider
{
    private readonly bool _allowGuestEval;
    private readonly bool _forceStrictMode;
    private SourceKind _pendingScript;

    internal enum SourceKind
    {
        Guest,
        Host,
        Classic
    }

    internal VmSourceProvider(bool allowGuestEval, bool forceStrictMode)
    {
        _allowGuestEval = allowGuestEval;
        _forceStrictMode = forceStrictMode;
    }

    /// <inheritdoc />
    public VmCapabilityId CapabilityId => JavaScriptProfile.SourceProviderCapability.CapabilityId;

    /// <inheritdoc />
    public int Version => JavaScriptProfile.SourceProviderCapability.Version;

    /// <summary>Authorizes only the compilation about to be requested by the host API.</summary>
    internal ScriptScope EnterScript(SourceKind kind) => new(this, kind);

    /// <inheritdoc />
    public VmArtifactProviderAnswer Answer(scoped in VmArtifactRequest request)
    {
        if (request.RequestingProfileId != JavaScriptProfile.Id)
            return VmArtifactProviderAnswer.NotFound(VmReason.ProviderArtifactNotFound);

        // Consume before decoding or compiling, including requests whose compilation fails.
        // Execution starts only after Answer returns, when no permit remains.
        var kind = _pendingScript;
        _pendingScript = SourceKind.Guest;

        if (kind == SourceKind.Guest && !_allowGuestEval)
            return VmArtifactProviderAnswer.Refused(VmReason.ProviderRefused);

        string source;

        try
        {
            source = System.Text.Encoding.UTF8.GetString(request.RequestPayload.Span);
        }
        catch (ArgumentException)
        {
            return VmArtifactProviderAnswer.Refused(VmReason.MalformedEncoding);
        }

        var forceStrict = kind == SourceKind.Host && _forceStrictMode;
        var compiled = JsCompiler.Compile(
            [new JsScriptUnit("main", source, SliceParseOptions.Script, forceStrict)]);

        if (!compiled.Succeeded || compiled.Artifact is null)
            return VmArtifactProviderAnswer.Refused(VmReason.SemanticValidationFailed);

        var descriptor = new VmArtifactDescriptor(
            JavaScriptProfile.Id,
            Broiler.VM.Profile.JavaScript.Format.JsFormat.FormatVersion,
            JavaScriptProfile.WideManifest,
            default,
            VmCallerIdentity.FromCanonicalIdentity("broiler-jseal-vm://source-provider"));

        return VmArtifactProviderAnswer.Provided(in descriptor, compiled.Artifact);
    }

    /// <summary>
    /// Restores the enclosing request state on every exit, including failure before a compile.
    /// An executing outer script has already consumed its permit, so nesting restores Guest.
    /// </summary>
    internal readonly struct ScriptScope : IDisposable
    {
        private readonly VmSourceProvider _provider;
        private readonly SourceKind _previous;

        internal ScriptScope(VmSourceProvider provider, SourceKind kind)
        {
            _provider = provider;
            _previous = provider._pendingScript;
            provider._pendingScript = kind;
        }

        /// <inheritdoc />
        public void Dispose() => _provider._pendingScript = _previous;
    }
}

