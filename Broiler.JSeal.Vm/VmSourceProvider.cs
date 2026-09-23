using Broiler.VM;
using Broiler.VM.Profile.JavaScript;
using Broiler.VM.Profile.JavaScript.Compiler;
using Broiler.VM.Profile.JavaScript.Format;

namespace Broiler.JSeal.Vm;

/// <summary>
/// Compiles source for the realm and enforces guest compilation policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>The request says who asked, so the provider no longer has to be told.</b> The VM marks every
/// program request (VM JSD-0024 section 15, JSD-0026 section 5): <see cref="JsFormat.ScriptRequestMark"/>
/// is written only by <c>JsHostRealm.EvaluateScript</c>, which only the embedder can call, and every
/// other request - a direct or indirect <c>eval</c>, the <c>Function</c> constructor - is one a
/// guest made. A script request is therefore compiled whatever the guest policy says, and a guest
/// request is refused when the realm was built without <c>AllowGuestEval</c>. A host script that
/// itself calls <c>eval</c> sends a guest request, so it cannot lend the host's permission.
/// </para>
/// <para>
/// Each scope records the front-end diagnostic of the one compilation it requested, so the realm
/// can attribute a syntax error to the supplied text. The first request answered inside a scope is
/// the one the scope made; any later one comes from guest code the scope's source is running.
/// </para>
/// <para>
/// <b>A module request is answered only from the realm's module map</b> (JSeal I11/I12): with the
/// graph the map compiled and opened a permit for, for exactly that referrer and specifier, and
/// otherwise not found, which the guest sees as a <c>TypeError</c>. No guest-evaluation permission
/// is consulted for it; module loading is not <c>eval</c> (VM JSD-0024 section 15.3).
/// </para>
/// </remarks>
internal sealed class VmSourceProvider : IVmArtifactProvider
{
    private readonly bool _allowGuestEval;
    private bool _awaitingRequested;
    private SliceSourceDiagnostic? _requestedFailure;
    private ModulePermit? _pendingModule;

    internal VmSourceProvider(bool allowGuestEval)
    {
        _allowGuestEval = allowGuestEval;
    }

    /// <inheritdoc />
    public VmCapabilityId CapabilityId => JavaScriptProfile.SourceProviderCapability.CapabilityId;

    /// <inheritdoc />
    public int Version => JavaScriptProfile.SourceProviderCapability.Version;

    /// <summary>Records the diagnostic of the compilation the host API is about to request.</summary>
    internal RequestScope EnterRequest() => new(this);

    /// <summary>
    /// The first diagnostic of the compilation the innermost open scope requested, when it failed.
    /// </summary>
    internal SliceSourceDiagnostic? RequestedFailure => _requestedFailure;

    /// <inheritdoc />
    public VmArtifactProviderAnswer Answer(scoped in VmArtifactRequest request)
    {
        if (request.RequestingProfileId != JavaScriptProfile.Id)
            return VmArtifactProviderAnswer.NotFound(VmReason.ProviderArtifactNotFound);

        // Consume before decoding or compiling, including requests whose compilation fails.
        var requested = _awaitingRequested;
        _awaitingRequested = false;
        var module = _pendingModule;
        _pendingModule = null;

        var payload = request.RequestPayload.Span;

        if (JsFormat.TryReadModuleRequest(payload, out var referrer, out var specifier))
        {
            return module is not null &&
                string.Equals(module.Referrer, referrer, StringComparison.Ordinal) &&
                string.Equals(module.Specifier, specifier, StringComparison.Ordinal)
                    ? Provided(module.Artifact)
                    : VmArtifactProviderAnswer.NotFound(VmReason.ProviderArtifactNotFound);
        }

        var embedder = payload.Length != 0 && payload[0] == JsFormat.ScriptRequestMark;

        if (!embedder && !_allowGuestEval)
            return VmArtifactProviderAnswer.Refused(VmReason.ProviderRefused);

        if (!JsCompiler.TryReadProgramRequest(payload, out var script))
            return VmArtifactProviderAnswer.Refused(VmReason.MalformedEncoding);

        // A dynamic import() in a host or classic script is resolved against the script's label.
        if (embedder)
            script = script with { Referrer = ScriptReferrer(script.SourceName) };

        var compiled = JsCompiler.Compile([script]);

        if (!compiled.Succeeded || compiled.Artifact is null)
        {
            if (requested && compiled.Diagnostics.Count > 0)
                _requestedFailure = compiled.Diagnostics[0];

            return VmArtifactProviderAnswer.Refused(VmReason.SemanticValidationFailed);
        }

        return Provided(compiled.Artifact);
    }

