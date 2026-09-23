using Broiler.VM.Profile.JavaScript;
using Broiler.VM.Profile.JavaScript.Compiler;

namespace Broiler.JSeal.Vm;

/// <summary>
/// <see cref="IJsSource"/>: running script this repository authored, a classic script the page
/// carries, and the source a page's <c>eval</c> and <c>new Function</c> ask for.
/// </summary>
/// <remarks>
/// <para>
/// <b>All three go through the realm's own <c>eval</c>, the only thing that evaluates INTO an
/// existing realm.</b> The obvious alternative - compile the text to an artifact and instantiate it
/// - produces a second realm with its own globals, so a polyfill installed that way would be
/// installed somewhere the page cannot see. Asking the realm for its <c>eval</c> and invoking it is
/// what keeps the evaluation in the realm the caller meant.
/// </para>
/// <para>
/// <b>What separates them is marked by this provider, not carried by the profile.</b> The
/// profile's evaluation request is the source text and nothing else, so the artifact provider cannot
/// tell which kind of evaluation it is answering. Host and classic evaluations each authorize
/// one compilation through <see cref="VmSourceProvider.EnterScript"/>, immediately before calling
/// the captured intrinsic inside the realm's step. The permit is consumed before guest code runs.
/// <see cref="EvaluateDynamicSource"/> grants no permit: a realm built without
/// <c>AllowGuestEval</c> also lacks
/// <see cref="JsCapabilities.GuestEval"/>, so the member throws before evaluating, while the page's
/// own <c>eval</c> in that realm is refused inside it. The reasoning, and the gap it stands in for,
/// are on <see cref="VmSourceProvider"/>.
/// </para>
/// </remarks>
internal sealed partial class VmRealm
{
    /// <inheritdoc />
    public JsValue EvaluateHostScript(string source, string label)
    {
        ArgumentNullException.ThrowIfNull(source);

        return Evaluate(source, label, VmSourceProvider.SourceKind.Host);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>The permit is what makes this expressible on an engine with no run-time compiler.</b>
    /// Compiling here means asking a registered artifact provider, and that provider is also where a
    /// forbidden evaluation is refused. Handing over a page's script therefore authorizes exactly
    /// one compilation, just as for host script.
    /// </remarks>
    public JsValue EvaluateClassicScript(string source, string label)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();

        if ((Capabilities & JsCapabilities.ClassicScriptSource) == 0)
            throw Lacking(JsCapabilities.ClassicScriptSource);

        return Evaluate(source, label, VmSourceProvider.SourceKind.Classic);
    }

    /// <inheritdoc />
    public JsValue EvaluateDynamicSource(string source, string label)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();

        if ((Capabilities & JsCapabilities.GuestEval) == 0)
            throw Lacking(JsCapabilities.GuestEval);

        return Evaluate(source, label, VmSourceProvider.SourceKind.Guest);
    }

    /// <summary>
    /// Evaluates one text in this realm, through the realm's own indirect <c>eval</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Indirect on purpose.</b> Calling the realm's <c>eval</c> value rather than the syntactic
    /// form is an indirect eval, which the language evaluates in global scope - which is what a host
    /// asking a realm to run a script means, and what a direct eval would not do.
    /// </para>
    /// <para>
    /// <b>The intrinsic, captured at realm creation, and NOT read off the global here.</b> <c>eval</c>
    /// is a writable global, so reading it at this line invoked whatever the page had assigned over
    /// it - which could intercept the bridge's own script and spend the pending permit on the
    /// page's own source. Capturing it ensures the authorized compile is requested first. See
    /// <c>VmHostBridge.Eval</c> for the measurement, and
    /// <c>APageThatReplacesEvalCannotBorrowTheHostsPermissionToCompile</c> for the case.
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
    private JsValue Evaluate(string source, string label, VmSourceProvider.SourceKind kind)
    {
        var sourceLabel = _options.SourceLabelFor(label);

        return InStep(realm =>
        {
            var evaluate = _bridge.Eval;

            if (evaluate.Kind is not JsHostValueKind.Function)
            {
                throw new JsEngineException(
                    $"this realm has no 'eval', so '{sourceLabel}' cannot be evaluated in it");
            }

            JsHostValue[] arguments = [JsHostValue.String(source)];

            using var permit = _sources.EnterScript(kind);

            try
            {
                return VmMarshal.Wrap(realm.Invoke(evaluate, JsHostValue.Undefined, arguments));
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

