namespace sutty.Setting;

/// <summary>A shared palette identity for app chrome, terminal ANSI colors, and persisted settings.</summary>
public sealed record ThemeDefinition(
    string Id,
    string Name,
    bool IsDark,
    string Background,
    string Foreground,
    string Accent,
    string SecondaryAccent,
    string Violet,
    IReadOnlyList<string> AnsiColors);

/// <summary>
/// Familiar editor palettes adapted to Sutty's gradient UI. Names and terminal ids are stable;
/// existing saved preferences remain compatible. Palette sources are in docs/THEMES.md.
/// </summary>
public static class ThemeCatalog
{
    public const string FollowApplication = "FollowApplication";

    private const string VsDarkAnsi = "000000 CD3131 0DBC79 E5E510 2472C8 BC3FBC 11A8CD E5E5E5 666666 F14C4C 23D18B F5F543 3B8EEA D670D6 29B8DB FFFFFF";
    private const string VsLightAnsi = "000000 CD3131 008000 795E26 0451A5 BC05BC 0598BC 555555 666666 CD3131 008000 795E26 0451A5 BC05BC 0598BC A5A5A5";
    private const string SolarizedAnsi = "073642 DC322F 859900 B58900 268BD2 D33682 2AA198 EEE8D5 002B36 CB4B16 586E75 657B83 839496 6C71C4 93A1A1 FDF6E3";
    private const string TokyoAnsi = "15161E F7768E 9ECE6A E0AF68 7AA2F7 BB9AF7 7DCFFF A9B1D6 414868 F7768E 9ECE6A E0AF68 7AA2F7 BB9AF7 7DCFFF C0CAF5";

