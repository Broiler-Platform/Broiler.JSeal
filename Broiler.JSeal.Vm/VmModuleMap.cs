using System.Diagnostics.CodeAnalysis;

using Broiler.VM.Profile.JavaScript;
using Broiler.VM.Profile.JavaScript.Compiler;

namespace Broiler.JSeal.Vm;

/// <summary>
/// The I11 module map over Broiler.VM: host resolution and loading in JSEAL, and linking, identity
/// and evaluation in the VM's own module graph (VM JSD-0024 section 15).
/// </summary>
/// <remarks>
/// <para>
/// <b>What the map does and what the VM does.</b> The map asks the host to resolve every specifier
/// and to load every key once, checks each text under the module goal as it arrives (so a parse
/// error is a load failure that loads nothing further), and records each module's resolutions. When
/// a graph's sources are all present it compiles them, under the module goal, into one artifact whose
/// root is the requested module, and asks the realm to link it with <c>JsHostRealm.LoadModule</c>.
/// The realm's source provider answers that one module request with that artifact, and the profile's
/// resolver question is answered from the same recorded resolutions. Linking, the realm-wide module
/// registry, namespaces, live bindings, cycles, top-level <c>await</c> and cached evaluation errors
/// are the VM's; nothing here links or evaluates a module.
/// </para>
/// <para>
/// <b>A handle per linked module.</b> The profile answers a <c>JsHostModule</c> only for the root of
/// an artifact, so every other module of a newly linked graph is asked for once more with an
/// artifact rooted at it; the realm adopts the instance it already holds under that key, so the
/// handle, the namespace and the instance are the ones the graph linked. That costs one compilation
/// per module, each of the graph under it, and is recorded, not optimised. A guest <c>import()</c>
/// compiles the graph under its root once, however often that root is imported.
/// </para>
/// <para>
/// <b>Compilation is the host's work, outside the realm's fuel.</b> Checking a text as it arrives and
/// compiling a graph are JSEAL calls into the VM's compiler, not guest execution, so no allowance of
/// the realm is charged for them; what bounds them is <see cref="JsModuleOptions.MaxModules"/> and
/// what the host's <see cref="IJsModuleHost.LoadAsync"/> agrees to supply.
/// </para>
/// <para>
/// <b>Status is read from the realm, not inferred.</b> The profile answers the specification's
/// <c>[[Status]]</c>, <c>[[EvaluationError]]</c>, <c>[[HasTLA]]</c> and <c>[[CycleRoot]]</c> for
/// every module it holds (JSD-0024 section 20.1), so <see cref="StatusOf"/> asks it, and nothing
/// here watches evaluation promises, times a job against a walk or reads a module's text. Only
/// JSEAL's own rule is applied on top: <see cref="JsModuleStatus.Evaluated"/> means the evaluation
/// promise fulfilled, so a cycle member whose root is still awaiting is
/// <see cref="JsModuleStatus.EvaluatingAsync"/>.
/// </para>
/// <para>
/// <b>Everything here runs on the realm's thread.</b> Host loads complete wherever the host completes
/// them and are applied only in a task handed to <see cref="IJsModuleHost.QueueRealmTask"/>, even when
/// the host's value task has already completed.
/// </para>
/// </remarks>
internal sealed class VmModuleMap : IJsModuleMap
{
    private readonly VmRealm _realm;
    private readonly IJsModuleHost _host;
    private readonly JsModuleOptions _options;
    private readonly CancellationTokenSource _cancel = new();

    private readonly Dictionary<string, ModuleSource> _sources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fetching = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VmModule> _modules = new(StringComparer.Ordinal);
    private readonly List<GraphLoad> _graphs = [];
    private readonly Dictionary<string, byte[]> _importArtifacts = new(StringComparer.Ordinal);

    private bool _disposed;

    internal VmModuleMap(VmRealm realm, IJsModuleHost host, JsModuleOptions options)
    {
        _realm = realm;
        _host = host;
        _options = options;
    }

    /// <inheritdoc />
    public bool HasPendingLoads => _fetching.Count != 0;

    /// <summary>Whether the map or its realm is gone, so a module answers only its key and label.</summary>
    internal bool IsClosed => _disposed || _realm.IsDisposed;

    /// <inheritdoc />
    public ValueTask<IJsModule> LoadAsync(string specifier, JsModuleReferrer referrer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specifier);
        ThrowIfDisposed();
        _realm.ThrowIfInModuleHost();

        var resolution = Resolve(new JsModuleRequest(referrer, specifier, null, JsModuleRequestKind.HostRoot), out var threw);

