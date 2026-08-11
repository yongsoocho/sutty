namespace sutty.Core.Terminal;

/// <summary>Raw bytes emitted by the remote PTY. Subscribers must decode incrementally.</summary>
public sealed class TerminalDataReceivedEventArgs : EventArgs
{
    public TerminalDataReceivedEventArgs(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
    }

    public ReadOnlyMemory<byte> Data { get; }
}
