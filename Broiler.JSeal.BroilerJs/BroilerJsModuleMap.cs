using System.Diagnostics.CodeAnalysis;

using Broiler.JavaScript.Modules;
using Broiler.JavaScript.Runtime;

namespace Broiler.JSeal.BroilerJs;

/// <summary>
/// The I10 module map over Broiler.JS: host resolution and loading, and the static graph handed to the
/// engine's own module loader for evaluation.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the map does and what the engine does.</b> The map resolves every specifier through the
/// host, loads each key once, compiles each text as a module before anything runs (so a parse error
/// is a load failure), reads each module's static requests, and refuses a module that calls
/// <c>import()</c> (see <see cref="BroilerJsModuleAnalysis"/>). Everything ECMAScript calls linking
/// and evaluation is the engine's: Broiler.JS 0.1.0-preview.3 links the graph (a missing or ambiguous
/// export is its SyntaxError), binds imports live, gives each module its own strict scope, handles
/// cycles and top-level await, and caches evaluation errors. <see cref="BroilerJsModuleContext"/>
/// only answers which already-loaded module a request names, from what the host said.
/// </para>
/// <para>
/// <b>The engine links when an evaluation starts, not when the map links.</b> The map's Link phase
/// makes a loaded graph visible; the engine loads and links it from the map's records when
/// <see cref="Record.Evaluate"/> first starts it, so a link error surfaces as that evaluation's
/// rejection rather than as a <see cref="LoadAsync"/> failure.
/// </para>
/// <para>
/// <b>Everything here runs on the realm's thread.</b> Host loads complete wherever the host completes
/// them, and are applied only in a task handed to <see cref="IJsModuleHost.QueueRealmTask"/>, even when
/// the host's value task has already completed. Evaluation settles only through the realm's job queue.
/// </para>
/// </remarks>
internal sealed class BroilerJsModuleMap : IJsModuleMap
{
    private readonly BroilerJsRealm _realm;
    private readonly IJsModuleHost _host;
    private readonly JsModuleOptions _options;
    private readonly Dictionary<string, Record> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Record> _byInternalName = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Referrer, string Specifier), JsModuleKey> _resolutions = [];
    private readonly HashSet<GraphLoad> _graphs = [];

    private readonly CancellationTokenSource _cancellation = new();
    private int _fetching;
    private bool _disposed;

    internal BroilerJsModuleMap(BroilerJsRealm realm, IJsModuleHost host, JsModuleOptions options)
    {
        _realm = realm;
        _host = host;
        _options = options;
    }

    /// <inheritdoc />
    public bool HasPendingLoads
    {
        get
        {
            ThrowIfUnusable();
            return _fetching > 0;
        }
    }

    /// <inheritdoc />
    public ValueTask<IJsModule> LoadAsync(string specifier, JsModuleReferrer referrer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specifier);
        ThrowIfUnusable();

        var graph = new GraphLoad(this, specifier, referrer);
        _graphs.Add(graph);

        var request = new JsModuleRequest(referrer, specifier, [], JsModuleRequestKind.HostRoot);
        if (TryResolve(graph, request, out var key) && TryGetOrCreate(graph, key, specifier, referrer, out var root))
        {
            graph.Root = root;

            if (root.Linked)
                graph.Complete();
            else
                Start(graph, root, specifier, referrer);
        }

        var completion = graph.Completion.Task;
        return new ValueTask<IJsModule>(cancellationToken.CanBeCanceled ? completion.WaitAsync(cancellationToken) : completion);
    }

    /// <inheritdoc />
    public bool TryGetModule(JsModuleKey key, [NotNullWhen(true)] out IJsModule? module)
    {
        ThrowIfUnusable();

        if (key.Value is not null && _records.TryGetValue(key.Value, out var record) && record.Linked)
        {
            module = record;
            return true;
        }

        module = null;
        return false;
    }

    /// <summary>
    /// Cancels outstanding loads, fails pending host waits, and rejects pending evaluation promises
    /// with a TypeError through the job queue.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        Abandon();

        if (_realm.IsDisposed)
            return;

        foreach (var record in _records.Values)
        {
            if (record.Linked && record.EvaluationPending)
            {
                var pending = record;
                _realm.EnqueueModuleJob(() => pending.RejectForDisposal(
                    _realm.ModuleTypeError("the module map was disposed before this module finished evaluating")));
            }
        }
    }

    /// <summary>Realm disposal: abandon loads and settle nothing.</summary>
    internal void DisposeWithRealm()
    {
        if (!_disposed)
            Abandon();
    }

    private void Abandon()
    {
        _disposed = true;
        _cancellation.Cancel();

        foreach (var graph in _graphs.ToArray())
            graph.Fail(new ObjectDisposedException(nameof(IJsModuleMap), "The module map was disposed while this graph was loading."));
    }

    internal bool IsUnusable => _disposed || _realm.IsDisposed;

    internal void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(IsUnusable, this);
        _realm.ThrowIfInModuleHost();
    }

    /// <summary>The loaded text of a module, by the internal name the engine knows it by.</summary>
    internal string? SourceFor(string internalName) =>
        _byInternalName.TryGetValue(internalName, out var record) ? record.Source : null;

    /// <summary>
    /// What the engine's loader resolves a request to: a linked root by its internal name, and a
    /// module's static request by the record the host resolved it to when the graph was loaded.
    /// </summary>
    internal string? ResolveForEngine(string? referrerName, string specifier)
    {
        if (referrerName is null)
            return _byInternalName.TryGetValue(specifier, out var root) && root.Linked ? specifier : null;

        return _byInternalName.TryGetValue(referrerName, out var referrer)
            && referrer.Requested.TryGetValue(specifier, out var dependency)
            && dependency.Linked
                ? dependency.InternalName
                : null;
    }

    // ── resolution and loading ──────────────────────────────────────────────────────────────────

    private static string ReferrerIdentity(JsModuleReferrer referrer) => referrer.Kind switch
    {
        JsModuleReferrerKind.Module => "module\0" + referrer.Key?.Value,
        JsModuleReferrerKind.Script => "script\0" + referrer.Label,
        _ => "host\0" + referrer.Key?.Value,
    };

    /// <summary>
    /// Resolves through the cache or the host. A failure fails <paramref name="graph"/> and is not cached.
    /// </summary>
    private bool TryResolve(GraphLoad graph, JsModuleRequest request, out JsModuleKey key)
    {
        key = default;

        if (request.Attributes.Count > 0)
        {
            graph.Fail(new JsModuleException(
                $"'{request.Specifier}' from {request.Referrer} carries import attributes, and this module map supports none (UnsupportedAttributes).",
                JsModulePhase.Resolve, request.Specifier, request.Referrer));
            return false;
        }

        var identity = (ReferrerIdentity(request.Referrer), request.Specifier);
        if (_resolutions.TryGetValue(identity, out key))
            return true;

        JsModuleResolution resolution;
        try
        {
            using (_realm.EnterModuleHost())
                resolution = _host.Resolve(in request);
        }
        catch (Exception hostFailure)
        {
            graph.Fail(new JsModuleException(
                $"The host's Resolve threw for '{request.Specifier}' from {request.Referrer}: {hostFailure.Message}",
                JsModulePhase.Resolve, request.Specifier, request.Referrer, innerException: hostFailure));
            return false;
        }

        if (!resolution.IsResolved || resolution.Key.Value is null)
        {
            graph.Fail(new JsModuleException(
                $"'{request.Specifier}' from {request.Referrer} did not resolve ({resolution.Failure}): {resolution.Message}",
                JsModulePhase.Resolve, request.Specifier, request.Referrer));
            return false;
        }

        key = resolution.Key;
        _resolutions[identity] = key;
        return true;
    }

    /// <summary>
    /// The record for <paramref name="key"/>, created if new. A new record that would take the map
    /// past <see cref="JsModuleOptions.MaxModules"/> fails <paramref name="graph"/> in the Load phase
    /// instead, before the host is asked for it, so a graph that keeps growing stops.
    /// </summary>
    private bool TryGetOrCreate(GraphLoad graph, JsModuleKey key, string specifier, JsModuleReferrer referrer, [NotNullWhen(true)] out Record? record)
    {
        if (_records.TryGetValue(key.Value, out record))
            return true;

        if (_records.Count >= _options.MaxModules)
        {
            graph.Fail(new JsModuleException(
                $"Loading '{key}' would hold more than MaxModules ({_options.MaxModules}) modules in this map.",
                JsModulePhase.Load, specifier, referrer, key));
            return false;
        }

        _records[key.Value] = record = new Record(this, key);
        return true;
    }

    private void Start(GraphLoad graph, Record record, string specifier, JsModuleReferrer referrer)
    {
        if (graph.Finished || !graph.Members.Add(record))
            return;

        graph.Origins[record] = (specifier, referrer);
        graph.Outstanding++;

        if (record.Analysis is not null)
        {
            OnFetched(graph, record);
            return;
        }

        (record.Waiting ??= []).Add(graph);
        if (!record.Fetching)
            BeginFetch(record);
    }

    private void BeginFetch(Record record)
    {
        record.Fetching = true;
        _fetching++;

        ValueTask<JsModuleSource> pending;
        try
        {
            using (_realm.EnterModuleHost())
                pending = _host.LoadAsync(record.Key, _cancellation.Token);
        }
        catch (Exception hostFailure)
        {
            pending = ValueTask.FromException<JsModuleSource>(hostFailure);
        }

        // Always applied in a realm task, even when already complete, so ordering never depends on
        // how the host completed it.
        pending.AsTask().ContinueWith(
            completed =>
            {
                try
                {
                    _host.QueueRealmTask(() => ApplyFetch(record, completed));
                }
                catch (Exception)
                {
                    // A host that can no longer queue work is being torn down; nothing is left to apply to.
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ApplyFetch(Record record, Task<JsModuleSource> completed)
    {
        // A late completion after disposal does nothing, and does not throw.
        if (IsUnusable)
            return;

        record.Fetching = false;
        _fetching--;
        var waiting = record.Waiting ?? [];
        record.Waiting = null;

        string? failure = null;
        Exception? inner = null;
        var phase = JsModulePhase.Load;
        string? label = null;
        var thrown = JsValue.Missing;

        if (!completed.IsCompletedSuccessfully)
        {
            inner = completed.Exception?.InnerException ?? new OperationCanceledException();
            failure = $"The host's LoadAsync failed for '{record.Key}': {inner.Message}";
        }
        else if (completed.Result is { IsFailed: true } refused)
        {
            failure = $"'{record.Key}' could not be loaded ({refused.Failure}): {refused.Message}";
        }
        else
        {
            var source = completed.Result;
            label = string.IsNullOrEmpty(source.Label) ? record.Key.Value : source.Label;
            var internalName = InternalNameFor(label);

            if (_realm.CompileModule(source.Text!, internalName, label) is { } syntaxError)
            {
                phase = JsModulePhase.Parse;
                failure = $"'{label}' is not a valid module: {syntaxError.Message}";
                inner = syntaxError;
                thrown = syntaxError.Thrown;
            }
            else
            {
                try
                {
                    record.Analysis = BroilerJsModuleAnalysis.Analyze(source.Text!);
                    record.Source = source.Text;
                    record.Label = label;
                    record.InternalName = internalName;
                    _byInternalName[internalName] = record;
                }
                catch (Exception analysisFailure)
                {
                    phase = JsModulePhase.Parse;
                    failure = $"'{label}' could not be analyzed as a module: {analysisFailure.Message}";
                    inner = analysisFailure;
                }
            }
        }

        if (failure is not null)
        {
            // Failed loads are not cached: the next request calls the host again.
            _records.Remove(record.Key.Value);
            foreach (var graph in waiting)
                graph.Fail(graph.Failure(record, phase, failure, label, thrown, inner));

            return;
        }

        foreach (var graph in waiting)
            OnFetched(graph, record);
    }

    /// <summary>A name for the engine unique in this map, never one of its built-in module names.</summary>
    private string InternalNameFor(string label)
    {
        var name = label;
        for (var suffix = 2; name is "module" or "clr" || _byInternalName.ContainsKey(name); suffix++)
            name = $"{label} #{suffix}";

        return name;
    }

    private void OnFetched(GraphLoad graph, Record record)
    {
        if (graph.Finished)
            return;

        var referrer = JsModuleReferrer.Module(record.Key);
        foreach (var requested in record.Analysis!.Requests)
        {
            var request = new JsModuleRequest(referrer, requested.Specifier, requested.Attributes, JsModuleRequestKind.Static);
            if (!TryResolve(graph, request, out var key))
                return;

            if (!TryGetOrCreate(graph, key, requested.Specifier, referrer, out var dependency))
                return;

            record.Requested[requested.Specifier] = dependency;

            if (!dependency.Linked)
                Start(graph, dependency, requested.Specifier, referrer);

            if (graph.Finished)
                return;
        }

        if (--graph.Outstanding == 0)
            Link(graph);
    }

    /// <summary>
    /// Checks the loaded graph against the map's options and the adapter's one refusal, and makes it
    /// visible. The engine's own linking happens when an evaluation of the graph starts.
    /// </summary>
    private void Link(GraphLoad graph)
    {
        var fresh = graph.Members.Where(record => !record.Linked).ToList();
        var linked = _records.Values.Count(record => record.Linked);

        if (linked + fresh.Count > _options.MaxModules)
        {
            graph.Fail(graph.Failure(graph.Root!, JsModulePhase.Load,
                $"Linking '{graph.Root!.Key}' would hold {linked + fresh.Count} modules, more than MaxModules ({_options.MaxModules}).",
                graph.Root!.Label));
            return;
        }

        foreach (var record in fresh)
        {
            if (record.Analysis!.Refusal is { } reason)
            {
                graph.Fail(graph.Failure(record, JsModulePhase.Link, $"'{record.Label}' is refused: {reason}.", record.Label));
                return;
            }
        }

        foreach (var record in fresh)
            record.Linked = true;

        graph.Complete();
    }

    // ── evaluation ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What <c>JSModuleContext.LoadModuleAsync</c> is asked for by anything other than
    /// <see cref="BroilerJsModuleContext.LoadThroughEngine"/>: a module's static requests, answered
    /// with the evaluation of the record the host resolved. The engine's own graph walk does not come
    /// here; it resolves through <see cref="ResolveForEngine"/>.
    /// </summary>
    internal Task<JSValue> ImportForEngine(string? referrerName, string specifier)
    {
        if (IsUnusable)
            return Task.FromException<JSValue>(_realm.ModuleTypeError("the module map was disposed"));

        if (referrerName is null
            || !_byInternalName.TryGetValue(referrerName, out var referrer)
            || !referrer.Requested.TryGetValue(specifier, out var dependency))
        {
            return Task.FromException<JSValue>(_realm.ModuleTypeError(
                $"'{specifier}' is not a static import of this module; import() is not routed through the module contract by the Broiler.JS adapter"));
        }

        return StartEvaluation(dependency);
    }

    /// <summary>
    /// The one evaluation of <paramref name="record"/>, started on first use and shared by every
    /// importer afterwards, including one that arrives while it is still running or after it failed.
    /// </summary>
    /// <remarks>
    /// Shared rather than re-requested from the engine so that the record's status and settlement have
    /// one source. The engine itself caches a module's evaluation and its error, so a later request -
    /// such as <see cref="JoinEngineOutcomes"/> makes for a dependency - answers with that outcome.
    /// Every caller is entered and on this realm's scheduler.
    /// </remarks>
    internal Task<JSValue> StartEvaluation(Record record)
    {
        if (record.EvaluationTask is { } started)
            return started;

        // The engine defers the start of a requested evaluation to a job, so nothing can ask for this
        // record again while the request is being made; refused rather than started twice if it does.
        if (record.Starting)
        {
            return Task.FromException<JSValue>(_realm.ModuleTypeError(
                $"'{record.Label}' was imported while its evaluation was starting"));
        }

        record.Status = JsModuleStatus.Evaluating;
        record.Starting = true;
        Task<JSValue> task;
        try
        {
            task = _realm.ModuleContext!.LoadThroughEngine(record.InternalName!);
        }
        finally
        {
            record.Starting = false;
        }

        record.EvaluationTask = task;

        if (!task.IsCompleted)
            record.Status = JsModuleStatus.EvaluatingAsync;

        task.ContinueWith(
            finished => _realm.EnqueueModuleJob(() => record.Finish(finished)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return task;
    }

    /// <summary>
    /// Joins the outcome of every module the engine evaluated as part of another module's graph.
    /// </summary>
    /// <remarks>
    /// The engine evaluates a whole graph from its root, so a dependency's own record is told nothing.
    /// A second request for a module the engine has evaluated answers with its cached outcome - its
    /// evaluation error included - so each such record starts its own (joined) evaluation here, and
    /// settles through the job queue like any other. A module the walk never reached stays linked.
    /// Runs as a job, after a record finished.
    /// </remarks>
    private void JoinEngineOutcomes()
    {
        foreach (var record in _records.Values)
        {
            if (record is { Linked: true, EvaluationTask: null, InternalName: { } name }
                && _realm.EngineModule(name) is { Status: ModuleStatus.Evaluated })
            {
                _realm.InModuleTurn(() => StartEvaluation(record));
            }
        }
    }

    // ── records ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>One module record, which is also the host's <see cref="IJsModule"/> once linked.</summary>
    internal sealed class Record(BroilerJsModuleMap map, JsModuleKey key) : IJsModule
    {
        private JsModuleStatus _status = JsModuleStatus.Linked;
        private JsValue _promise;
        private Action<JsValue>? _resolve;
        private Action<JsValue>? _reject;
        private JsValue _error;
        private JsValue? _namespace;
        private bool _settled;

        public JsModuleKey Key { get; } = key;

        public string SourceLabel => Label ?? Key.Value;

        internal string? Label { get; set; }
        internal string? InternalName { get; set; }
        internal string? Source { get; set; }
        internal BroilerJsModuleAnalysis? Analysis { get; set; }
        internal Dictionary<string, Record> Requested { get; } = new(StringComparer.Ordinal);
        internal bool Fetching { get; set; }
        internal List<GraphLoad>? Waiting { get; set; }
        internal bool Linked { get; set; }
        internal Task<JSValue>? EvaluationTask { get; set; }
        internal bool Starting { get; set; }

        internal bool EvaluationPending =>
            !_settled && _status is JsModuleStatus.Evaluating or JsModuleStatus.EvaluatingAsync;

        public JsModuleStatus Status
        {
            get
            {
                map.ThrowIfUnusable();

                // Evaluated by the engine as part of another module's graph, and not joined yet.
                if (EvaluationTask is null && InternalName is { } name)
                {
                    switch (map._realm.EngineModule(name)?.Status)
                    {
                        case ModuleStatus.Evaluating:
                            return JsModuleStatus.Evaluating;
                        case ModuleStatus.EvaluatingAsync:
                            return JsModuleStatus.EvaluatingAsync;
                    }
                }

                return _status;
            }

            internal set => _status = value;
        }

        public JsValue EvaluationError
        {
            get
            {
                map.ThrowIfUnusable();
                return _status == JsModuleStatus.Errored ? _error : JsValue.Missing;
            }
        }

        public JsValue Evaluate()
        {
            map.ThrowIfUnusable();

            if (!_promise.IsMissing)
                return _promise;

            return map._realm.InModuleTurn(() =>
            {
                map.StartEvaluation(this);
                _promise = map._realm.NewPromise(out var resolve, out var reject);
                _resolve = resolve;
                _reject = reject;

                // Already finished (through an earlier import): settle in a job, never inline.
                if (_status is JsModuleStatus.Evaluated or JsModuleStatus.Errored)
                    map._realm.EnqueueModuleJob(Settle);

                return _promise;
            });
        }

        public JsValue GetNamespace()
        {
            map.ThrowIfUnusable();

            if (_namespace is { } known)
                return known;

            _namespace = map._realm.EngineNamespace(InternalName!)
                ?? throw new InvalidOperationException(
                    $"'{SourceLabel}' has not been linked by the engine, and Broiler.JS links a module only when an " +
                    "evaluation of its graph starts. Call Evaluate on it, or on a module that imports it, first.");
            return _namespace.Value;
        }

        /// <summary>Records the engine's outcome and settles the evaluation promise; runs as a job.</summary>
        internal void Finish(Task<JSValue> finished)
        {
            if (map.IsUnusable || _settled)
                return;

            if (finished.IsCompletedSuccessfully)
            {
                _namespace ??= BroilerJsMarshal.Wrap(finished.Result);
                _status = JsModuleStatus.Evaluated;
            }
            else
            {
                _error = map._realm.ThrownBy(finished);
                _status = JsModuleStatus.Errored;
            }

            if (!_promise.IsMissing)
                Settle();

            map.JoinEngineOutcomes();
        }

        private void Settle()
        {
            if (_settled || map.IsUnusable)
                return;

            _settled = true;
            if (_status == JsModuleStatus.Errored)
                _reject!(_error);
            else
                _resolve!(JsValue.Undefined);
        }

        internal void RejectForDisposal(Exception typeError)
        {
            if (_settled || _promise.IsMissing || map._realm.IsDisposed)
                return;

            _settled = true;
            _reject!(map._realm.ThrownValueOf(typeError));
        }
    }

    /// <summary>One <see cref="LoadAsync"/> call in progress.</summary>
    internal sealed class GraphLoad(BroilerJsModuleMap map, string specifier, JsModuleReferrer referrer)
    {
        internal TaskCompletionSource<IJsModule> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Record? Root { get; set; }
        internal HashSet<Record> Members { get; } = [];
        internal Dictionary<Record, (string Specifier, JsModuleReferrer Referrer)> Origins { get; } = [];
        internal int Outstanding { get; set; }
        internal bool Finished { get; private set; }

        internal void Complete()
        {
            if (Finished)
                return;

            Finished = true;
            map._graphs.Remove(this);
            Completion.TrySetResult(Root!);
        }

        internal void Fail(Exception failure)
        {
            if (Finished)
                return;

            Finished = true;
            map._graphs.Remove(this);
            Completion.TrySetException(failure);
        }

        /// <summary>A failure attributed to the request in this graph that named <paramref name="record"/>.</summary>
        internal JsModuleException Failure(
            Record record, JsModulePhase phase, string message, string? label, JsValue thrown = default, Exception? inner = null)
        {
            var (requestSpecifier, requestReferrer) = Origins.TryGetValue(record, out var origin) ? origin : (specifier, referrer);
            return new JsModuleException(message, phase, requestSpecifier, requestReferrer, record.Key, label, thrown, inner);
        }
    }
}
