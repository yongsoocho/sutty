namespace sutty.Core.Terminal;

/// <summary>Raw bytes emitted by an interactive terminal. Subscribers must decode incrementally.</summary>
public sealed class TerminalDataReceivedEventArgs : EventArgs
{
    public TerminalDataReceivedEventArgs(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Data = data;
    }

    public ReadOnlyMemory<byte> Data { get; }
}
