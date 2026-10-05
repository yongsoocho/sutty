using System.Globalization;
using System.Text.RegularExpressions;

namespace sutty.Command;

/// <summary>Expands positional parameters once without interpreting their supplied values.</summary>
public static class CommandTemplateExpansion
{
    private static readonly Regex Parameters = new(@"\$([0-9]+)", RegexOptions.CultureInvariant);

    public static List<int> GetParameterNumbers(string command) => Parameters.Matches(command)
        .Select(match => TryGetNumber(match, out var number) ? (int?)number : null)
        .Where(number => number.HasValue)
        .Select(number => number!.Value)
        .Distinct()
        .Order()
        .ToList();

    public static string Expand(string command, IReadOnlyDictionary<int, string> values) =>
        Parameters.Replace(command, match =>
            TryGetNumber(match, out var number) && values.TryGetValue(number, out var value)
                ? value
                : match.Value);

    private static bool TryGetNumber(Match match, out int number) => int.TryParse(
        match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
}
