using sutty.Core.Sessions;

namespace sutty.Core.Terminal;

/// <summary>One shell tab's observed lifetime; initial failures never count as an ended working session.</summary>
public sealed class ShellTabLifetimePolicy
{
    private readonly object _gate = new();
    private TerminalState _terminalState = TerminalState.Closed;
    private SessionState _sessionState = SessionState.Idle;
    private bool _terminalWasOpen;
    private bool _sessionWasConnected;
    private bool _closeRequested;

    public void ObserveTerminal(TerminalState state)
    {
        lock (_gate)
        {
            _terminalState = state;
            if (state == TerminalState.Open)
            {
                _terminalWasOpen = true;
                _closeRequested = false;
            }
        }
    }

    public void ObserveSession(SessionState state)
    {
        lock (_gate)
        {
            _sessionState = state;
            if (state == SessionState.Connected)
            {
                _sessionWasConnected = true;
                _closeRequested = false;
            }
        }
    }

    public bool HasEnded
    {
        get { lock (_gate) return HasEndedCore; }
    }

    /// <summary>Mounting or focusing a retained ended tab must not silently start a second shell.</summary>
    public bool CanStartAutomatically
    {
        get { lock (_gate) return !_terminalWasOpen && _terminalState is not (TerminalState.Open or TerminalState.Opening); }
    }

    private bool HasEndedCore =>
        (_terminalWasOpen && _terminalState is TerminalState.Closed or TerminalState.Failed) ||
        (_sessionWasConnected && _sessionState is SessionState.Disconnected or SessionState.Failed);

    /// <summary>Coalesce PTY-close and transport-disconnect callbacks into one request.</summary>
    public bool TryRequestClose(bool autoCloseEnabled)
    {
        lock (_gate)
        {
            if (!autoCloseEnabled || !HasEndedCore || _closeRequested) return false;
            _closeRequested = true;
            return true;
        }
    }
}
