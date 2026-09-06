using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Core.Terminal;

internal static class TunnelAndDirectoryTests
{
    public static async Task RunAsync()
    {
        CheckDirectoryPreparation();
        await CheckOwnershipAndRecoveryAsync();
        await CheckCancellationAndShutdownAsync();
        await CheckCleanupFailureAsync();
        Console.WriteLine("Tunnel lifecycle and explicit directory command checks passed.");
    }

    private static void CheckDirectoryPreparation()
    {
        Check(TerminalDirectoryCommand.Prepare("/") == "cd '/'", "root command");
        Check(TerminalDirectoryCommand.Prepare("/한글 디렉터리/$HOME; `whoami` $(id)") ==
            "cd '/한글 디렉터리/$HOME; `whoami` $(id)'", "metacharacters remain literal");
        Check(TerminalDirectoryCommand.Prepare("/it's a path ") == "cd '/it'\"'\"'s a path '",
            "quotes escaped and significant trailing space retained");
        foreach (var invalid in new[] { "", "relative/path", "~/path", "/x\nreboot", "/x\r", "/x\0", "/x\u001b[0m", "/x\u2028id" })
        {
            try { TerminalDirectoryCommand.Prepare(invalid); throw new Exception("invalid path accepted"); }
            catch (ArgumentException) { }
        }
    }

    private static async Task CheckOwnershipAndRecoveryAsync()
    {
        var listeners = new List<TestListener>();
        var manager = new SessionTunnelManager(_ =>
        {
            var listener = new TestListener();
            listeners.Add(listener);
            return listener;
        });
        var source = Rule();
        var id = manager.Add(source);
        source.BindHost = "0.0.0.0";
        Check(manager.Snapshot().Single().Definition.BindHost == "127.0.0.1", "immutable captured definition");
        Check(!manager.Snapshot().Single().IsListening, "adding has no network side effect");
        await manager.StartAsync(id, false);
        await manager.StartAsync(id, false);
        Check(listeners.Count == 1 && manager.Snapshot().Single().State == TunnelState.Running,
            "repeated start retains exactly one listener");
        var staleFailure = listeners[0].CaptureFailureCallback();
        listeners[0].RaiseFailure();
        Check(manager.Snapshot().Single() is { State: TunnelState.Failed, IsListening: true, ErrorCode: "listener-failed" },
            "channel error reports that listener remains open");
        await manager.StartAsync(id, false);
        Check(listeners[0].Disposed && listeners[0].SubscriberCount == 0 && listeners.Count == 2,
            "retry retires and detaches previous listener first");
        staleFailure();
        Check(manager.Snapshot().Single().State == TunnelState.Running, "stale callback cannot fault new generation");
        await manager.StopAsync(id);
        await manager.StopAsync(id);
        Check(listeners[1].Disposed && manager.Snapshot().Single().State == TunnelState.Stopped,
            "stop is idempotent");

        var externalId = manager.Add(Rule("0.0.0.0"));
        await ExpectAsync<InvalidOperationException>(() => manager.StartAsync(externalId, false));
        Check(listeners.Count == 2, "external bind rejected before opening a listener");
        await manager.StartAsync(externalId, true);
        await manager.StopAllAsync();
        Check(listeners.All(l => l.Disposed && l.SubscriberCount == 0), "shutdown releases every listener callback");

        var failures = new List<TestListener>();
        var failing = new SessionTunnelManager(_ =>
        {
            var listener = new TestListener { FailStart = true };
            failures.Add(listener);
            return listener;
        });
        var failId = failing.Add(Rule());
        await ExpectAsync<IOException>(() => failing.StartAsync(failId, false));
        Check(failures.Single().Disposed && failing.Snapshot().Single() is
            { State: TunnelState.Failed, IsListening: false, ErrorCode: "start-failed" },
            "partial start disposes resource and records actionable failure");
        await failing.StopAllAsync();
        Check(failing.Snapshot().Single().ErrorCode == "start-failed", "disconnect retains last failure for review");

        try { manager.Add(Rule(port: 0)); throw new Exception("invalid port accepted"); }
        catch (ArgumentException) { }
        try { manager.Add(new SshPortForwardingRule { Type = (SshPortForwardingType)17, BindPort = 22 }); throw new Exception("invalid type accepted"); }
        catch (ArgumentException) { }
        var limits = new SessionTunnelManager(_ => throw new Exception("Adding must not create a listener."));
        for (var n = 0; n < 32; n++) limits.Add(Rule(port: n + 1000));
        try { limits.Add(Rule()); throw new Exception("unbounded tunnel count"); }
        catch (InvalidOperationException) { }
    }

