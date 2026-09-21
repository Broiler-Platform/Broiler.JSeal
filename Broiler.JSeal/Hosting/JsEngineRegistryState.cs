namespace Broiler.JSeal;

// Separate storage lets registry tests exercise empty/reset states without changing the
// process-wide providers used to discover and run conformance tests.
internal sealed class JsEngineRegistryState
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IJsEngineProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private string? _defaultName;

    internal void Register(IJsEngineProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        // Read provider code outside the lock, and use the same name throughout the mutation.
        var name = provider.Name;
        lock (_gate)
        {
            _providers[name] = provider;
            _defaultName ??= name;
        }
    }

    internal bool Unregister(string name)
    {
        lock (_gate)
        {
            if (!_providers.Remove(name))
                return false;
            if (StringComparer.OrdinalIgnoreCase.Equals(_defaultName, name))
                _defaultName = _providers.Keys.Min(StringComparer.OrdinalIgnoreCase);
            return true;
        }
    }

    internal IReadOnlyCollection<IJsEngineProvider> All
    {
        get { lock (_gate) return _providers.Values.ToArray(); }
    }

    internal IJsEngineProvider? Find(string name)
    {
        lock (_gate)
            return _providers.TryGetValue(name, out var provider) ? provider : null;
    }

    internal void SetDefault(string name)
    {
        lock (_gate)
        {
            if (!_providers.ContainsKey(name))
                throw new ArgumentException($"No JavaScript engine named '{name}' is registered.", nameof(name));
            _defaultName = name;
        }
    }

    internal IJsEngineProvider GetDefault(string? requested = null)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(requested) && _providers.TryGetValue(requested.Trim(), out var chosen))
                return chosen;
            if (_defaultName is { } name)
                return _providers[name];
            throw new InvalidOperationException(
                "No JavaScript engine provider is registered. A host must reference an engine provider " +
                "assembly and call its registration entry point before loading a page.");
        }
    }

    internal bool HasAny
    {
        get { lock (_gate) return _providers.Count != 0; }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _providers.Clear();
            _defaultName = null;
        }
    }
}
