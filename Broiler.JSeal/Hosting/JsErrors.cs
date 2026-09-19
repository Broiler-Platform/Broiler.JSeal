namespace Broiler.JSeal;

/// <summary>
/// The ECMAScript error constructors a host binding raises. Anything DOM-shaped goes through
/// <see cref="IJsCalls.DomError"/> instead, because a <c>DOMException</c> carries a <c>name</c> the
/// page branches on and is not one of these.
/// </summary>
public enum JsErrorKind
{
    /// <summary>A plain <c>Error</c>.</summary>
    Error,

    /// <summary>A <c>TypeError</c> â€” by far the most common, and what WebIDL raises for a wrong argument type or an illegal invocation.</summary>
    TypeError,

    /// <summary>A <c>RangeError</c>.</summary>
    RangeError,

    /// <summary>A <c>SyntaxError</c>.</summary>
    SyntaxError,

    /// <summary>A <c>ReferenceError</c>.</summary>
    ReferenceError,
}

/// <summary>
/// A JavaScript exception crossing back into host code.
/// </summary>
/// <remarks>
/// <para>
/// A provider wraps whatever its engine throws in this, so a host <c>catch</c> can be written once
/// instead of once per engine. The bridge catches broadly today â€” every script and module evaluation
/// sits in <c>catch (Exception)</c> and is logged and skipped â€” which works but cannot tell a page's
/// <c>throw</c> apart from a bug in the bridge. Catching this can.
/// </para>
/// <para>
/// <see cref="Thrown"/> is the value the page threw, which need not be an <c>Error</c>: <c>throw 42</c>
/// and <c>throw {code: 5}</c> are both legal, and a host that reports only a message loses what the
/// page said. It is <see cref="JsValue.Missing"/> when the failure did not originate in guest code.
/// </para>
/// </remarks>
public sealed class JsEngineException : Exception
{
    /// <summary>Wraps a value the page threw.</summary>
    public JsEngineException(string message, JsValue thrown, Exception? innerException = null)
        : base(message, innerException)
    {
        Thrown = thrown;
    }

    /// <summary>Reports a failure that did not originate in guest code.</summary>
    public JsEngineException(string message, Exception? innerException = null)
        : this(message, JsValue.Missing, innerException)
    {
    }

    /// <summary>The value JavaScript threw, or <see cref="JsValue.Missing"/>.</summary>
    public JsValue Thrown { get; }

    /// <summary>
    /// The JavaScript stack at the throw, when the engine supplied one. The CLR
    /// <see cref="Exception.StackTrace"/> is the host's stack and is a different question.
    /// </summary>
    public string? ScriptStackTrace { get; init; }
}

/// <summary>
/// Thrown when a host asks a realm for something its engine declared it cannot do.
/// </summary>
/// <remarks>
/// Distinct from <see cref="JsEngineException"/> on purpose: that one means the page's code went
/// wrong, this one means the host's did. A host that branches on
/// <see cref="IJsRealm.Capabilities"/> never sees it, which is the point â€” it is the backstop for a
/// call site that forgot to.
/// </remarks>
public sealed class JsCapabilityUnavailableException : Exception
{
    /// <summary>Names the missing capability and the engine that lacks it.</summary>
    public JsCapabilityUnavailableException(string engineName, JsCapabilities missing)
        : base($"The '{engineName}' JavaScript engine does not support {missing}.")
    {
        EngineName = engineName;
        Missing = missing;
    }

    /// <summary>The engine that was asked.</summary>
    public string EngineName { get; }

    /// <summary>What it was asked for and does not have.</summary>
    public JsCapabilities Missing { get; }
}

