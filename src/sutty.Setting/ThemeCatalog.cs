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
    private const string OneDarkProAnsi = "3F4451 E05561 8CC265 D18F52 4AA5F0 C162DE 42B3C2 D7DAE0 4F5666 FF616E A5E075 F0A45D 4DC4FF DE73FF 4CD1E0 E6E6E6";
    private const string TomorrowAnsi = "111111 FF9DA4 D1F1A9 FFEEAD BBDAFF EBBBFF 99FFFF CCCCCC 333333 FF7882 B8F171 FFE580 80BAFF D778FF 78FFFF FFFFFF";
    private const string MaterialAnsi = "000000 F07178 C3E88D FFCB6B 82AAFF C792EA 89DDFF EEFFFF 546E7A F07178 C3E88D FFCB6B 82AAFF C792EA 89DDFF FFFFFF";

    public static IReadOnlyList<ThemeDefinition> Presets { get; } = Array.AsReadOnly<ThemeDefinition>(
    [
        Create("DeepField", "Dark", true, "0A0D10", "9FB0C0", "2BC7B5", "5BDCCB", "78A9EE",
            "111827 FF6B7A 66D9A6 F6C76A 72A7FF C792EA 6EE7D8 D7E2F0 637083 FF8793 83E6BB FFDC8A 93BDFF DDB3F4 99F6E4 FFFFFF"),
        Create("DeepFieldLight", "Light", false, "FFFFFF", "48545F", "0FA396", "0B7C72", "2570C8", VsLightAnsi),
        Create("VSCodeDarkPlus", "VS Code Dark+", true, "1E1E1E", "D4D4D4", "569CD6", "4EC9B0", "C586C0", VsDarkAnsi),
        Create("VSCodeLightPlus", "VS Code Light+", false, "FFFFFF", "000000", "0451A5", "008577", "AF00DB", VsLightAnsi),
        Create("VSCodeDarkModern", "VS Code Dark Modern", true, "1F1F1F", "CCCCCC", "4DAAFC", "4EC9B0", "C586C0", VsDarkAnsi),
        Create("VSCodeLightModern", "VS Code Light Modern", false, "FFFFFF", "3B3B3B", "005FB8", "008577", "8250DF", VsLightAnsi),
        Create("VSCodeQuietLight", "VS Code Quiet Light", false, "F5F5F5", "333333", "705697", "448C27", "7A3E9D", VsLightAnsi),
        Create("VSCodeAbyss", "VS Code Abyss", true, "000C18", "6688CC", "80BAFF", "99FFFF", "D778FF", TomorrowAnsi),
        Create("VSCodeKimbieDark", "VS Code Kimbie Dark", true, "221A0F", "D3AF86", "F79A32", "8AB1B0", "98676A", VsDarkAnsi),
        Create("VSCodeRed", "VS Code Red", true, "390000", "F8F8F8", "FF6262", "9DF39F", "FEC758", VsDarkAnsi),
        Create("VSCodeTomorrowNightBlue", "VS Code Tomorrow Night Blue", true, "002451", "FFFFFF", "BBDAFF", "99FFFF", "EBBBFF", TomorrowAnsi),
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
        Create("OneDarkPro", "One Dark Pro", true, "282C34", "ABB2BF", "61AFEF", "56B6C2", "C678DD", OneDarkProAnsi),
        Create("OneDarkProDarker", "One Dark Pro Darker", true, "23272E", "ABB2BF", "61AFEF", "56B6C2", "C678DD", OneDarkProAnsi),
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
        Create("MaterialDarker", "Material Darker", true, "212121", "EEFFFF", "82AAFF", "80CBC4", "C792EA", MaterialAnsi),
        Create("MaterialOcean", "Material Ocean", true, "0F111A", "BABED8", "82AAFF", "80CBC4", "C792EA", MaterialAnsi),
        Create("MaterialLighter", "Material Lighter", false, "FAFAFA", "546E7A", "6182B8", "39ADB5", "9C3EDA",
            "000000 E53935 91B859 E2931D 6182B8 9C3EDA 39ADB5 90A4AE 546E7A E53935 91B859 E2931D 6182B8 945EB8 39ADB5 FFFFFF"),
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
        Create("RosePine", "Rosé Pine", true, "191724", "E0DEF4", "EBBCBA", "9CCFD8", "C4A7E7",
            "26233A EB6F92 31748F F6C177 9CCFD8 C4A7E7 EBBCBA E0DEF4 908CAA EB6F92 31748F F6C177 9CCFD8 C4A7E7 EBBCBA E0DEF4"),
        Create("RosePineMoon", "Rosé Pine Moon", true, "232136", "E0DEF4", "EA9A97", "9CCFD8", "C4A7E7",
            "393552 EB6F92 3E8FB0 F6C177 9CCFD8 C4A7E7 EA9A97 E0DEF4 908CAA EB6F92 3E8FB0 F6C177 9CCFD8 C4A7E7 EA9A97 E0DEF4"),
        Create("RosePineDawn", "Rosé Pine Dawn", false, "FAF4ED", "575279", "D7827E", "56949F", "907AA9",
            "F2E9E1 B4637A 286983 EA9D34 56949F 907AA9 D7827E 575279 797593 B4637A 286983 EA9D34 56949F 907AA9 D7827E 575279"),
        Create("EverforestDark", "Everforest Dark", true, "2D353B", "D3C6AA", "A7C080", "7FBBB3", "D699B6",
            "343F44 E67E80 A7C080 DBBC7F 7FBBB3 D699B6 83C092 D3C6AA 859289 E67E80 A7C080 DBBC7F 7FBBB3 D699B6 83C092 D3C6AA"),
        Create("EverforestLight", "Everforest Light", false, "FDF6E3", "5C6A72", "8DA101", "35A77C", "DF69BA",
            "5C6A72 F85552 8DA101 DFA000 3A94C5 DF69BA 35A77C 939F91 5C6A72 F85552 8DA101 DFA000 3A94C5 DF69BA 35A77C F4F0D9"),
        Create("KanagawaWave", "Kanagawa Wave", true, "1F1F28", "DCD7BA", "7E9CD8", "7AA89F", "957FB8",
            "16161D C34043 76946A C0A36E 7E9CD8 957FB8 6A9589 C8C093 727169 E82424 98BB6C E6C384 7FB4CA 938AA9 7AA89F DCD7BA"),
        Create("KanagawaDragon", "Kanagawa Dragon", true, "181616", "C5C9C5", "8BA4B0", "8EA4A2", "8992A7",
            "0D0C0C C4746E 8A9A7B C4B28A 8BA4B0 A292A3 8EA4A2 C8C093 A6A69C E46876 87A987 E6C384 7FB4CA 938AA9 7AA89F C5C9C5"),
        Create("KanagawaLotus", "Kanagawa Lotus", false, "F2ECBC", "545464", "4D699B", "597B75", "624C83",
            "1F1F28 C84053 6F894E 77713F 4D699B B35B79 597B75 545464 8A8980 D7474B 6E915F 836F4A 6693BF 624C83 5E857A 43436C"),
        Create("Horizon", "Horizon", true, "1C1E26", "D5D8DA", "E95678", "26BBD9", "EE64AC",
            "000000 E95678 29D398 FAB795 26BBD9 EE64AC 59E1E3 E5E5E5 666666 EC6A88 3FDAA4 FBC3A7 3FC4DE F075B5 6BE4E6 FFFFFF"),
        Create("HorizonBright", "Horizon Bright", false, "FDF0ED", "06060C", "E95678", "26BBD9", "EE64AC",
            "000000 E95678 29D398 FAB795 26BBD9 EE64AC 59E1E3 555555 666666 EC6A88 3FDAA4 FBC3A7 3FC4DE F075B5 6BE4E6 A5A5A5"),
        Create("Andromeda", "Andromeda", true, "23262E", "D5CED9", "00E8C6", "7CB7FF", "FF00AA",
            "000000 EE5D43 96E072 FFE66D 7CB7FF FF00AA 00E8C6 E5E5E5 666666 EE5D43 96E072 FFE66D 7CB7FF FF00AA 00E8C6 FFFFFF"),
        Create("Poimandres", "Poimandres", true, "1B1E28", "A6ACCD", "5DE4C7", "ADD7FF", "F087BD",
            "1B1E28 D0679D 5DE4C7 FFFAC2 89DDFF F087BD 89DDFF FFFFFF A6ACCD D0679D 5DE4C7 FFFAC2 ADD7FF F087BD ADD7FF FFFFFF"),
        Create("PoimandresStorm", "Poimandres Storm", true, "252B37", "A6ACCD", "5DE4C7", "ADD7FF", "F087BD",
            "252B37 D0679D 5DE4C7 FFFAC2 89DDFF F087BD 89DDFF FFFFFF A6ACCD D0679D 5DE4C7 FFFAC2 ADD7FF F087BD ADD7FF FFFFFF"),
        Create("Vesper", "Vesper", true, "101010", "FFFFFF", "FFC799", "99FFE4", "FF8080", VsDarkAnsi),
        Create("MinDark", "Min Dark", true, "1F1F1F", "888888", "79B8FF", "FFAB70", "B392F0",
            "000000 CD3131 0DBC79 E5E510 2472C8 BC3FBC 11A8CD E5E5E5 5C5C5C F14C4C 23D18B F5F543 3B8EEA D670D6 29B8DB FFFFFF"),
        Create("MinLight", "Min Light", false, "FFFFFF", "212121", "6871FF", "4DBF99", "9966CC",
            "333333 D32F2F 77CC00 F29718 E0E0E0 9966CC 4DBF99 C7C7C7 A1A1A1 D6656A A3D900 E7C547 6871FF A37ACC 57D9AD 7E7E7E"),
        Create("Darcula", "Darcula", true, "242424", "CCCCCC", "CC8242", "7A9EC2", "9E7BB0", VsDarkAnsi),
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
        if (colors.Length != 16 || colors.Any(color => !IsRgb(color[1..])))
            throw new ArgumentException($"Theme {name} must define 16 RGB ANSI colors.", nameof(ansi));
        if (new[] { background, foreground, accent, secondaryAccent, violet }.Any(color => !IsRgb(color)))
            throw new ArgumentException($"Theme {name} must define RGB UI colors.", nameof(background));

        return new(id, name, isDark, "#" + background, "#" + foreground,
            "#" + accent, "#" + secondaryAccent, "#" + violet, Array.AsReadOnly(colors));
    }

    private static bool IsRgb(string color) => color.Length == 6 && color.All(Uri.IsHexDigit);
}