        if (!resolution.IsResolved)
            return ValueTask.FromException<IJsModule>(ResolveFailure(specifier, referrer, resolution, threw));

        var graph = new GraphLoad(resolution.Key.Value, specifier, referrer, request: null);

        if (cancellationToken.CanBeCanceled)
            cancellationToken.Register(() => graph.Completion.TrySetCanceled(cancellationToken));

        Start(graph);
        return new ValueTask<IJsModule>(graph.Completion.Task);
    }

    /// <inheritdoc />
    public bool TryGetModule(JsModuleKey key, [NotNullWhen(true)] out IJsModule? module)
    {
        ThrowIfDisposed();
        _realm.ThrowIfInModuleHost();

        if (key.Value is not null && _modules.TryGetValue(key.Value, out var found))
        {
            module = found;
            return true;
        }

        module = null;
        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Cancels every outstanding host load, releases every host waiter with
    /// <see cref="ObjectDisposedException"/>, and detaches the map from the realm: a later guest
    /// <c>import()</c> is refused as a module nobody can find, and a load the host completes late is
    /// ignored. A guest <c>import()</c> the map had deferred is rejected with a <c>TypeError</c> while
    /// the realm is still alive; when the realm itself is being disposed nothing is settled, because
    /// that would run guest reactions during teardown.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancel.Cancel();

        var imports = new List<JsHostModuleRequest>();

        foreach (var graph in _graphs)
        {
            if (graph.Request is { } request)
                imports.Add(request);
            else
                graph.Completion.TrySetException(new ObjectDisposedException(nameof(IJsModuleMap)));
        }

        if (imports.Count != 0 && !_realm.IsDisposed)
        {
            _realm.InStep(realm =>
            {
                foreach (var request in imports)
                    realm.FailModuleRequest(request, JsHostErrorKind.TypeError, "the module map was disposed before the import was loaded");
            });
        }

        _graphs.Clear();
        _fetching.Clear();

        if (_realm.Sources.Modules == this)
            _realm.Sources.Modules = null;

        if (_realm.Bridge is VmModuleHostBridge bridge && bridge.Loader == this)
            bridge.Loader = null;
    }

    // ── guest import() (I12) ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes a guest <c>import()</c> the profile offered, inside the guest's step: resolves it now,
    /// loads its graph through the host, and completes or fails it later from a realm task.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The referrer is what the calling code was compiled with.</b> A module's code carries its
    /// key; a host or classic script's carries its label, tagged by the source provider
    /// (<see cref="VmSourceProvider.ScriptReferrer"/>) so that it cannot be read as a module key, and
    /// is sent to the host as <see cref="JsModuleReferrer.Script"/> with that label.
    /// </para>
    /// <para>
    /// <b>Refusals stay the profile's.</b> When the map is closed, when the options forbid dynamic
    /// import, or when a script is calling and the options forbid imports from scripts, the import is
    /// answered <see cref="JsHostModuleLoad.Now"/>: the provider holds no permit for it, so it
    /// rejects with the realm's own <c>TypeError</c>. So is an import whose code carries neither,
    /// which is code the profile can attach to no script and no module at all: rather than hand the
    /// host a referrer that is not the caller's, the map leaves the import to the profile. Eval code
    /// and a function the <c>Function</c> constructor made are not such code: the profile compiles
    /// them with the referrer <c>GetActiveScriptOrModule</c> gives them, so they arrive here with the
    /// calling module's key or the calling script's label.
    /// Guest-evaluation permission is not consulted; module loading is not <c>eval</c>.
    /// </para>
    /// </remarks>
    internal JsHostModuleLoad OfferImport(JsHostModuleRequest request)
    {
        if (IsClosed || !_options.AllowDynamicImport)
            return JsHostModuleLoad.Now;

        // A module of this map is calling - linked already, or still being linked by the graph
        // whose evaluation reached this import - or a script is, or code with no referrer is.
        JsModuleReferrer referrer;

        if (_sources.ContainsKey(request.Referrer))
            referrer = JsModuleReferrer.Module(new JsModuleKey(request.Referrer));
        else if (VmSourceProvider.TryReadScriptReferrer(request.Referrer, out var label) && _options.AllowImportFromScripts)
            referrer = JsModuleReferrer.Script(label);
        else
            return JsHostModuleLoad.Now;

        var resolution = Resolve(new JsModuleRequest(referrer, request.Specifier, null, JsModuleRequestKind.Dynamic), out var threw);

        if (!resolution.IsResolved)
        {
            var failure = ResolveFailure(request.Specifier, referrer, resolution, threw);
            QueueRealmTask(() => FailImport(request, JsHostErrorKind.TypeError, failure.Message));
            return JsHostModuleLoad.Deferred;
        }

        Start(new GraphLoad(resolution.Key.Value, request.Specifier, referrer, request));
        return JsHostModuleLoad.Deferred;
    }

    private void FailImport(JsHostModuleRequest request, JsHostErrorKind kind, string message)
    {
        if (IsClosed)
            return;

        _realm.InStep(realm => realm.FailModuleRequest(request, kind, message));
    }

    // ── loading ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a graph and starts the loads it needs. It never links inline: a graph whose sources
    /// are all present already is linked from a realm task, so every load completes in one.
    /// </summary>
    private void Start(GraphLoad graph)
    {
        _graphs.Add(graph);

        if (Advance(graph, link: false))
            QueueRealmTask(Pump);
    }

    /// <summary>Advances every graph after a load was applied.</summary>
    private void Pump()
    {
        if (IsClosed)
            return;

        foreach (var graph in _graphs.ToArray())
            Advance(graph, link: true);
    }

    /// <summary>
    /// Walks one graph over the sources present: starts the loads it lacks, resolves each module's
    /// requests once, and fails it at the first failure. Answers whether it is ready to link, and
    /// links it when <paramref name="link"/> allows.
    /// </summary>
    private bool Advance(GraphLoad graph, bool link)
    {
        if (graph.Completion.Task.IsCompleted && graph.Request is null)
        {
            // A waiter that cancelled leaves nothing to do.
            _graphs.Remove(graph);
            return false;
        }

        graph.Edges.Clear();
        graph.Edges[graph.RootKey] = (graph.Specifier, graph.Referrer);
        graph.Awaiting.Clear();

        var order = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(graph.RootKey);
        var seen = new HashSet<string>(StringComparer.Ordinal) { graph.RootKey };

        while (queue.Count != 0)
        {
            var key = queue.Dequeue();

            if (!_sources.TryGetValue(key, out var source))
            {
                graph.Awaiting.Add(key);

                if (!_fetching.Contains(key) && !Fetch(graph, key))
                    return false;

                continue;
            }

            if (source.Failure is { } parse)
            {
                Fail(graph, parse(graph.Edges[key]));
                return false;
            }

            order.Add(key);

            foreach (var specifier in source.Specifiers)
            {
                if (!source.Resolutions.TryGetValue(specifier, out var target))
                {
                    var referrer = JsModuleReferrer.Module(new JsModuleKey(key));
                    var resolution = Resolve(new JsModuleRequest(referrer, specifier, null, JsModuleRequestKind.Static), out var threw);

                    if (!resolution.IsResolved)
                    {
                        Fail(graph, ResolveFailure(specifier, referrer, resolution, threw));
                        return false;
                    }

                    target = source.Resolutions[specifier] = resolution.Key.Value;
                }

                if (seen.Add(target))
                {
                    graph.Edges[target] = (specifier, JsModuleReferrer.Module(new JsModuleKey(key)));
                    queue.Enqueue(target);
                }
            }
        }

        if (graph.Awaiting.Count != 0)
            return false;

        if (link)
            Link(graph, order);

        return true;
    }

    /// <summary>Starts the host's load of <paramref name="key"/>; false when the map is full.</summary>
    private bool Fetch(GraphLoad graph, string key)
    {
        if (_sources.Count + _fetching.Count >= _options.MaxModules)
        {
            var (specifier, referrer) = graph.Edges[key];
            Fail(graph, new JsModuleException(
                $"The module map holds its maximum of {_options.MaxModules} modules, so '{key}' cannot be loaded.",
                JsModulePhase.Load, specifier, referrer, new JsModuleKey(key)));
            return false;
        }

        _fetching.Add(key);

        Task<JsModuleSource> loading;

        try
        {
            using (_realm.EnterModuleHost())
                loading = _host.LoadAsync(new JsModuleKey(key), _cancel.Token).AsTask();
        }
        catch (Exception thrown)
        {
            loading = Task.FromException<JsModuleSource>(thrown);
        }

        // APPLIED IN A REALM TASK EVEN WHEN ALREADY COMPLETE: the host's loop decides when a load
        // reaches the realm, never the thread that finished it.
        loading.ContinueWith(
            finished => QueueRealmTask(() => Apply(key, finished)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return true;
    }

    /// <summary>Applies one finished host load, then advances every graph.</summary>
    private void Apply(string key, Task<JsModuleSource> finished)
    {
        if (IsClosed || !_fetching.Remove(key))
            return;

        string? failure = null;

        if (!finished.IsCompletedSuccessfully)
            failure = finished.Exception?.InnerException?.Message ?? "the host's load was cancelled";
        else if (finished.Result.IsFailed)
            failure = $"{finished.Result.Failure}: {finished.Result.Message}";

        if (failure is not null)
        {
            // NOT CACHED: every graph waiting for this key fails, and a later load asks the host again.
            foreach (var graph in _graphs.ToArray())
            {
                if (!graph.Awaiting.Contains(key))
                    continue;

                var (specifier, referrer) = graph.Edges[key];
                Fail(graph, new JsModuleException(
                    $"The host could not load '{key}' (requested as '{specifier}' by {referrer}): {failure}",
                    JsModulePhase.Load, specifier, referrer, new JsModuleKey(key),
                    innerException: finished.Exception?.InnerException));
            }
        }
        else
        {
            _sources[key] = Check(key, finished.Result);
        }

        Pump();
    }

    /// <summary>
    /// Reads a source's static requests and checks it under the module goal, alone, before any of its
    /// dependencies is resolved or loaded.
    /// </summary>
    /// <remarks>
    /// <b>An import attribute is a resolution failure of the request that carries it.</b> The VM's
    /// request list does not carry attributes and its front end declines every one as
    /// <c>UnsupportedImportAttribute</c>, positioned at the declaration. The specifier named there is
    /// the first string literal of that declaration that is one of the module's requests; a
    /// declaration where that reading is not certain (an escaped literal, no match) stays the
    /// module's parse failure.
    /// </remarks>
    private ModuleSource Check(string key, JsModuleSource loaded)
    {
        var text = loaded.Text!;
        var label = loaded.Label ?? key;
        var requests = JsCompiler.Requests(text, SliceParseOptions.Module);
        var diagnostic = requests.Succeeded ? null : requests.Diagnostics.FirstOrDefault();

        if (diagnostic is null)
        {
            var alone = JsCompiler.Compile([], [new JsModuleUnit(key, text, SliceParseOptions.Module, [])], new JsCompileRequest());

            if (!alone.Succeeded)
                diagnostic = alone.Diagnostics.FirstOrDefault();
        }

        var source = new ModuleSource(key, text, label, requests.Succeeded ? requests.Specifiers : []);

        if (!requests.Succeeded || diagnostic is not null)
        {
            if (diagnostic is { Code: SliceSourceDiagnosticCode.UnsupportedImportAttribute } &&
                AttributedSpecifier(text, diagnostic, source.Specifiers) is { } attributed)
            {
                source.Failure = _ => new JsModuleException(
                    $"'{attributed}' in '{label}' carries an import attribute: {JsModuleFailure.UnsupportedAttributes}. {diagnostic.Message}",
                    JsModulePhase.Resolve, attributed, JsModuleReferrer.Module(new JsModuleKey(key)));
            }
            else
            {
                source.Failure = edge => ParseFailure(key, label, diagnostic, edge);
            }
        }

        return source;
    }

    private JsModuleException ParseFailure(string key, string label, SliceSourceDiagnostic? diagnostic, (string Specifier, JsModuleReferrer Referrer) edge)
    {
        var detail = diagnostic?.ToString() ?? "the module goal refused it";
        var message = $"'{label}' is not a module this profile admits: {detail}";
        var thrown = _realm.InStep(realm => VmMarshal.Wrap(realm.Error(JsHostErrorKind.SyntaxError, message).Thrown));
        var syntax = new JsEngineException(message, thrown)
        {
            SourceLabel = label,
            SourceLine = diagnostic?.Line,
            SourceColumn = diagnostic?.Column,
        };

        return new JsModuleException(message, JsModulePhase.Parse, edge.Specifier, edge.Referrer, new JsModuleKey(key), label, thrown, syntax);
    }

    /// <summary>The first request-valued string literal at or after the diagnostic's position.</summary>
    private static string? AttributedSpecifier(string text, SliceSourceDiagnostic diagnostic, IReadOnlyList<string> specifiers)
    {
        var offset = 0;

        for (var line = 1; line < diagnostic.Line && offset < text.Length; offset++)
        {
            var c = text[offset];

            if (c is '\n' or '\u2028' or '\u2029' || (c == '\r' && (offset + 1 >= text.Length || text[offset + 1] != '\n')))
                line++;
        }

        offset = Math.Min(text.Length, offset + Math.Max(0, diagnostic.Column - 1));

        for (var literals = 0; literals < 3 && offset < text.Length; offset++)
        {
            var quote = text[offset];

            if (quote is not ('\'' or '"'))
            {
                if (quote == ';')
                    return null;

                continue;
            }

            var end = text.IndexOf(quote, offset + 1);

            if (end < 0)
                return null;

            var literal = text[(offset + 1)..end];

            if (literal.Contains('\\'))
                return null;

            if (specifiers.Contains(literal))
                return literal;

            literals++;
            offset = end;
        }

        return null;
    }

    // ── linking ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Links a graph whose sources are all present: one artifact rooted at the requested module, then
    /// a handle for every module of it the map does not hold yet.
    /// </summary>
    private void Link(GraphLoad graph, List<string> order)
    {
        _graphs.Remove(graph);

        if (graph.Request is { } request)
        {
            LinkImport(graph, request, order);
            return;
        }

        if (graph.Completion.Task.IsCompleted)
            return;

        try
        {
            var root = _realm.InStep(realm =>
            {
                var linked = Handle(realm, graph.RootKey, referrer: string.Empty, specifier: graph.RootKey);
                Adopt(realm, order);
                return linked;
            });

            graph.Completion.TrySetResult(root);
        }
        catch (JsEngineException failure)
        {
            var label = _sources[graph.RootKey].Label;
            graph.Completion.TrySetException(new JsModuleException(
                $"The graph rooted at '{graph.RootKey}' does not link: {failure.Message}",
                JsModulePhase.Link, graph.Specifier, graph.Referrer, new JsModuleKey(graph.RootKey), label, failure.Thrown, failure));
        }
    }

    /// <summary>
    /// Completes a deferred guest <c>import()</c>: the profile makes the same module request the
    /// import made, answered with this graph, links it, evaluates it and settles the import through
    /// the job queue. A graph that does not link rejects the import there, as a <c>SyntaxError</c>.
    /// </summary>
    private void LinkImport(GraphLoad graph, JsHostModuleRequest request, List<string> order)
    {
        if (IsClosed)
            return;

        _realm.InStep(realm =>
        {
            // COMPILED ONCE PER IMPORTED ROOT: a module's text and resolutions never change once
            // the map holds them, so neither does the graph under it, and a guest that imports one
            // specifier in a loop costs the host one compilation rather than one per call.
            if (!_importArtifacts.TryGetValue(graph.RootKey, out var artifact))
                _importArtifacts[graph.RootKey] = artifact = Compile(graph.RootKey);

            using (_realm.Sources.EnterModule(request.Referrer, request.Specifier, artifact))
                realm.CompleteModuleRequest(request);

            try
            {
                Adopt(realm, order);
            }
            catch (JsEngineException)
            {
                // The graph did not link; the import already rejected with the realm's SyntaxError.
                return;
            }

            // The import's own walk has run as far as it can; asking its root for its evaluation
            // promise joins that walk rather than starting another, and gives the map the promise
            // a later IJsModule.Evaluate must answer with.
            StartEvaluation(realm, _modules[graph.RootKey]);
        });
    }

    /// <summary>A handle for every module of a linked graph the map does not hold yet.</summary>
    private void Adopt(JsHostRealm realm, List<string> order)
    {
        foreach (var key in order)
        {
            if (!_modules.ContainsKey(key))
                Handle(realm, key, referrer: string.Empty, specifier: key);
        }
    }

    /// <summary>Links (or adopts) the graph rooted at <paramref name="key"/> and records its handle.</summary>
    private VmModule Handle(JsHostRealm realm, string key, string referrer, string specifier)
    {
        if (_modules.TryGetValue(key, out var known))
            return known;

        JsHostModule linked;

        try
        {
            using (_realm.Sources.EnterModule(referrer, specifier, Compile(key)))
                linked = realm.LoadModule(specifier, referrer);
        }
        catch (JsHostThrowException thrown)
        {
            throw new JsEngineException(thrown.Message, VmMarshal.Wrap(thrown.Thrown), thrown)
            {
                SourceLabel = _sources[key].Label,
            };
        }

        var module = new VmModule(this, key, _sources[key].Label, linked);
        _modules[key] = module;
        return module;
    }

    /// <summary>The artifact of the graph rooted at <paramref name="rootKey"/>: every reachable module, root first.</summary>
    private byte[] Compile(string rootKey)
    {
        var units = new List<JsModuleUnit>();
        var queue = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { rootKey };
        queue.Enqueue(rootKey);

        while (queue.Count != 0)
        {
            var source = _sources[queue.Dequeue()];
            var resolutions = new List<JsResolvedRequest>();

            foreach (var specifier in source.Specifiers)
            {
                var target = source.Resolutions[specifier];
                resolutions.Add(new JsResolvedRequest(specifier, target));

                if (seen.Add(target))
                    queue.Enqueue(target);
            }

            units.Add(new JsModuleUnit(source.Key, source.Text, SliceParseOptions.Module, resolutions));
        }

        var compiled = JsCompiler.Compile([], units, new JsCompileRequest());

        if (!compiled.Succeeded || compiled.Artifact is null)
        {
            throw new JsEngineException(
                $"the graph rooted at '{rootKey}' did not compile: {compiled.Diagnostics.FirstOrDefault()}");
        }

        return compiled.Artifact;
    }

    /// <summary>The profile's resolver question, answered from the resolutions the host gave.</summary>
    internal bool Confirms(string referrer, string specifier, string key) =>
        !IsClosed &&
        _sources.TryGetValue(referrer, out var source) &&
        source.Resolutions.TryGetValue(specifier, out var resolved) &&
        string.Equals(resolved, key, StringComparison.Ordinal);

    // ── evaluation ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Starts (or joins) a module's evaluation and answers its evaluation promise.</summary>
    internal JsValue Evaluate(VmModule module) =>
        _realm.InStep(realm => VmMarshal.Wrap(StartEvaluation(realm, module)));

    /// <summary>The module's own evaluation promise, asked for once, with one reaction on it.</summary>
    /// <remarks>
    /// The profile answers the same promise however often it is asked, so the map keeps it for
    /// <see cref="IJsModule.Evaluate"/>. The reaction carries no state: it only tells the map that
    /// this evaluation's settlement has been delivered, which is what the contract's
    /// <see cref="JsModuleStatus.Evaluated"/> waits for (<see cref="StatusOf"/>).
    /// </remarks>
    private JsHostValue StartEvaluation(JsHostRealm realm, VmModule root)
    {
        if (!root.Promise.IsMissing)
            return root.Promise;

        // Refused before anything runs: an evaluation whose settlement the map cannot observe would
        // leave every module of its graph EvaluatingAsync for good.
        var then = PromiseThen();

        var covered = new List<VmModule>();

        foreach (var key in Closure(root.Key.Value))
        {
            if (_modules.TryGetValue(key, out var member))
            {
                member.Covering++;
                covered.Add(member);
            }
        }

        try
        {
            var promise = root.Promise = realm.EvaluateModule(root.Handle);
            Observe(realm, then, promise, () => Release(covered));
            return promise;
        }
        catch
        {
            // Nothing will deliver a settlement now, so the cover ends here rather than leave every
            // module of this graph EvaluatingAsync for the rest of the realm's life.
            Release(covered);
            throw;
        }
    }

    /// <summary>Ends the cover one settled evaluation held over its graph.</summary>
    private static void Release(List<VmModule> covered)
    {
        foreach (var member in covered)
            member.Covering--;

        covered.Clear();
    }

    /// <summary>Attaches one host reaction, for either settlement, through the captured intrinsic <c>then</c>.</summary>
    private static void Observe(JsHostRealm realm, JsHostValue then, JsHostValue promise, Action settled)
    {
        var reaction = realm.NewMethod(string.Empty, (JsHostRealm _, JsHostValue _, ReadOnlySpan<JsHostValue> _) =>
        {
            settled();
            return JsHostValue.Undefined;
        }, 1);

        realm.Invoke(then, promise, [reaction, reaction]);
    }

    /// <summary>The intrinsic <c>Promise.prototype.then</c> captured at realm creation.</summary>
    /// <exception cref="JsEngineException">The realm has none: the map refuses to start what it cannot observe.</exception>
    private JsHostValue PromiseThen()
    {
        var then = ((VmModuleHostBridge)_realm.Bridge).PromiseThen;

        if (then.Kind is not JsHostValueKind.Function)
        {
            throw new JsEngineException(
                "The realm had no intrinsic Promise.prototype.then when it was created, so the module map cannot observe a " +
                "module's evaluation; nothing was evaluated.");
        }

        return then;
    }

    /// <summary>Every key reachable from <paramref name="key"/> through recorded resolutions.</summary>
    private IEnumerable<string> Closure(string key)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { key };
        var queue = new Queue<string>();
        queue.Enqueue(key);

        while (queue.Count != 0)
        {
            var next = queue.Dequeue();
            yield return next;

            if (!_sources.TryGetValue(next, out var source))
                continue;

            foreach (var target in source.Resolutions.Values)
            {
                if (seen.Add(target))
                    queue.Enqueue(target);
            }
        }
    }

    /// <summary>
    /// A module's state, read from the realm (VM JSD-0024 section 20.1) rather than inferred.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The profile holds the specification's <c>[[Status]]</c>, <c>[[EvaluationError]]</c> (the
    /// identical value every later evaluation rejects with) and <c>[[CycleRoot]]</c> on every module
    /// instance and answers them by key, so nothing here watches evaluation promises, times a marker
    /// job against a walk, or reads a module's text to guess whether it can await.
    /// </para>
    /// <para>
    /// <b>One mapping is JSEAL's own.</b> <see cref="JsModuleStatus.Evaluated"/> means the module's
    /// evaluation promise fulfilled, while the language marks a cycle member evaluated as soon as its
    /// own body ran, whatever its cycle root - which owns that promise - goes on to do. Such a member
    /// takes its answer from the root (<see cref="FromCycleRoot"/>): it is
    /// <see cref="JsModuleStatus.EvaluatingAsync"/> while the root is awaiting and
    /// <see cref="JsModuleStatus.Errored"/> when the root ended with an evaluation error, which is
    /// what its graph's promise says.
    /// </para>
    /// <para>
    /// A key the realm holds no module of is <see cref="JsModuleStatus.Linked"/>: the map asks only
    /// for a module the realm linked, and the specification's <c>unlinked</c> and <c>linking</c> are
    /// never observable (linking runs no guest or host code).
    /// </para>
    /// </remarks>
    internal JsModuleStatus StatusOf(VmModule module) =>
        _realm.InStep(realm => Classify(realm, module.Key.Value, module.Covering, out _));

    /// <summary>The value a module's evaluation threw, or Missing unless it is errored.</summary>
    internal JsValue ErrorOf(VmModule module) =>
        _realm.InStep(realm =>
            Classify(realm, module.Key.Value, module.Covering, out var error) is JsModuleStatus.Errored
                ? VmMarshal.Wrap(error)
                : JsValue.Missing);

    /// <summary>The contract's status for one key; see <see cref="StatusOf"/>.</summary>
    private static JsModuleStatus Classify(JsHostRealm realm, string key, int covering, out JsHostValue error)
    {
        error = JsHostValue.Missing;

        if (!realm.TryGetModuleState(key, out var state))
            return JsModuleStatus.Linked;

        switch (state.Status)
        {
            case JsHostModuleStatus.Evaluating:
                return JsModuleStatus.Evaluating;

            case JsHostModuleStatus.EvaluatingAsync:
                return JsModuleStatus.EvaluatingAsync;

            case JsHostModuleStatus.Evaluated when covering > 0:
                // The language has finished the module, but the evaluation that finished it has not
                // delivered its settlement yet, and the contract's Evaluated waits for that.
                return JsModuleStatus.EvaluatingAsync;

            case JsHostModuleStatus.Evaluated when state.HasEvaluationError:
                error = state.EvaluationError;
                return JsModuleStatus.Errored;

            case JsHostModuleStatus.Evaluated:
                return FromCycleRoot(realm, key, state, out error);

            default:
                return JsModuleStatus.Linked;
        }
    }

    /// <summary>
    /// What an evaluated module takes from its cycle root, which owns the evaluation promise
    /// <see cref="IJsModule.Evaluate"/> answers for every member of the cycle.
    /// </summary>
    /// <remarks>
    /// The language marks a member of a cycle <c>evaluated</c> as soon as its own body ran, and
    /// leaves its <c>[[EvaluationError]]</c> empty even when the cycle's evaluation later fails,
    /// because a rejection travels only along <c>[[AsyncParentModules]]</c>. ModuleEvaluate sends
    /// such a member to its <c>[[CycleRoot]]</c>, so the root's promise is the member's: while the
    /// root is awaiting, the member is <see cref="JsModuleStatus.EvaluatingAsync"/>, and when the
    /// root ended with an evaluation error, the member is <see cref="JsModuleStatus.Errored"/> with
    /// that value - the one its own <see cref="IJsModule.Evaluate"/> rejects with.
    /// </remarks>
    private static JsModuleStatus FromCycleRoot(JsHostRealm realm, string key, JsHostModuleState state, out JsHostValue error)
    {
        error = JsHostValue.Missing;

        if (state.CycleRoot is not { Length: > 0 } root ||
            string.Equals(root, key, StringComparison.Ordinal) ||
            !realm.TryGetModuleState(root, out var rootState))
        {
            return JsModuleStatus.Evaluated;
        }

        switch (rootState.Status)
        {
            case JsHostModuleStatus.EvaluatingAsync:
                return JsModuleStatus.EvaluatingAsync;

            case JsHostModuleStatus.Evaluated when rootState.HasEvaluationError:
                error = rootState.EvaluationError;
                return JsModuleStatus.Errored;

            default:
                return JsModuleStatus.Evaluated;
        }
    }

    // ── host calls ─────────────────────────────────────────────────────────────────────────────

    private JsModuleResolution Resolve(in JsModuleRequest request, out Exception? threw)
    {
        threw = null;

        try
        {
            using (_realm.EnterModuleHost())
                return _host.Resolve(in request);
        }
        catch (Exception exception)
        {
            threw = exception;
            return JsModuleResolution.Failed(JsModuleFailure.Refused, exception.Message);
        }
    }

    private static JsModuleException ResolveFailure(string specifier, JsModuleReferrer referrer, JsModuleResolution resolution, Exception? threw) =>
        new(threw is null
                ? $"The host did not resolve '{specifier}' from {referrer}: {resolution.Failure}: {resolution.Message}"
                : $"The host's Resolve threw for '{specifier}' from {referrer}: {threw.Message}",
            JsModulePhase.Resolve, specifier, referrer, innerException: threw);

    private void QueueRealmTask(Action task) =>
        _host.QueueRealmTask(() =>
        {
            if (!IsClosed)
                task();
        });

    /// <summary>
    /// Ends a graph with <paramref name="failure"/>: a host waiter sees the exception, and a deferred
    /// guest <c>import()</c> is rejected from a realm task - a <c>SyntaxError</c> for a module that
    /// did not parse, a <c>TypeError</c> for one nobody could resolve or load, as the profile's own
    /// undeferred import rejects.
    /// </summary>
    private void Fail(GraphLoad graph, JsModuleException failure)
    {
        _graphs.Remove(graph);

        if (graph.Request is not { } request)
        {
            graph.Completion.TrySetException(failure);
            return;
        }

        var kind = failure.Phase is JsModulePhase.Parse ? JsHostErrorKind.SyntaxError : JsHostErrorKind.TypeError;
        QueueRealmTask(() => FailImport(request, kind, failure.Message));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsClosed, typeof(IJsModuleMap));

    /// <summary>One loaded text and what the map learned from it.</summary>
    private sealed class ModuleSource(string key, string text, string label, IReadOnlyList<string> specifiers)
    {
        public string Key { get; } = key;
        public string Text { get; } = text;
        public string Label { get; } = label;
        public IReadOnlyList<string> Specifiers { get; } = specifiers;
        public Dictionary<string, string> Resolutions { get; } = new(StringComparer.Ordinal);

        /// <summary>The failure a graph reaching this module reports, given the edge it reached it by.</summary>
        public Func<(string Specifier, JsModuleReferrer Referrer), JsModuleException>? Failure { get; set; }
    }

    /// <summary>One wait for a graph: a host root load, or a guest import the map deferred.</summary>
    private sealed class GraphLoad(string rootKey, string specifier, JsModuleReferrer referrer, JsHostModuleRequest? request)
    {
        public string RootKey { get; } = rootKey;
        public string Specifier { get; } = specifier;
        public JsModuleReferrer Referrer { get; } = referrer;
        public JsHostModuleRequest? Request { get; } = request;
        public TaskCompletionSource<IJsModule> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<string, (string Specifier, JsModuleReferrer Referrer)> Edges { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Awaiting { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>One linked module record of a <see cref="VmModuleMap"/>.</summary>
internal sealed class VmModule(VmModuleMap map, string key, string label, JsHostModule handle) : IJsModule
{
    /// <inheritdoc />
    public JsModuleKey Key { get; } = new(key);

    /// <inheritdoc />
    public string SourceLabel { get; } = label;

    internal JsHostModule Handle { get; } = handle;

    /// <summary>The module's own evaluation promise once the map asked for it, else Missing.</summary>
    internal JsHostValue Promise { get; set; } = JsHostValue.Missing;

    /// <summary>How many evaluations the map started, and has not seen settle, cover this module.</summary>
    internal int Covering { get; set; }

    /// <inheritdoc />
    /// <remarks>Read from the realm; see <see cref="VmModuleMap.StatusOf"/>.</remarks>
    public JsModuleStatus Status
    {
        get
        {
            ThrowIfClosed();
            return map.StatusOf(this);
        }
    }

    /// <inheritdoc />
    public JsValue EvaluationError
    {
        get
        {
            ThrowIfClosed();
            return map.ErrorOf(this);
        }
    }

    /// <inheritdoc />
    public JsValue Evaluate()
    {
        ThrowIfClosed();
        return map.Evaluate(this);
    }

    /// <inheritdoc />
    public JsValue GetNamespace()
    {
        ThrowIfClosed();
        return VmMarshal.Wrap(Handle.Namespace);
    }

    private void ThrowIfClosed() => ObjectDisposedException.ThrowIf(map.IsClosed, typeof(IJsModule));
}
