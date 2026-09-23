using System.Diagnostics.CodeAnalysis;

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
/// is a load failure), reads each module's static requests, and refuses the graphs the pinned engine
/// would run wrongly (see <see cref="BroilerJsModuleAnalysis"/>). Evaluation, the exports objects and
/// the binding of imported names are the engine's: <see cref="BroilerJsModuleContext"/> only answers
/// which already-loaded module a request names.
/// </para>
/// <para>
/// <b>Module code is compiled strict.</b> The pinned engine compiles module text as the sloppy body
/// of a function, and has no strictness option, so the map prepends <c>"use strict";</c> to the text
/// it hands the engine, after a hashbang line if there is one, which is how this provider already
/// forces strictness on host source. Line numbers are unchanged; engine-reported columns on the
/// directive's line shift by its 13 characters.
/// </para>
/// <para>
/// <b>Top-level var and function declarations are global properties on the pinned engine.</b> Two
/// modules declaring the same such name would share one binding, and a module reading a name it does
/// not declare would see another module's. Link refuses a graph in which a fresh module's
/// <see cref="BroilerJsModuleAnalysis.TopLevelVarNames"/> meet another module's, or another module's
/// <see cref="BroilerJsModuleAnalysis.FreeNames"/>, or a property the global object already has. That
/// the names are visible as properties of <c>globalThis</c> at all is a reported gap, not refusable.
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

    // Linked modules' top-level var-scoped names and free names, for the global-binding refusal in Link.
    private readonly Dictionary<string, Record> _topLevelVarOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Record> _freeNameReaders = new(StringComparer.Ordinal);
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
            var strictText = StrictModuleText(source.Text!);

            if (_realm.CompileModule(strictText, internalName, label) is { } syntaxError)
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
                    // Analyzed as the host wrote it, so the directive is not a statement before its imports.
                    record.Analysis = BroilerJsModuleAnalysis.Analyze(source.Text!);
                    record.Source = strictText;
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

    /// <summary>
    /// The text the engine compiles: the host's, with a <c>"use strict";</c> directive on its first
    /// line, or on the line after a hashbang, which must stay first. See the class remarks.
    /// </summary>
    internal static string StrictModuleText(string text)
    {
        const string Directive = "\"use strict\";";
        if (!text.StartsWith("#!", StringComparison.Ordinal))
            return Directive + text;

        var end = text.IndexOfAny(['\n', '\r', '\u2028', '\u2029']);
        if (end < 0)
            return text + "\n" + Directive;

        end += text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1;
        return text[..end] + Directive + text[end..];
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
    /// Checks the loaded graph and makes it visible. The engine links nothing ahead of evaluation, so
    /// this is where the graphs it would run wrongly are refused; see <see cref="BroilerJsModuleAnalysis"/>.
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

        if (FindCycle(fresh) is { } cycle)
        {
            graph.Fail(graph.Failure(cycle[0], JsModulePhase.Link,
                $"The module graph is cyclic ({string.Join(" -> ", cycle.Select(record => record.Label))}), and Broiler.JS " +
                "0.1.0-preview.1 has neither live bindings nor a cross-module temporal dead zone, so a cycle would read " +
                "copies taken before the other module ran. Refused until the upstream Broiler.JS module-semantics slice lands.",
                cycle[0].Label));
            return;
        }

        foreach (var record in fresh)
        {
            if (record.Analysis!.Refusal is { } reason)
            {
                graph.Fail(graph.Failure(record, JsModulePhase.Link,
                    $"'{record.Label}' is refused: {reason}. Refused until the upstream Broiler.JS module-semantics slice lands.",
                    record.Label));
                return;
            }

            if (SharedGlobalBinding(record, fresh) is { } shared)
            {
                graph.Fail(graph.Failure(record, JsModulePhase.Link,
                    $"'{record.Label}' is refused: {shared}, and Broiler.JS 0.1.0-preview.1 binds a module's top-level var and " +
                    "function declarations on the realm's global object, so they would share one binding. Refused until the " +
                    "upstream Broiler.JS module-semantics slice lands.",
                    record.Label));
                return;
            }

            // The engine awaits each dependency where its import declaration stands, so an importer
            // waits for an asynchronous dependency before it even starts the next one. ECMAScript starts
            // the later siblings while the first waits. Only the last request may be asynchronous.
            var requests = record.Analysis.Requests;
            for (var i = 0; i < requests.Count - 1; i++)
            {
                var dependency = record.Requested[requests[i].Specifier];
                if (IsAsyncGraph(dependency, []))
                {
                    graph.Fail(graph.Failure(record, JsModulePhase.Link,
                        $"'{record.Label}' imports '{dependency.Label}', which waits on top-level await, before another " +
                        "module, and Broiler.JS 0.1.0-preview.1 would not start that module until the wait ends. Refused " +
                        "until the upstream Broiler.JS module-semantics slice lands.",
                        record.Label));
                    return;
                }
            }
        }

        foreach (var record in fresh)
        {
            record.Linked = true;
            foreach (var name in record.Analysis!.TopLevelVarNames)
                _topLevelVarOwners.TryAdd(name, record);
            foreach (var name in record.Analysis.FreeNames)
                _freeNameReaders.TryAdd(name, record);
        }

        graph.Complete();
    }

    /// <summary>
    /// Why <paramref name="record"/>'s top-level var-scoped names would meet another module's names,
    /// or the global object's, on the pinned engine's global object; or null.
    /// </summary>
    private string? SharedGlobalBinding(Record record, List<Record> fresh)
    {
        var analysis = record.Analysis!;
        foreach (var name in analysis.TopLevelVarNames)
        {
            var owner = _topLevelVarOwners.GetValueOrDefault(name)
                ?? fresh.FirstOrDefault(other => other != record && other.Analysis!.TopLevelVarNames.Contains(name));
            if (owner is not null)
                return $"it declares the top-level var or function '{name}', which '{owner.Label}' also declares";

            var reader = _freeNameReaders.GetValueOrDefault(name)
                ?? fresh.FirstOrDefault(other => other != record && other.Analysis!.FreeNames.Contains(name));
            if (reader is not null)
                return $"it declares the top-level var or function '{name}', which '{reader.Label}' reads without declaring it";

            if (_realm.HasProperty(_realm.Global, name))
                return $"it declares the top-level var or function '{name}', which the realm's global object already has";
        }

        foreach (var name in analysis.FreeNames)
        {
            if (_topLevelVarOwners.TryGetValue(name, out var owner))
                return $"it reads '{name}' without declaring it, and '{owner.Label}' declares it as a top-level var or function";
        }

        return null;
    }

    /// <summary>Whether <paramref name="record"/> or anything it imports uses top-level await. The graph is acyclic here.</summary>
    private static bool IsAsyncGraph(Record record, HashSet<Record> visited) =>
        visited.Add(record)
        && (record.Analysis!.HasTopLevelAwait || record.Requested.Values.Any(dependency => IsAsyncGraph(dependency, visited)));

    /// <summary>A cycle among <paramref name="records"/>, as a path that starts and ends at the same record, or null.</summary>
    private static List<Record>? FindCycle(List<Record> records)
    {
        var state = new Dictionary<Record, bool>(); // false: on the stack, true: done
        var path = new List<Record>();

        foreach (var record in records)
        {
            if (Visit(record) is { } cycle)
                return cycle;
        }

        return null;

        List<Record>? Visit(Record record)
        {
            if (record.Linked)
                return null;

            if (state.TryGetValue(record, out var done))
                return done ? null : [.. path.Skip(path.IndexOf(record)), record];

            state[record] = false;
            path.Add(record);
            foreach (var dependency in record.Requested.Values)
            {
                if (Visit(dependency) is { } cycle)
                    return cycle;
            }

            path.RemoveAt(path.Count - 1);
            state[record] = true;
            return null;
        }
    }

    // ── evaluation ──────────────────────────────────────────────────────────────────────────────

    /// <summary>What the engine's loader calls for each <c>import</c> a module body reaches.</summary>
    internal Task<JSValue> ImportForEngine(string? referrerName, string specifier)
    {
        if (IsUnusable)
            return Task.FromException<JSValue>(_realm.ModuleTypeError("the module map was disposed"));

        if (referrerName is null
            || !_byInternalName.TryGetValue(referrerName, out var referrer)
            || !referrer.Requested.TryGetValue(specifier, out var dependency))
        {
            return Task.FromException<JSValue>(_realm.ModuleTypeError(
                $"'{specifier}' is not a static import of this module; import() is not routed through the module contract on the pinned Broiler.JS engine"));
        }

        return StartEvaluation(dependency);
    }

    /// <summary>
    /// The one evaluation of <paramref name="record"/>, started on first use and shared by every
    /// importer afterwards, including one that arrives while it is still running or after it failed.
    /// </summary>
    /// <remarks>
    /// Shared rather than re-requested from the engine because the engine answers a second request
    /// for a module it has started with that module's exports as they stand, whether or not its body
    /// has finished or thrown. Every caller is entered and on this realm's scheduler.
    /// </remarks>
    internal Task<JSValue> StartEvaluation(Record record)
    {
        if (record.EvaluationTask is { } started)
            return started;

        // Only a cycle could import a module while its own evaluation is starting, and cycles are
        // refused at link time; a second engine request here would read unfinished exports.
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

            if (EvaluationTask is null)
            {
                throw new InvalidOperationException(
                    $"'{SourceLabel}' has not started evaluating, and Broiler.JS 0.1.0-preview.1 creates a module's " +
                    "namespace only when its evaluation starts. Call Evaluate first; the upstream Broiler.JS " +
                    "module-semantics slice removes this limitation.");
            }

            _namespace = map._realm.EngineNamespace(InternalName!)
                ?? throw new InvalidOperationException($"The engine has no module compiled as '{InternalName}'.");
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