    private static async Task CheckCancellationAndShutdownAsync()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var listener = new TestListener
        {
            OnStart = () =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test release timeout");
            },
        };
        var manager = new SessionTunnelManager(_ => listener);
        var id = manager.Add(Rule());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => manager.StartAsync(id, false, canceled.Token));
        Check(!listener.StartCalled && !listener.Disposed, "pre-cancellation does not create a listener");

        using var duringStart = new CancellationTokenSource();
        var pending = manager.StartAsync(id, false, duringStart.Token);
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "start entered");
        var shutdown = manager.StopAllAsync();
        duringStart.Cancel();
        Check(!pending.IsCompleted && !shutdown.IsCompleted, "cancellation and shutdown drain in-flight start");
        release.Set();
        await ExpectAsync<OperationCanceledException>(() => pending);
        await shutdown;
        Check(listener.Disposed && listener.SubscriberCount == 0 &&
            manager.Snapshot().Single() is { State: TunnelState.Stopped, IsListening: false, ErrorCode: null },
            "late-created canceled listener is disposed and cancellation is not failure");
    }

    private static async Task CheckCleanupFailureAsync()
    {
        var good = new TestListener();
        var bad = new TestListener { FailDispose = true };
        var factoryIndex = 0;
        var manager = new SessionTunnelManager(_ => factoryIndex++ == 0 ? good : bad);
        var first = manager.Add(Rule(port: 9001));
        var second = manager.Add(Rule(port: 9002));
        await manager.StartAsync(first, false);
        await manager.StartAsync(second, false);
        await ExpectAsync<IOException>(() => manager.StopAllAsync());
        Check(good.Disposed && manager.Snapshot().Single(t => t.Id == second) is
            { State: TunnelState.Failed, IsListening: true, ErrorCode: "stop-failed" },
            "one cleanup failure does not strand other listeners or claim success");
        bad.FailDispose = false;
        await manager.StopAsync(second);
        Check(bad.Disposed && manager.Snapshot().All(t => !t.IsListening), "failed cleanup remains retryable");
    }

    private static SshPortForwardingRule Rule(string bind = "127.0.0.1", int port = 8080) => new()
    {
        Type = SshPortForwardingType.Local, BindHost = bind, BindPort = port,
        DestinationHost = "127.0.0.1", DestinationPort = 80,
    };

    private static async Task ExpectAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("Tunnel/link check failed: " + label);
    }

    private sealed class TestListener : ITunnelListener
    {
        public bool IsStarted { get; private set; }
        public bool Disposed { get; private set; }
        public bool StartCalled { get; private set; }
        public bool FailStart { get; init; }
        public bool FailDispose { get; set; }
        public Action? OnStart { get; init; }
        public event EventHandler? Failed;
        public event EventHandler? Closing;
        public int SubscriberCount => (Failed?.GetInvocationList().Length ?? 0) + (Closing?.GetInvocationList().Length ?? 0);
        public Action CaptureFailureCallback() { var callback = Failed; return () => callback?.Invoke(this, EventArgs.Empty); }
        public void RaiseFailure() => Failed?.Invoke(this, EventArgs.Empty);
        public void Start()
        {
            StartCalled = true;
            OnStart?.Invoke();
            IsStarted = true;
            if (FailStart) throw new IOException("Synthetic start failure.");
        }
        public void Dispose()
        {
            if (FailDispose) throw new IOException("Synthetic cleanup failure.");
            Closing?.Invoke(this, EventArgs.Empty);
            IsStarted = false;
            Disposed = true;
        }
    }
}
