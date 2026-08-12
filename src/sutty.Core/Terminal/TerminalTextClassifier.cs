using System.Text.Json;
using System.Text.RegularExpressions;

namespace sutty.Core.Terminal;

/// <summary>The semantic role of a highlighted terminal text range.</summary>
public enum TerminalTextHighlightKind
{
    Comment,
    String,
    Number,
    Keyword,
    Property,
    Warning,
    Critical,
}

/// <summary>A UTF-16 text range classified for terminal presentation.</summary>
public readonly record struct TerminalTextSpan(
    int Start,
    int Length,
    TerminalTextHighlightKind Kind);

/// <summary>
/// Classifies structured terminal output and operational severity without adding a parser
/// dependency to the UI. Classification is bounded so an unexpectedly large command result
/// cannot stall the UI thread.
/// </summary>
public static class TerminalTextClassifier
{
    public const int MaxScanLength = 256 * 1024;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly Regex JsonPropertyRegex = Create(
        "\"(?:\\\\.|[^\"\\\\])*\"(?=\\s*:)");
    private static readonly Regex QuotedStringRegex = Create(
        "\"(?:\\\\.|[^\"\\\\])*\"|'(?:''|[^'])*'");
    private static readonly Regex NumberRegex = Create(
        @"(?<![\w.])-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?(?![\w.])");
    private static readonly Regex StructuredKeywordRegex = Create(
        @"\b(?:true|false|null|yes|no|on|off)\b",
        RegexOptions.IgnoreCase);
    private static readonly Regex YamlPropertyRegex = Create(
        @"(?m)^\s*(?:-\s*)?(?<key>[A-Za-z_][\w.-]*)(?=\s*:)");
    private static readonly Regex YamlCommentRegex = Create(@"(?m)#.*$");

    private static readonly Regex CriticalKeywordRegex = Create(
        @"\b(?:critical|fatal|panic|error|err|failed|failure|exception|denied|corrupt(?:ed|ion)?)\b",
        RegexOptions.IgnoreCase);
    private static readonly Regex WarningKeywordRegex = Create(
        @"\b(?:warning|warn|caution|deprecated|timeout|timed\s+out|retry|unstable|degraded)\b",
        RegexOptions.IgnoreCase);
    private static readonly Regex DangerousCommandRegex = Create(
        @"(?im)(?:^|[;&|]\s*)(?:sudo\s+)?(?:rm\s+-[^\r\n\s]*[rf][^\r\n\s]*\s+|mkfs(?:\.\w+)?\s+|dd\s+[^\r\n]*\bof=|shutdown\b|reboot\b|poweroff\b|halt\b|format(?:-volume)?\b|remove-item\b[^\r\n]*(?:-recurse|-force)|clear-disk\b|stop-computer\b|restart-computer\b|drop\s+(?:database|schema|table)\b|truncate\s+table\b)");

    public static IReadOnlyList<TerminalTextSpan> Classify(
        string? text,
        bool includeSyntax = true,
        bool includeSeverity = true)
    {
        if (string.IsNullOrEmpty(text) || (!includeSyntax && !includeSeverity))
            return [];

        var scanned = text.Length <= MaxScanLength ? text : text[..MaxScanLength];
        var spans = new List<TerminalTextSpan>();

        try
        {
            if (includeSyntax)
                AddStructuredSyntax(scanned, spans);

            if (includeSeverity)
            {
                AddMatches(scanned, WarningKeywordRegex, TerminalTextHighlightKind.Warning, spans);
                AddMatches(scanned, CriticalKeywordRegex, TerminalTextHighlightKind.Critical, spans);
                AddMatches(scanned, DangerousCommandRegex, TerminalTextHighlightKind.Critical, spans);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Highlighting is decorative. Keep the unmodified text visible on timeout.
        }

        return spans
            .Distinct()
            .OrderBy(span => Priority(span.Kind))
            .ThenBy(span => span.Start)
            .ToArray();
    }

    private static void AddStructuredSyntax(string text, List<TerminalTextSpan> spans)
    {
        var isJson = LooksLikeJson(text);
        var yamlProperties = isJson ? null : YamlPropertyRegex.Matches(text);
        var isYaml = !isJson && yamlProperties is { Count: > 0 } &&
            (yamlProperties.Count > 1 || text.Contains('\n') || text.TrimStart().StartsWith("---"));

        if (!isJson && !isYaml)
            return;

        if (isYaml)
            AddMatches(text, YamlCommentRegex, TerminalTextHighlightKind.Comment, spans);

        AddMatches(text, QuotedStringRegex, TerminalTextHighlightKind.String, spans);
        AddMatches(text, NumberRegex, TerminalTextHighlightKind.Number, spans);
        AddMatches(text, StructuredKeywordRegex, TerminalTextHighlightKind.Keyword, spans);

        if (isJson)
        {
            AddMatches(text, JsonPropertyRegex, TerminalTextHighlightKind.Property, spans);
        }
        else
        {
            foreach (Match match in yamlProperties!)
            {
                var key = match.Groups["key"];
                if (key.Success)
                    spans.Add(new TerminalTextSpan(key.Index, key.Length, TerminalTextHighlightKind.Property));
            }
        }
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 2 ||
            (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(trimmed);
            return true;
        }
        catch (JsonException)
        {
            // Partial command output is still useful to color when it has JSON shape.
            return JsonPropertyRegex.IsMatch(trimmed);
        }
    }

    private static void AddMatches(
        string text,
        Regex regex,
        TerminalTextHighlightKind kind,
        List<TerminalTextSpan> spans)
    {
        foreach (Match match in regex.Matches(text))
        {
            if (match.Success && match.Length > 0)
                spans.Add(new TerminalTextSpan(match.Index, match.Length, kind));
        }
    }

    private static int Priority(TerminalTextHighlightKind kind) => kind switch
    {
        TerminalTextHighlightKind.Comment => 0,
        TerminalTextHighlightKind.String => 1,
        TerminalTextHighlightKind.Number => 2,
        TerminalTextHighlightKind.Keyword => 3,
        TerminalTextHighlightKind.Property => 4,
        TerminalTextHighlightKind.Warning => 10,
        TerminalTextHighlightKind.Critical => 11,
        _ => 0,
    };

    private static Regex Create(string pattern, RegexOptions options = RegexOptions.None) =>
        new(
            pattern,
            options | RegexOptions.Compiled | RegexOptions.CultureInvariant,
            RegexTimeout);
}
