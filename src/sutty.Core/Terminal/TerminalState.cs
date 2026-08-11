namespace sutty.Core.Terminal;

/// <summary>Lifecycle of the interactive PTY channel inside an SSH connection.</summary>
public enum TerminalState
{
    Closed,
    Opening,
    Open,
    Failed,
}
