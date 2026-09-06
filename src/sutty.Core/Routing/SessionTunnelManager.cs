using sutty.Core.Models;

namespace sutty.Core.Routing;

public enum TunnelState { Stopped, Starting, Running, Stopping, Failed }

/// <summary>Immutable session-local intent. Definitions never carry authentication data.</summary>
public sealed record TunnelDefinition(
    SshPortForwardingType Type, string BindHost, int BindPort,
    string DestinationHost, int DestinationPort)
{
    public static TunnelDefinition FromRule(SshPortForwardingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!Enum.IsDefined(rule.Type) || string.IsNullOrWhiteSpace(rule.BindHost) ||
            rule.BindHost.Any(char.IsWhiteSpace) || rule.BindHost.Any(char.IsControl) ||
            rule.BindPort is < 1 or > 65_535)
            throw new ArgumentException("Check the tunnel type, bind address, and port (1–65535).");
        if (rule.Type != SshPortForwardingType.Dynamic &&
            (string.IsNullOrWhiteSpace(rule.DestinationHost) ||
             rule.DestinationHost.Any(char.IsWhiteSpace) || rule.DestinationHost.Any(char.IsControl) ||
             rule.DestinationPort is < 1 or > 65_535))
            throw new ArgumentException("Check the tunnel destination address and port (1–65535).");
        return new(rule.Type, rule.BindHost, rule.BindPort,
            rule.Type == SshPortForwardingType.Dynamic ? "" : rule.DestinationHost,
            rule.Type == SshPortForwardingType.Dynamic ? 0 : rule.DestinationPort);
    }
}

public sealed record TunnelSnapshot(Guid Id, TunnelDefinition Definition,
    TunnelState State, bool IsListening, string? ErrorCode);

/// <summary>Optional runtime tunnel capability, separate from terminal and SFTP availability.</summary>
public interface IPortForwardingSession
{
    IReadOnlyList<TunnelSnapshot> Tunnels { get; }
    event EventHandler? TunnelsChanged;
    Task<Guid> AddTunnelAsync(SshPortForwardingRule rule, CancellationToken ct = default);
    Task StartTunnelAsync(Guid id, bool allowExternalBind = false, CancellationToken ct = default);
    Task StopTunnelAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// A listener is owned until Dispose returns, including partial Start failure. Implementations
/// must bound synchronous network waits; the manager drains them before cancellation returns.
/// </summary>
public interface ITunnelListener : IDisposable
{
    bool IsStarted { get; }
    event EventHandler? Failed;
    event EventHandler? Closing;
    void Start();
}

/// <summary>
/// Serializes listener ownership, snapshots and cleanup. Factory injection lets lifecycle and
/// cancellation be tested without credentials or a server. Error codes never contain server text.
/// </summary>
public sealed class SessionTunnelManager(Func<TunnelDefinition, ITunnelListener> createListener)
{
    private sealed class Entry(Guid id, TunnelDefinition definition)
    {
        public Guid Id { get; } = id;
        public TunnelDefinition Definition { get; } = definition;
        public TunnelState State { get; set; }
        public string? ErrorCode { get; set; }
        public ITunnelListener? Listener { get; set; }
        public EventHandler? OnFailed { get; set; }
        public EventHandler? OnClosing { get; set; }
    }

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly List<Entry> _entries = [];
    public event EventHandler? Changed;

    public IReadOnlyList<TunnelSnapshot> Snapshot()
    {
        lock (_stateGate)
            return _entries.Select(e => new TunnelSnapshot(e.Id, e.Definition,
                e.State, e.Listener?.IsStarted == true, e.ErrorCode)).ToArray();
    }

