using sutty.Core.Sessions;
using sutty.Core.Terminal;

internal static class ShellTabLifetimeSelfTests
{
    public static void Run()
    {
        var firstConnection = new ShellTabLifetimePolicy();
        firstConnection.ObserveTerminal(TerminalState.Closed);
        firstConnection.ObserveSession(SessionState.Connecting);
        firstConnection.ObserveSession(SessionState.Failed);
        Check(!firstConnection.TryRequestClose(true), "initial SSH failure remains visible");

        var firstProcess = new ShellTabLifetimePolicy();
        firstProcess.ObserveTerminal(TerminalState.Opening);
        firstProcess.ObserveTerminal(TerminalState.Closed);
        Check(!firstProcess.TryRequestClose(true), "initial or cancelled process opening stays open");
        firstProcess.ObserveTerminal(TerminalState.Failed);
        Check(!firstProcess.TryRequestClose(true), "initial process launch failure stays open");
        Check(firstProcess.CanStartAutomatically, "an initial failure does not masquerade as a completed shell");

        var remote = new ShellTabLifetimePolicy();
        remote.ObserveSession(SessionState.Connected);
        remote.ObserveTerminal(TerminalState.Closed);
        Check(!remote.TryRequestClose(true), "connected SSH does not close before its first PTY opens");
        remote.ObserveTerminal(TerminalState.Opening);
        remote.ObserveTerminal(TerminalState.Failed);
        Check(!remote.TryRequestClose(true), "initial PTY failure can be inspected while transport is connected");
        remote.ObserveTerminal(TerminalState.Open);
        remote.ObserveTerminal(TerminalState.Closed);
        Check(remote.TryRequestClose(true), "SSH logout closes its tab even when the SSH transport remains connected");
        remote.ObserveSession(SessionState.Disconnected);
        Check(!remote.TryRequestClose(true), "transport and PTY end events request closure only once");

        foreach (var endedState in new[] { SessionState.Disconnected, SessionState.Failed })
        {
            var established = new ShellTabLifetimePolicy();
            established.ObserveSession(SessionState.Connected);
            established.ObserveSession(endedState);
            Check(established.TryRequestClose(true), "established SSH disconnect/failure closes its owning tab");
        }

        var localOrExternal = new ShellTabLifetimePolicy();
        var otherWorkingTab = new ShellTabLifetimePolicy();
        localOrExternal.ObserveTerminal(TerminalState.Open);
        otherWorkingTab.ObserveTerminal(TerminalState.Open);
        localOrExternal.ObserveTerminal(TerminalState.Closed);
        Check(!localOrExternal.TryRequestClose(false), "keep-ended-tabs preference suppresses automatic closure");
        Check(localOrExternal.TryRequestClose(true), "local and external process exit closes the matching tab");
        Check(!localOrExternal.CanStartAutomatically, "retaining or remounting an ended shell does not launch it again");
        Check(!otherWorkingTab.TryRequestClose(true), "another working tab is unaffected");

        var restarted = new ShellTabLifetimePolicy();
        restarted.ObserveTerminal(TerminalState.Open);
        restarted.ObserveTerminal(TerminalState.Closed);
        restarted.ObserveTerminal(TerminalState.Opening);
        Check(!restarted.TryRequestClose(true), "an obsolete close callback cannot close a reopening terminal");
        restarted.ObserveTerminal(TerminalState.Open);
        Check(!restarted.TryRequestClose(true), "an obsolete close callback cannot close a running terminal");
        restarted.ObserveTerminal(TerminalState.Failed);
        var requests = 0;
        Parallel.For(0, 32, _ =>
        {
            if (restarted.TryRequestClose(true)) Interlocked.Increment(ref requests);
        });
        Check(requests == 1, "concurrent end callbacks coalesce into one close request");
        Console.WriteLine("Shell tab logout, disconnect, retention and lifecycle self-tests passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Shell tab lifetime test failed: " + message);
    }
}
