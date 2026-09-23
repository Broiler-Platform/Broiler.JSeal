using Broiler.VM.Profile.JavaScript;

namespace Broiler.JSeal.Vm;

/// <summary>
/// <see cref="IJsSource"/>: running script this repository authored, a classic script the page
/// carries, and the source a page's <c>eval</c> and <c>new Function</c> ask for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Host and classic source is a Script, evaluated through <c>JsHostRealm.EvaluateScript</c></b>
/// (VM JSD-0024 section 16): the realm's own <c>ScriptEvaluation</c>, so a top-level <c>let</c>
/// persists for later scripts, a <c>var</c> is a non-configurable global property, and a conflicting
/// script is refused before it creates anything. It evaluates INTO the existing realm; compiling to
/// an artifact and instantiating it would build a second realm the page cannot see.
/// </para>
/// <para>
/// <b>Dynamic source is eval code, evaluated through the realm's own indirect <c>eval</c></b>, so
/// its lexical declarations live and die with that evaluation. The embedder's request and a guest's
/// are told apart by the VM's request mark, not by this class; see <see cref="VmSourceProvider"/>.
/// <see cref="EvaluateDynamicSource"/> needs <see cref="JsCapabilities.GuestEval"/>, which a realm
/// built without <c>AllowGuestEval</c> lacks, and the page's own <c>eval</c> in that realm is refused
/// inside it.
/// </para>
/// </remarks>
internal partial class VmRealm
{
    /// <inheritdoc />
    public JsValue EvaluateHostScript(string source, string label)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Evaluate(source, label, SourceKind.Host);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A classic script is the embedder's request exactly as host script is: the page supplied the
    /// text, but handing it over is the host's act, so it compiles under a policy that refuses guest
    /// evaluation. Only host script is forced strict by <c>ForceStrictMode</c>.
    /// </remarks>
    public JsValue EvaluateClassicScript(string source, string label)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();

        if ((Capabilities & JsCapabilities.ClassicScriptSource) == 0)
            throw Lacking(JsCapabilities.ClassicScriptSource);

        return Evaluate(source, label, SourceKind.Classic);
    }

    /// <inheritdoc />
    public JsValue EvaluateDynamicSource(string source, string label)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();

        if ((Capabilities & JsCapabilities.GuestEval) == 0)
            throw Lacking(JsCapabilities.GuestEval);

        return Evaluate(source, label, SourceKind.Dynamic);
    }

    /// <summary>Evaluates one text in this realm as a script, or as indirect eval code.</summary>
    /// <remarks>
    /// <para>
    /// <b>Dynamic source goes through the intrinsic <c>eval</c> captured at realm creation, NOT the
    /// one on the global here.</b> <c>eval</c> is a writable global, so reading it at this line would
    /// invoke whatever the page had assigned over it. Calling the value rather than the syntactic form
    /// is an indirect eval, which the language evaluates in global scope. See <c>VmHostBridge.Eval</c>.
    /// </para>
    /// <para>
    /// <b>Source identity is attributed here, not by the VM.</b> A guest throw escaping this call
    /// carries the selected label. When the throw is the refusal of the compilation this call
    /// requested, it also carries the front end's line and column. Those positions refer to the
    /// supplied text, because forced strictness is a compiler flag rather than a prepended
    /// directive. Text in which the front end may lose a line reports no position at all. The pinned
    /// VM supplies no guest stack, so no frames or run-time positions are reported.
    /// </para>
    /// </remarks>
    private JsValue Evaluate(string source, string label, SourceKind kind)
    {
        var sourceLabel = _options.SourceLabelFor(label);

        return InStep(realm =>
        {
            var evaluate = _bridge.Eval;

            if (kind == SourceKind.Dynamic && evaluate.Kind is not JsHostValueKind.Function)
            {
                throw new JsEngineException(
                    $"this realm has no 'eval', so '{sourceLabel}' cannot be evaluated in it");
            }

            using var scope = _sources.EnterRequest();

            try
            {
                var result = kind == SourceKind.Dynamic
                    ? realm.Invoke(evaluate, JsHostValue.Undefined, [JsHostValue.String(source)])
                    : realm.EvaluateScript(
                        source,
                        // The VM separates the name from the source with U+0000, so such a label is
                        // not sent; JSEAL attributes the label itself either way.
                        sourceLabel.Contains('\0') ? string.Empty : sourceLabel,
                        kind == SourceKind.Host && _options.ForceStrictMode);

                return VmMarshal.Wrap(result);
            }
            catch (JsHostThrowException thrown)
            {
                var failure = MayMiscountLines(source) ? null : _sources.RequestedFailure;

                throw new JsEngineException(thrown.Message, VmMarshal.Wrap(thrown.Thrown), thrown)
                {
                    SourceLabel = sourceLabel,
                    SourceLine = failure?.Line,
                    SourceColumn = failure?.Column,
                };
            }
        });
    }

    /// <summary>Which evaluation a host API asked for.</summary>
    private enum SourceKind
    {
        /// <summary>Host script: a Script, strict when <c>ForceStrictMode</c> is set.</summary>
        Host,

        /// <summary>A page's classic script: a Script under its own directive prologue.</summary>
        Classic,

        /// <summary>A page's <c>eval</c> or <c>new Function</c> text: indirect eval code.</summary>
        Dynamic
    }

    /// <summary>
    /// Whether the text may hold a line continuation inside a template literal, whose line terminator
    /// the pinned front end does not count, so a diagnostic after it names the wrong line.
    /// </summary>
    /// <remarks>
    /// Telling a template from a string or comment needs a lexer, so any backquote together with any
    /// backslash before a line terminator reports no position rather than a plausible wrong one.
    /// Continuations in strings, and CR, CRLF, U+2028 and U+2029 elsewhere, are counted as ECMAScript
    /// counts them.
    /// </remarks>
    private static bool MayMiscountLines(string source)
    {
        if (!source.Contains('`'))
            return false;

        for (var i = source.IndexOf('\\'); i >= 0 && i + 1 < source.Length; i = source.IndexOf('\\', i + 1))
        {
            if (source[i + 1] is '\r' or '\n' or '\u2028' or '\u2029')
                return true;
        }

        return false;
    }
}

