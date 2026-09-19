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
internal sealed partial class BroilerJsRealm
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

        if (!_allowGuestEval)
            throw new JsCapabilityUnavailableException(EngineName, JsCapabilities.GuestEval);

        return Evaluate(source, label);
    }

    private JsValue Evaluate(string source, string label)
    {
        using var scope = Enter();

        try
        {
            return BroilerJsMarshal.Wrap(_context.Eval(source, label));
        }
        catch (JSException engineException)
        {
            throw Translate(engineException);
        }
    }

    /// <summary>
    /// <c>ForceStrictMode</c>, expressed the only way this engine offers, and applied to the source
    /// THIS REPOSITORY authored rather than to everything the realm evaluates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Broiler.JS has no realm-wide "everything is strict" switch â€” <c>JSContextOptions</c> carries
    /// none â€” so the directive is prepended to the source instead, which is what the language itself
    /// says makes a script strict.
    /// </para>
    /// <para>
    /// <b>It is applied by the caller rather than inside <c>Evaluate</c>, and the difference is a
    /// specification one.</b> It used to sit in the shared helper, so every member forced it â€” the
    /// page's own evaluations included. An indirect <c>eval</c> evaluates a NEW script whose
    /// strictness comes from its own source, so a host that forced it strict would make one page
    /// behave differently here than anywhere else. Only <see cref="EvaluateHostScript"/> applies it
    /// now; <see cref="EvaluateClassicScript"/> and <see cref="EvaluateDynamicSource"/> evaluate the
    /// source as given.
    /// </para>
    /// <para>
    /// <c>docs/vm-javascript-profile.md</c> states the <c>eval</c> half of that rule, but what it
    /// measures is the script engines' <c>StrictModeEnabled</c>, not this option: its Broiler.JS row
    /// is <c>ScriptEngine</c>, whose parsed document scripts are made strict by its own
    /// <c>PrepareSource</c> and never pass through <see cref="IJsSource"/>. A classic script handed to
    /// this realm is not forced strict â€” an inserted script element or a frame's, on that same page â€”
    /// and no caller outside the tests sets <c>ForceStrictMode</c>. What measures this
    /// realm is the conformance suite's
    /// <c>ForcedStrictModeReachesHostScriptAndNotWhatThePageEvaluates</c>, which asserts the host and
    /// dynamic members and not the classic one.
    /// </para>
    /// <para>
    /// The two providers disagreed in OPPOSITE directions and nothing pinned either: this one forced
    /// strict on both of the members the contract then had, and Broiler.VM forced it on neither,
    /// because its <c>ForceStrictMode</c> reached only the bootstrap unit and never a compile the
    /// source provider answered. Both are corrected together, and the conformance suite now asks.
    /// </para>
    /// <para>
    /// <b>No newline, deliberately.</b> The prologue goes on the same line as the source's own first
    /// line so that every line number a stack frame reports is the line the host handed in. A
    /// <c>\n</c> here would shift every reported line by one, which is the kind of defect that is
    /// invisible until someone is reading a stack trace at the moment they can least afford a wrong
    /// answer. Column one of line one moves; nothing else does.
    /// </para>
    /// </remarks>
    private string ApplyStrictMode(string source) =>
        _forceStrictMode ? "\"use strict\";" + source : source;
}

