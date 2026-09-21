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
        ThrowIfDisposed();

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

    /// <summary>Apply forced strictness only to host-authored source.</summary>
    /// <remarks>
    /// The directive stays on the first source line to preserve subsequent line numbers. Classic scripts
    /// and dynamic source bypass this helper. Source-authorization tests pin these boundaries on both
    /// providers; labels are diagnostic metadata, not authorization tokens.
    /// </remarks>
    private string ApplyStrictMode(string source) =>
        _forceStrictMode ? "\"use strict\";" + source : source;
}

