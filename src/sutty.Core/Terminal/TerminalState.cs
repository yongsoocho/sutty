namespace sutty.Core.Terminal;

/// <summary>Lifecycle shared by remote PTY channels and local terminal processes.</summary>
public enum TerminalState
{
    Closed,
    Opening,
    Open,
    Failed,
}
