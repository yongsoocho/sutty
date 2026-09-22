using sutty.Setting;
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
    public const string FollowApplication = ThemeCatalog.FollowApplication;

    public static IReadOnlyList<TerminalThemePreset> Presets { get; } =
        ThemeCatalog.Presets.Select(CreatePreset).ToArray();

    public static TerminalThemePreset Resolve(string? id, string? applicationTheme)
    {
        var result = CreatePreset(ThemeCatalog.ResolveTerminal(id, applicationTheme));
        if (ThemeCatalog.NormalizeTerminalId(id) == FollowApplication)
        {
            // Follow the exact named palette, including its ANSI colors, even when
            // switching between two dark themes without an ActualThemeChanged event.
            var app = ThemeManager.Find(applicationTheme ?? "Dark");
            result.Palette.Background = app.Colors["TerminalBg"];
            result.Palette.Foreground = app.Colors["TerminalFg"];
            result.Palette.CursorAccent = app.Colors["TerminalBg"];
        }

        return result;
    }

    private static TerminalThemePreset CreatePreset(ThemeDefinition definition)
    {
        var ansi = definition.AnsiColors;
        var displayName = definition.Id switch
        {
            "DeepField" => "Sutty Deep Field",
            "DeepFieldLight" => "Sutty Deep Field Light",
            _ => definition.Name,
        };
        return new(definition.Id, displayName, definition.IsDark, new TerminalThemePayload
        {
            Background = definition.Background,
            Foreground = definition.Foreground,
            Cursor = definition.Accent,
            CursorAccent = definition.Background,
            SelectionBackground = Blend(definition.Background, definition.Accent, 0.30),
            Black = ansi[0], Red = ansi[1], Green = ansi[2], Yellow = ansi[3],
            Blue = ansi[4], Magenta = ansi[5], Cyan = ansi[6], White = ansi[7],
            BrightBlack = ansi[8], BrightRed = ansi[9], BrightGreen = ansi[10], BrightYellow = ansi[11],
            BrightBlue = ansi[12], BrightMagenta = ansi[13], BrightCyan = ansi[14], BrightWhite = ansi[15],
        });
    }

    private static string Blend(string first, string second, double amount)
    {
        var result = "#";
        for (var offset = 1; offset <= 5; offset += 2)
        {
            var a = Convert.ToByte(first.Substring(offset, 2), 16);
            var b = Convert.ToByte(second.Substring(offset, 2), 16);
            result += ((byte)Math.Round(a + (b - a) * amount)).ToString("X2");
        }
        return result;
    }
}
