namespace sutty.Core.Terminal;

/// <summary>Character and pixel dimensions supplied when a PTY is allocated.</summary>
public readonly record struct TerminalSize(
    uint Columns,
    uint Rows,
    uint PixelWidth = 0,
    uint PixelHeight = 0)
{
    public TerminalSize Clamp() => new(
        Math.Clamp(Columns, 20u, 500u),
        Math.Clamp(Rows, 5u, 200u),
        PixelWidth,
        PixelHeight);
}
