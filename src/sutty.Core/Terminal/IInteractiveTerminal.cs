namespace sutty.Core.Terminal;

/// <summary>
/// Byte-oriented interactive terminal contract shared by remote SSH PTYs and
/// Windows-local ConPTY sessions. Implementations own their process/channel lifetime.
/// </summary>
public interface IInteractiveTerminal
{
    TerminalState TerminalState { get; }
    string? LastTerminalError { get; }
    bool SupportsTerminalResize { get; }

    event EventHandler<TerminalState>? TerminalStateChanged;
    event EventHandler<TerminalDataReceivedEventArgs>? TerminalDataReceived;

    Task OpenTerminalAsync(TerminalSize size, CancellationToken ct = default);
    Task SendTerminalInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
    Task<bool> ResizeTerminalAsync(TerminalSize size, CancellationToken ct = default);
    Task CloseTerminalAsync();
}
