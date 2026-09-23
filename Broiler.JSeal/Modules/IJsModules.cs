using System.Diagnostics.CodeAnalysis;

namespace Broiler.JSeal;

/// <summary>
/// An optional realm interface: the realm can load, link and evaluate ECMAScript module graphs.
/// </summary>
/// <remarks>
/// <para>
/// Designed by I09 (<c>docs/jseal.modules.md</c>). A host checks the flag and the type together:
/// <c>realm.Capabilities.HasFlag(JsCapabilities.Modules) &amp;&amp; realm is IJsModules</c>. A realm
/// without module support implements nothing new, which is why this is a separate interface rather
/// than members of <see cref="IJsSource"/>.
/// </para>
/// <para>
/// <b>No registered provider exposes it yet.</b> Until I13 publishes <see cref="JsCapabilities.Modules"/>,
/// providers reach this interface only through an internal, test-only option, and a realm from a
/// registered provider never implements it. Adopted realms (<see cref="IJsRealmAdoption"/>) never
/// implement it, because the engine context they wrap keeps its own resolution rules.
/// </para>
/// </remarks>
public interface IJsModules
{
    /// <summary>Opens the realm's module map. At most one map per realm.</summary>
    /// <exception cref="InvalidOperationException">The realm already opened a map.</exception>
    IJsModuleMap OpenModuleMap(IJsModuleHost host, JsModuleOptions options);
}

/// <summary>The host side of a module map: resolution, source loading and the realm's task queue.</summary>
/// <remarks>
/// <see cref="Resolve"/>, and the synchronous part of <see cref="LoadAsync"/>, run on the realm's
/// thread, often while guest code or a link step is on the stack. During them the host must not call
/// any operation of the realm, the map or a module; a provider throws
/// <see cref="InvalidOperationException"/> to the host code that does. <see cref="QueueRealmTask"/>
/// may be called from anywhere, including from inside those two members.
/// </remarks>
public interface IJsModuleHost
{
    /// <summary>
    /// Maps a request to a canonical key. Synchronous and free of I/O. Every specifier reaches this
    /// member, including bare names; a provider never answers one from an engine-registered cache.
    /// </summary>
    JsModuleResolution Resolve(in JsModuleRequest request);

    /// <summary>
    /// Supplies the source for a key. Called at most once per key per map unless an earlier call
    /// failed. The token fires only when the map or the realm is disposed.
    /// </summary>
    ValueTask<JsModuleSource> LoadAsync(JsModuleKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="task"/> later on the realm's thread, serialized with other realm work and
    /// never inside another realm operation. Thread-safe. This is the host's event loop.
    /// </summary>
    void QueueRealmTask(Action task);
}

/// <summary>One realm's module records, keyed by the host's canonical keys.</summary>
public interface IJsModuleMap : IDisposable
{
    /// <summary>Resolves, loads and links the static graph rooted at <paramref name="specifier"/>. It does not evaluate.</summary>
    /// <remarks>
    /// Loads complete through <see cref="IJsModuleHost.QueueRealmTask"/>, so the returned task
    /// completes only while the host runs its queue. Failures surface as
    /// <see cref="JsModuleException"/>. Cancelling <paramref name="cancellationToken"/> abandons
    /// this wait only; the load continues for any other waiter.
    /// </remarks>
    ValueTask<IJsModule> LoadAsync(string specifier, JsModuleReferrer referrer, CancellationToken cancellationToken = default);

    /// <summary>The module for <paramref name="key"/>, when it is linked or later.</summary>
    bool TryGetModule(JsModuleKey key, [NotNullWhen(true)] out IJsModule? module);

    /// <summary>Whether any host load is still outstanding.</summary>
    bool HasPendingLoads { get; }
}

/// <summary>One linked module record.</summary>
/// <remarks>After the map or realm is disposed only <see cref="Key"/> and <see cref="SourceLabel"/> can be read.</remarks>
public interface IJsModule
{
    /// <summary>The canonical key the host resolved.</summary>
    JsModuleKey Key { get; }

    /// <summary>The label failures and stacks use; the key unless the host supplied one.</summary>
    string SourceLabel { get; }

    /// <summary>Where evaluation stands. <see cref="JsModuleStatus.Evaluated"/> only after the evaluation promise fulfils.</summary>
    JsModuleStatus Status { get; }

    /// <summary>
    /// Starts evaluation if needed and returns the module's evaluation promise, the same promise on
    /// every call. Never blocks and never drains jobs; a later <see cref="IJsJobs.DrainJobs"/> settles it.
    /// </summary>
    JsValue Evaluate();

    /// <summary>The module namespace object, the identical object on every call.</summary>
    JsValue GetNamespace();

    /// <summary>The value evaluation threw, or <see cref="JsValue.Missing"/> unless <see cref="JsModuleStatus.Errored"/>.</summary>
    JsValue EvaluationError { get; }
}
