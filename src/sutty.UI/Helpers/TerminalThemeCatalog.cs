using sutty.UI.Controls;
using System;
using System.Collections.Generic;
using System.Linq;

namespace sutty.UI.Helpers;

internal sealed record TerminalThemePreset(
    string Id,
    string DisplayName,
    bool IsDark,
    TerminalThemePayload Palette);

internal static class TerminalThemeCatalog
{
    public const string FollowApplication = "FollowApplication";

    public static IReadOnlyList<TerminalThemePreset> Presets { get; } =
    [
        new("DeepField", "Sutty Deep Field", true, Palette(
            "#08111f", "#d7e2f0", "#6ee7d8", "#08111f", "#315878",
            "#111827", "#ff6b7a", "#66d9a6", "#f6c76a", "#72a7ff", "#c792ea", "#6ee7d8", "#d7e2f0",
            "#637083", "#ff8793", "#83e6bb", "#ffdc8a", "#93bdff", "#ddb3f4", "#99f6e4", "#ffffff")),
        new("Ubuntu", "Ubuntu", true, Palette(
            "#300a24", "#eeeeec", "#f2f2f2", "#300a24", "#5e2750",
            "#2e3436", "#cc0000", "#4e9a06", "#c4a000", "#3465a4", "#75507b", "#06989a", "#d3d7cf",
            "#555753", "#ef2929", "#8ae234", "#fce94f", "#729fcf", "#ad7fa8", "#34e2e2", "#eeeeec")),
        new("AtomOneDark", "Atom One Dark", true, Palette(
            "#282c34", "#abb2bf", "#528bff", "#282c34", "#3e4451",
            "#1e2127", "#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2", "#abb2bf",
            "#5c6370", "#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2", "#ffffff")),
        new("Dracula", "Dracula", true, Palette(
            "#282a36", "#f8f8f2", "#f8f8f2", "#282a36", "#44475a",
            "#21222c", "#ff5555", "#50fa7b", "#f1fa8c", "#bd93f9", "#ff79c6", "#8be9fd", "#f8f8f2",
            "#6272a4", "#ff6e6e", "#69ff94", "#ffffa5", "#d6acff", "#ff92df", "#a4ffff", "#ffffff")),
        new("GitHubDark", "GitHub Dark", true, Palette(
            "#0d1117", "#c9d1d9", "#58a6ff", "#0d1117", "#264f78",
            "#484f58", "#ff7b72", "#3fb950", "#d29922", "#58a6ff", "#bc8cff", "#39c5cf", "#b1bac4",
            "#6e7681", "#ffa198", "#56d364", "#e3b341", "#79c0ff", "#d2a8ff", "#56d4dd", "#f0f6fc")),
        new("GitHubLight", "GitHub Light", false, Palette(
            "#ffffff", "#24292f", "#0969da", "#ffffff", "#add6ff",
            "#24292f", "#cf222e", "#116329", "#4d2d00", "#0969da", "#8250df", "#1b7c83", "#6e7781",
            "#57606a", "#a40e26", "#1a7f37", "#633c01", "#218bff", "#a475f9", "#3192aa", "#8c959f")),
        new("SolarizedDark", "Solarized Dark", true, Palette(
            "#002b36", "#839496", "#93a1a1", "#002b36", "#073642",
            "#073642", "#dc322f", "#859900", "#b58900", "#268bd2", "#d33682", "#2aa198", "#eee8d5",
            "#586e75", "#cb4b16", "#586e75", "#657b83", "#839496", "#6c71c4", "#93a1a1", "#fdf6e3")),
        new("SolarizedLight", "Solarized Light", false, Palette(
            "#fdf6e3", "#657b83", "#586e75", "#fdf6e3", "#eee8d5",
            "#073642", "#dc322f", "#859900", "#b58900", "#268bd2", "#d33682", "#2aa198", "#eee8d5",
            "#002b36", "#cb4b16", "#586e75", "#657b83", "#839496", "#6c71c4", "#93a1a1", "#fdf6e3")),
    ];

    public static TerminalThemePreset Resolve(string? id, bool applicationIsDark)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            string.Equals(id, FollowApplication, StringComparison.OrdinalIgnoreCase))
        {
            return applicationIsDark
                ? Presets[0]
                : Presets.First(preset => preset.Id == "GitHubLight");
        }

        return Presets.FirstOrDefault(preset =>
                   string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))
               ?? (applicationIsDark ? Presets[0] : Presets.First(preset => preset.Id == "GitHubLight"));
    }

    private static TerminalThemePayload Palette(
        string background, string foreground, string cursor, string cursorAccent, string selection,
        string black, string red, string green, string yellow, string blue, string magenta, string cyan, string white,
        string brightBlack, string brightRed, string brightGreen, string brightYellow, string brightBlue,
        string brightMagenta, string brightCyan, string brightWhite) => new()
        {
            Background = background,
            Foreground = foreground,
            Cursor = cursor,
            CursorAccent = cursorAccent,
            SelectionBackground = selection,
            Black = black,
            Red = red,
            Green = green,
            Yellow = yellow,
            Blue = blue,
            Magenta = magenta,
            Cyan = cyan,
            White = white,
            BrightBlack = brightBlack,
            BrightRed = brightRed,
            BrightGreen = brightGreen,
            BrightYellow = brightYellow,
            BrightBlue = brightBlue,
            BrightMagenta = brightMagenta,
            BrightCyan = brightCyan,
            BrightWhite = brightWhite,
        };
}