    public static IReadOnlyList<ThemeDefinition> Presets { get; } = Array.AsReadOnly<ThemeDefinition>(
    [
        Create("DeepField", "Dark", true, "0A0D10", "9FB0C0", "2BC7B5", "5BDCCB", "78A9EE",
            "111827 FF6B7A 66D9A6 F6C76A 72A7FF C792EA 6EE7D8 D7E2F0 637083 FF8793 83E6BB FFDC8A 93BDFF DDB3F4 99F6E4 FFFFFF"),
        Create("DeepFieldLight", "Light", false, "FFFFFF", "48545F", "0FA396", "0B7C72", "2570C8", VsLightAnsi),
        Create("VSCodeDarkPlus", "VS Code Dark+", true, "1E1E1E", "D4D4D4", "569CD6", "4EC9B0", "C586C0", VsDarkAnsi),
        Create("VSCodeLightPlus", "VS Code Light+", false, "FFFFFF", "000000", "0451A5", "008577", "AF00DB", VsLightAnsi),
        Create("VSCodeDarkModern", "VS Code Dark Modern", true, "1F1F1F", "CCCCCC", "4DAAFC", "4EC9B0", "C586C0", VsDarkAnsi),
        Create("VSCodeLightModern", "VS Code Light Modern", false, "FFFFFF", "3B3B3B", "005FB8", "008577", "8250DF", VsLightAnsi),
        Create("Dracula", "Dracula", true, "282A36", "F8F8F2", "BD93F9", "8BE9FD", "FF79C6",
            "21222C FF5555 50FA7B F1FA8C BD93F9 FF79C6 8BE9FD F8F8F2 6272A4 FF6E6E 69FF94 FFFFA5 D6ACFF FF92DF A4FFFF FFFFFF"),
        Create("Monokai", "Monokai", true, "272822", "F8F8F2", "F92672", "AE81FF", "66D9EF",
            "333333 C4265E 86B42B B3B42B 6A7EC8 8C6BC8 56ADBC E3E3DD 666666 F92672 A6E22E E2E22E 819AFF AE81FF 66D9EF F8F8F2"),
        Create("MonokaiDimmed", "Monokai Dimmed", true, "1E1E1E", "C5C8C6", "CE6700", "9872A2", "6089B4",
            "1E1E1E C7444A 98A64A D0B344 6089B4 9872A2 5C9A9A C5C8C6 666666 D8797C B3BE68 E5CE78 81A2BE B294BB 8ABEB7 F8F8F2"),
        Create("AtomOneDark", "Atom One Dark", true, "282C34", "ABB2BF", "61AFEF", "56B6C2", "C678DD",
            "1E2127 E06C75 98C379 E5C07B 61AFEF C678DD 56B6C2 ABB2BF 5C6370 E06C75 98C379 E5C07B 61AFEF C678DD 56B6C2 FFFFFF"),
        Create("AtomOneLight", "Atom One Light", false, "FAFAFA", "383A42", "4078F2", "0184BC", "A626A4",
            "383A42 E45649 50A14F C18401 4078F2 A626A4 0184BC A0A1A7 696C77 E45649 50A14F C18401 4078F2 A626A4 0184BC F0F0F0"),
        Create("GitHubDark", "GitHub Dark", true, "0D1117", "C9D1D9", "58A6FF", "3FB950", "BC8CFF",
            "484F58 FF7B72 3FB950 D29922 58A6FF BC8CFF 39C5CF B1BAC4 6E7681 FFA198 56D364 E3B341 79C0FF D2A8FF 56D4DD F0F6FC"),
        Create("GitHubLight", "GitHub Light", false, "FFFFFF", "24292F", "0969DA", "1A7F37", "8250DF",
            "24292F CF222E 116329 4D2D00 0969DA 8250DF 1B7C83 6E7781 57606A A40E26 1A7F37 633C01 218BFF A475F9 3192AA 8C959F"),
        Create("GitHubDarkDimmed", "GitHub Dark Dimmed", true, "22272E", "ADBAC7", "539BF5", "57AB5A", "B083F0",
            "545D68 F47067 57AB5A C69026 539BF5 B083F0 39C5CF 909DAB 636E7B FF938A 6BC46D DAAB44 6CB6FF DCBDFB 56D4DD CDD9E5"),
        Create("SolarizedDark", "Solarized Dark", true, "002B36", "93A1A1", "268BD2", "2AA198", "6C71C4", SolarizedAnsi),
        Create("SolarizedLight", "Solarized Light", false, "FDF6E3", "657B83", "268BD2", "2AA198", "6C71C4", SolarizedAnsi),
        Create("Nord", "Nord", true, "2E3440", "D8DEE9", "88C0D0", "8FBCBB", "B48EAD",
            "3B4252 BF616A A3BE8C EBCB8B 81A1C1 B48EAD 88C0D0 E5E9F0 4C566A BF616A A3BE8C EBCB8B 81A1C1 B48EAD 8FBCBB ECEFF4"),
        Create("TokyoNight", "Tokyo Night", true, "1A1B26", "A9B1D6", "7AA2F7", "7DCFFF", "BB9AF7", TokyoAnsi),
        Create("TokyoNightStorm", "Tokyo Night Storm", true, "24283B", "A9B1D6", "7AA2F7", "73DACA", "BB9AF7", TokyoAnsi),
        Create("TokyoNightLight", "Tokyo Night Light", false, "D5D6DB", "343B58", "34548A", "0F4B6E", "5A4A78",
            "0F0F14 8C4351 485E30 8F5E15 34548A 5A4A78 0F4B6E 9699A3 9699A3 8C4351 485E30 8F5E15 34548A 5A4A78 0F4B6E D5D6DB"),
        Create("CatppuccinMocha", "Catppuccin Mocha", true, "1E1E2E", "CDD6F4", "CBA6F7", "89DCEB", "F5C2E7",
            "45475A F38BA8 A6E3A1 F9E2AF 89B4FA F5C2E7 94E2D5 BAC2DE 585B70 F38BA8 A6E3A1 F9E2AF 89B4FA F5C2E7 94E2D5 A6ADC8"),
        Create("CatppuccinMacchiato", "Catppuccin Macchiato", true, "24273A", "CAD3F5", "C6A0F6", "91D7E3", "F5BDE6",
            "494D64 ED8796 A6DA95 EED49F 8AADF4 F5BDE6 8BD5CA B8C0E0 5B6078 ED8796 A6DA95 EED49F 8AADF4 F5BDE6 8BD5CA A5ADCB"),
        Create("CatppuccinFrappe", "Catppuccin Frappé", true, "303446", "C6D0F5", "CA9EE6", "99D1DB", "F4B8E4",
            "51576D E78284 A6D189 E5C890 8CAAEE F4B8E4 81C8BE B5BFE2 626880 E78284 A6D189 E5C890 8CAAEE F4B8E4 81C8BE A5ADCE"),
        Create("CatppuccinLatte", "Catppuccin Latte", false, "EFF1F5", "4C4F69", "8839EF", "179299", "EA76CB",
            "5C5F77 D20F39 40A02B DF8E1D 1E66F5 EA76CB 179299 ACB0BE 6C6F85 D20F39 40A02B DF8E1D 1E66F5 EA76CB 179299 BCC0CC"),
        Create("GruvboxDark", "Gruvbox Dark", true, "282828", "EBDBB2", "FABD2F", "8EC07C", "D3869B",
            "282828 CC241D 98971A D79921 458588 B16286 689D6A A89984 928374 FB4934 B8BB26 FABD2F 83A598 D3869B 8EC07C EBDBB2"),
        Create("GruvboxLight", "Gruvbox Light", false, "FBF1C7", "3C3836", "AF3A03", "427B58", "8F3F71",
            "FBF1C7 CC241D 98971A D79921 458588 B16286 689D6A 7C6F64 928374 9D0006 79740E B57614 076678 8F3F71 427B58 3C3836"),
        Create("NightOwl", "Night Owl", true, "011627", "D6DEEB", "82AAFF", "7FDBCA", "C792EA",
            "011627 EF5350 22DA6E ADDB67 82AAFF C792EA 21C7A8 FFFFFF 575656 EF5350 22DA6E FAD430 82AAFF C792EA 7FDBCA FFFFFF"),
        Create("LightOwl", "Light Owl", false, "FBFBFB", "403F53", "4876D6", "08916A", "994CC3",
            "403F53 DE3D3B 08916A E0AF02 4876D6 994CC3 0C969B 90A7B2 989FB1 DE3D3B 08916A E0AF02 4876D6 994CC3 0C969B D6DEEB"),
        Create("Material", "Material", true, "263238", "EEFFFF", "82AAFF", "80CBC4", "C792EA",
            "263238 F07178 C3E88D FFCB6B 82AAFF C792EA 89DDFF EEFFFF 546E7A F07178 C3E88D FFCB6B 82AAFF C792EA 89DDFF FFFFFF"),
        Create("MaterialPalenight", "Material Palenight", true, "292D3E", "A6ACCD", "C792EA", "89DDFF", "F07178",
            "292D3E F07178 C3E88D FFCB6B 82AAFF C792EA 89DDFF A6ACCD 676E95 F07178 C3E88D FFCB6B 82AAFF C792EA 89DDFF FFFFFF"),
        Create("AyuDark", "Ayu Dark", true, "0B0E14", "BFBDB6", "E6B450", "39BAE6", "D2A6FF",
            "01060E F07178 AAD94C FFB454 59C2FF D2A6FF 95E6CB BFBDB6 565B66 F07178 AAD94C FFB454 59C2FF D2A6FF 95E6CB F3F4F5"),
        Create("AyuLight", "Ayu Light", false, "F8F9FA", "5C6166", "BF7F00", "399EE6", "A37ACC",
            "5C6166 F07171 86B300 F2AE49 399EE6 A37ACC 4CBF99 ABB0B6 8A9199 F07171 86B300 F2AE49 399EE6 A37ACC 4CBF99 F8F9FA"),
        Create("AyuMirage", "Ayu Mirage", true, "1F2430", "CCCAC2", "FFCC66", "73D0FF", "D4BFFF",
            "191E2A F28779 D5FF80 FFD173 73D0FF D4BFFF 95E6CB CCCAC2 707A8C F28779 D5FF80 FFD173 73D0FF D4BFFF 95E6CB F3F4F5"),
        Create("Cobalt2", "Cobalt2", true, "193549", "FFFFFF", "FFC600", "00BBFF", "FF9D00",
            "000000 FF0000 3AD900 FFC600 0088FF FF628C 80FCFF FFFFFF 555555 FF628C A5FF90 FFFF00 9EFFFF FF9D00 80FCFF FFFFFF"),
        Create("SynthWave84", "SynthWave '84", true, "262335", "F0EFF1", "FF7EDB", "36F9F6", "B893CE",
            "241B2F FE4450 72F1B8 F97E72 03EDF9 FF7EDB 36F9F6 F0EFF1 614D85 FE4450 72F1B8 FFEA00 03EDF9 FF7EDB 36F9F6 FFFFFF"),
        Create("Ubuntu", "Ubuntu", true, "300A24", "EEEEEC", "E95420", "AD7FA8", "729FCF",
            "2E3436 CC0000 4E9A06 C4A000 3465A4 75507B 06989A D3D7CF 555753 EF2929 8AE234 FCE94F 729FCF AD7FA8 34E2E2 EEEEEC"),
    ]);

    public static ThemeDefinition FindApplication(string? name) =>
        Presets.FirstOrDefault(preset => string.Equals(preset.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? Presets[0];

    public static ThemeDefinition ResolveTerminal(string? id, string? applicationTheme) =>
        Presets.FirstOrDefault(preset => string.Equals(preset.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? FindApplication(applicationTheme);

    public static string NormalizeTerminalId(string? id) =>
        Presets.FirstOrDefault(preset => string.Equals(preset.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
        ?? FollowApplication;

    private static ThemeDefinition Create(
        string id, string name, bool isDark, string background, string foreground,
        string accent, string secondaryAccent, string violet, string ansi)
    {
        var colors = ansi.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(color => "#" + color).ToArray();
        if (colors.Length != 16 || colors.Any(color => color.Length != 7))
            throw new ArgumentException($"Theme {name} must define 16 RGB ANSI colors.", nameof(ansi));

        return new(id, name, isDark, "#" + background, "#" + foreground,
            "#" + accent, "#" + secondaryAccent, "#" + violet, Array.AsReadOnly(colors));
    }
}