    /// <summary>An answer carrying <paramref name="artifact"/> under this provider's identity.</summary>
    private static VmArtifactProviderAnswer Provided(byte[] artifact)
    {
        var descriptor = new VmArtifactDescriptor(
            JavaScriptProfile.Id,
            JsFormat.FormatVersion,
            JavaScriptProfile.WideManifest,
            default,
            VmCallerIdentity.FromCanonicalIdentity("broiler-jseal-vm://source-provider"));

        return VmArtifactProviderAnswer.Provided(in descriptor, artifact);
    }

    /// <summary>
    /// The referrer a host or classic script's <c>import()</c> carries: its label behind a prefix
    /// that begins with U+0001.
    /// </summary>
    /// <remarks>
    /// The profile hands the module map a referrer as one string, and a module's is its key, which is
    /// the host's to choose. The map reads a referrer as a module key first; a script's label is
    /// tagged so that it is not taken for a key that happens to equal it, and a key would have to
    /// begin with the control character to be taken for a script. Eval code and <c>Function</c>
    /// bodies carry the referrer of the script or module they were created from.
    /// </remarks>
    internal static string ScriptReferrer(string label) => ScriptReferrerPrefix + label;

    /// <summary>Reads what <see cref="ScriptReferrer"/> wrote, or answers false.</summary>
    internal static bool TryReadScriptReferrer(string referrer, out string label)
    {
        if (referrer.StartsWith(ScriptReferrerPrefix, StringComparison.Ordinal))
        {
            label = referrer[ScriptReferrerPrefix.Length..];
            return true;
        }

        label = string.Empty;
        return false;
    }

    private const string ScriptReferrerPrefix = "\u0001script:";

    /// <summary>The module map whose resolutions the composition's resolver confirms, when one is open.</summary>
    internal VmModuleMap? Modules { get; set; }

    /// <summary>
    /// Authorizes exactly one module request, for <paramref name="referrer"/> and
    /// <paramref name="specifier"/>, answered with the graph <paramref name="artifact"/> the module
    /// map compiled. The next request consumes it, whatever it is.
    /// </summary>
    internal ModuleScope EnterModule(string referrer, string specifier, byte[] artifact) =>
        new(this, new ModulePermit(referrer, specifier, artifact));

    /// <summary>
    /// The profile's resolver question (<c>JavaScriptProfile.ResolveCapability</c>): does the host
    /// resolve this specifier from this module to this key? Answered from the open map's own record
    /// of what the host's <see cref="IJsModuleHost.Resolve"/> said, and no otherwise.
    /// </summary>
    internal bool ConfirmResolution(ReadOnlySpan<byte> request)
    {
        var parts = JsFormat.DecodeText(request).Split('\0');

        return parts.Length == 3 && Modules is { } map && map.Confirms(parts[0], parts[1], parts[2]);
    }

    internal sealed record ModulePermit(string Referrer, string Specifier, byte[] Artifact);

    /// <summary>Restores the enclosing module permit on every exit.</summary>
    internal readonly struct ModuleScope : IDisposable
    {
        private readonly VmSourceProvider _provider;
        private readonly ModulePermit? _previous;

        internal ModuleScope(VmSourceProvider provider, ModulePermit permit)
        {
            _provider = provider;
            _previous = provider._pendingModule;
            provider._pendingModule = permit;
        }

        /// <inheritdoc />
        public void Dispose() => _provider._pendingModule = _previous;
    }

    /// <summary>
    /// Restores the enclosing request state on every exit, including failure before a compile.
    /// The enclosing scope's recorded diagnostic is restored with it.
    /// </summary>
    internal readonly struct RequestScope : IDisposable
    {
        private readonly VmSourceProvider _provider;
        private readonly bool _previousAwaiting;
        private readonly SliceSourceDiagnostic? _previousFailure;

        internal RequestScope(VmSourceProvider provider)
        {
            _provider = provider;
            _previousAwaiting = provider._awaitingRequested;
            _previousFailure = provider._requestedFailure;
            provider._awaitingRequested = true;
            provider._requestedFailure = null;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _provider._awaitingRequested = _previousAwaiting;
            _provider._requestedFailure = _previousFailure;
        }
    }
}
