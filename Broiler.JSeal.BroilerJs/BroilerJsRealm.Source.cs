using Broiler.JavaScript.Runtime;

namespace Broiler.JSeal.BroilerJs;

/// <summary>
/// <see cref="IJsSource"/>: turning JavaScript source into something that runs, and keeping apart the
/// three cases the page's Content-Security-Policy treats differently.
/// </summary>
/// <remarks>
/// <b>Each reaches the same <c>JSContext.Eval</c>, and that is the point rather than a shortcut.</b>
/// Broiler.JS carries a run-time compiler and cannot tell them apart â€” nothing in the engine
/// distinguishes them, because the distinction is not the engine's. It is the host's, and it is which
/// directive governs the source rather than who wrote it. <see cref="EvaluateHostScript"/> runs
/// source this repository authored and is exempt from the page's policy.
/// <see cref="EvaluateClassicScript"/> runs a classic script the page carries, unconditionally,
/// because the <c>script-src</c> decision is its caller's to take, and every caller but
/// <c>JSWorker</c> takes it before calling. Only
/// <see cref="EvaluateDynamicSource"/> is refused here, when the realm was built without
/// <see cref="JsCapabilities.GuestEval"/>. The page's own <c>eval</c>, <c>Function</c> at every arity
/// (with its async, generator and async-generator siblings) and <c>ShadowRealm.prototype.evaluate</c>
/// do not go through these members: <c>RefuseGuestCompilation</c> in <c>BroilerJsRealm.cs</c> refuses
/// them inside the realm, on the event the engine raises before each one compiles. An engine with no
/// run-time compiler would implement the members
/// differently â€” compiling host script when it is built, and lacking both
/// <see cref="JsCapabilities.ClassicScriptSource"/> and <see cref="JsCapabilities.GuestEval"/>,
/// because a page's text is not knowable then â€” and the contract is shaped so that it can.
/// </remarks>
internal partial class BroilerJsRealm
{
    /// <inheritdoc />
    public JsValue EvaluateHostScript(string source, string label)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Evaluate(ApplyStrictMode(source), label);
    }

    /// <summary>
    /// Runs a classic script the page carries.
    /// </summary>
    /// <remarks>
    /// <b>Unconditional, and its being three lines is the design rather than a shortcut.</b> This
    /// engine carries a run-time compiler, so the ABILITY the capability names is never in doubt
    /// here; and the PERMISSION that governs a script element is <c>script-src</c>, which is the
    /// caller's to decide before it calls, and every caller but <c>JSWorker</c> does. There is nothing
    /// left for this method to check. On an engine that
    /// compiles ahead of time the same member would be the one that could not be written, which is
    /// why the capability exists at all.
    /// </remarks>
    public JsValue EvaluateClassicScript(string source, string label)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Evaluate(source, label);
    }

    /// <summary>
    /// Runs JavaScript the page asks to evaluate at run time, on the page's behalf.
    /// </summary>
    /// <remarks>
    /// The refusal is a capability failure rather than an engine one: the page asked for something
    /// this realm was deliberately built without, which is a different fact from the source being
    /// wrong. A host that read a restrictive Content-Security-Policy and passed
    /// <c>AllowGuestEval: false</c> gets it here, at the host member. The page's own <c>eval</c>,
    /// <c>Function</c> and <c>ShadowRealm.prototype.evaluate</c> are refused separately, inside the
    /// realm, by <c>RefuseGuestCompilation</c> in <c>BroilerJsRealm.cs</c>.
    /// </remarks>
    public JsValue EvaluateDynamicSource(string source, string label)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();

        if (!_allowGuestEval)
            throw new JsCapabilityUnavailableException(EngineName, JsCapabilities.GuestEval);

        return Evaluate(source, label);
    }

    /// <summary>Evaluates under the selected source identity, which the engine also sees.</summary>
    /// <remarks>
    /// The identity is the compiler location, so it appears in this engine's own frames. Guest
    /// throws escaping here carry it, and a parse failure in this text also carries its line when
    /// the engine's report of it can be trusted.
    /// </remarks>
    private JsValue Evaluate(string source, string label)
    {
        var sourceLabel = _options.SourceLabelFor(label);
        using var scope = Enter();

        try
        {
            return BroilerJsMarshal.Wrap(_context.Eval(source, sourceLabel));
        }
        catch (JSException engineException)
        {
            throw Translate(engineException, sourceLabel, source);
        }
    }

    /// <summary>
    /// The line of a parse failure in the evaluated text, read from the engine's compile frame, or
    /// null when that line cannot be trusted.
    /// </summary>
    /// <remarks>
    /// The pinned engine exposes no structured parse position. A parse failure's trace is its message
    /// followed by one frame, <c>at Compile:{location}:{line},{column}</c>. Run-time throws start with
    /// a different frame, and a guest <c>eval</c> or <c>Function</c> compiles under another location
    /// with a further frame after it, so only a sole compile frame under this evaluation's identity
    /// counts. Line 0 means the parser had no position, such as end of input. The frame's line is not
    /// always the failure's: a lexer failure, such as an unterminated string, leaves it at the line
    /// where the token began scanning, so when the message ends in the parser's own
    /// <c>at {line}, {column}</c> the two must agree. The pinned lexer also counts only LF and CRLF
    /// as a line break, in code, comments, strings and templates alike, while ECMAScript also counts a
    /// lone CR, U+2028 and U+2029. Text containing any of those reports no line rather than a
    /// plausible wrong one.
    /// </remarks>
    private static int? CompileFailureLine(string message, string? trace, string sourceLabel, string source)
    {
        if (trace is null || !trace.StartsWith(message, StringComparison.Ordinal) || HasMiscountedLines(source))
            return null;

        var frame = trace.AsSpan(message.Length).Trim();
        var prefix = "at Compile:" + sourceLabel + ":";

        if (!frame.StartsWith(prefix, StringComparison.Ordinal) || frame.IndexOfAny('\r', '\n') >= 0)
            return null;

        frame = frame[prefix.Length..];
        var comma = frame.IndexOf(',');

        if (comma <= 0 || !int.TryParse(frame[..comma], out var line) || line <= 0)
            return null;

        return ReportedParserLine(message) is not { } reported || reported == line ? line : null;
    }

    /// <summary>The line of a message ending in the parser's <c> at {line}, {column}</c>.</summary>
    private static int? ReportedParserLine(string message)
    {
        var text = message.AsSpan().TrimEnd();
        var comma = text.LastIndexOf(", ", StringComparison.Ordinal);
        if (comma < 0 || !int.TryParse(text[(comma + 2)..], out _))
            return null;

        var at = text[..comma].LastIndexOf(" at ", StringComparison.Ordinal);
        return at >= 0 && int.TryParse(text[(at + 4)..comma], out var line) ? line : null;
    }

    /// <summary>
    /// Whether the text holds a line terminator the pinned lexer does not count as ECMAScript does:
    /// a CR not followed by LF, U+2028 or U+2029, wherever it appears.
    /// </summary>
    private static bool HasMiscountedLines(string source)
    {
        for (var i = 0; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '\u2028' or '\u2029':
                    return true;
                case '\r' when i + 1 == source.Length || source[i + 1] != '\n':
                    return true;
            }
        }

        return false;
    }

    /// <summary>Apply forced strictness only to host-authored source.</summary>
    /// <remarks>
    /// The pinned engine has no strictness option, so the directive is prepended on the first source
    /// line. Line numbers survive; engine-reported columns on that first line shift by the directive's
    /// 13 characters, which is why this provider reports no <see cref="JsEngineException.SourceColumn"/>.
    /// Classic scripts and dynamic source bypass this helper. Labels are diagnostic metadata, not
    /// authorization tokens.
    /// </remarks>
    private string ApplyStrictMode(string source) =>
        _forceStrictMode ? "\"use strict\";" + source : source;
}