    public Guid Add(SshPortForwardingRule rule)
    {
        var definition = TunnelDefinition.FromRule(rule);
        Guid id;
        lock (_stateGate)
        {
            if (_entries.Count >= 32)
                throw new InvalidOperationException("At most 32 tunnels are supported per session.");
            id = Guid.NewGuid();
            _entries.Add(new Entry(id, definition));
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return id;
    }

    public async Task StartAsync(Guid id, bool allowExternalBind, CancellationToken ct = default)
    {
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Entry entry;
            lock (_stateGate)
                entry = _entries.Single(e => e.Id == id);
            if (ForwardingExposurePolicy.IsExternalBind(entry.Definition.BindHost) && !allowExternalBind)
                throw new InvalidOperationException("Explicit consent is required for an external tunnel bind.");
            if (entry.State == TunnelState.Running)
                return;
            if (entry.Listener is not null)
                await ReleaseAsync(entry).ConfigureAwait(false);

            Update(entry, TunnelState.Starting);
            try
            {
                ct.ThrowIfCancellationRequested();
                var listener = createListener(entry.Definition);
                lock (_stateGate)
                {
                    entry.Listener = listener;
                    entry.OnFailed = (_, _) => ListenerFailed(entry, listener);
                    entry.OnClosing = (_, _) => ListenerClosing(entry, listener);
                    listener.Failed += entry.OnFailed;
                    listener.Closing += entry.OnClosing;
                }
                // SSH.NET Start has finite connection timeout. Never abandon it on cancellation:
                // a listener created after cancellation must still be disposed before returning.
                await Task.Run(listener.Start, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                lock (_stateGate)
                {
                    if (entry.State == TunnelState.Starting)
                    {
                        entry.State = listener.IsStarted ? TunnelState.Running : TunnelState.Failed;
                        if (!listener.IsStarted) entry.ErrorCode = "start-failed";
                    }
                }
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                await ReleaseAsync(entry).ConfigureAwait(false);
                Update(entry, TunnelState.Stopped);
                throw;
            }
            catch
            {
                await ReleaseAsync(entry).ConfigureAwait(false);
                Update(entry, TunnelState.Failed, "start-failed");
                throw;
            }
        }
        finally { _operationGate.Release(); }
    }

    public async Task StopAsync(Guid id, CancellationToken ct = default)
    {
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Entry entry;
            lock (_stateGate) entry = _entries.Single(e => e.Id == id);
            Update(entry, TunnelState.Stopping);
            await ReleaseAsync(entry).ConfigureAwait(false);
            Update(entry, TunnelState.Stopped);
        }
        finally { _operationGate.Release(); }
    }

    public async Task StopAllAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Entry[] entries;
            lock (_stateGate) entries = _entries.ToArray();
            Exception? cleanupError = null;
            foreach (var entry in entries.Reverse())
            {
                try
                {
                    await ReleaseAsync(entry).ConfigureAwait(false);
                    // Retain a failed rule's diagnosis after disconnect for review.
                    Update(entry, entry.ErrorCode is null ? TunnelState.Stopped : TunnelState.Failed,
                        entry.ErrorCode);
                }
                catch (Exception error) { cleanupError ??= error; }
            }
            if (cleanupError is not null) throw cleanupError;
        }
        finally { _operationGate.Release(); }
    }

    private async Task ReleaseAsync(Entry entry)
    {
        ITunnelListener? listener;
        lock (_stateGate)
        {
            listener = entry.Listener;
            if (listener is not null)
            {
                listener.Failed -= entry.OnFailed;
                listener.Closing -= entry.OnClosing;
            }
            entry.OnFailed = entry.OnClosing = null;
        }
        if (listener is null) return;
        try
        {
            await Task.Run(listener.Dispose).ConfigureAwait(false);
            lock (_stateGate) entry.Listener = null;
        }
        catch
        {
            // Keep ownership and actual IsStarted after failed cleanup, allowing another
            // stop attempt. Session teardown still disposes its SSH client as a final owner.
            Update(entry, TunnelState.Failed, "stop-failed");
            throw;
        }
    }

    private void ListenerFailed(Entry entry, ITunnelListener listener)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(entry.Listener, listener) || entry.OnFailed is null) return;
            entry.State = TunnelState.Failed;
            entry.ErrorCode = "listener-failed";
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ListenerClosing(Entry entry, ITunnelListener listener)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(entry.Listener, listener) || entry.OnClosing is null) return;
            if (entry.ErrorCode is null) entry.State = TunnelState.Stopping;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Update(Entry entry, TunnelState state, string? error = null)
    {
        lock (_stateGate) { entry.State = state; entry.ErrorCode = error; }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
