using System.Text.Json.Serialization;

namespace sutty.UI.Controls;

internal sealed class TerminalBridgeMessage
{
    public int Version { get; set; } = 1;
    public string Type { get; set; } = string.Empty;
    public long Id { get; set; }
    public string? Data { get; set; }
    public string? Text { get; set; }
    public int Columns { get; set; }
    public int Rows { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public int Number { get; set; }
    public string? Action { get; set; }
    public string? FontFamily { get; set; }
    public int FontSize { get; set; }
    public string? CursorStyle { get; set; }
    public bool CursorBlink { get; set; }
    public int Scrollback { get; set; }
    public bool ScreenReaderMode { get; set; }
    public string? Language { get; set; }
    public TerminalThemePayload? Theme { get; set; }
}

internal sealed class TerminalThemePayload
{
    public string Background { get; set; } = "#08111f";
    public string Foreground { get; set; } = "#d7e2f0";
    public string Cursor { get; set; } = "#6ee7d8";
    public string CursorAccent { get; set; } = "#08111f";
    public string SelectionBackground { get; set; } = "#315878";
    public string Black { get; set; } = "#111827";
    public string Red { get; set; } = "#ff6b7a";
    public string Green { get; set; } = "#66d9a6";
    public string Yellow { get; set; } = "#f6c76a";
    public string Blue { get; set; } = "#72a7ff";
    public string Magenta { get; set; } = "#c792ea";
    public string Cyan { get; set; } = "#6ee7d8";
    public string White { get; set; } = "#d7e2f0";
    public string BrightBlack { get; set; } = "#637083";
    public string BrightRed { get; set; } = "#ff8793";
    public string BrightGreen { get; set; } = "#83e6bb";
    public string BrightYellow { get; set; } = "#ffdc8a";
    public string BrightBlue { get; set; } = "#93bdff";
    public string BrightMagenta { get; set; } = "#ddb3f4";
    public string BrightCyan { get; set; } = "#99f6e4";
    public string BrightWhite { get; set; } = "#ffffff";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TerminalBridgeMessage))]
internal sealed partial class TerminalBridgeJsonContext : JsonSerializerContext;
