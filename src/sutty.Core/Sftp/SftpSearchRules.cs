namespace sutty.Core.Sftp;

internal static class SftpSearchRules
{
    public const int DefaultMaximumResults = 500;
    public const int MaximumResults = 1_000;

    public static (string Query, int MaximumResults) Normalize(
        string query,
        int maximumResults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var normalized = query.Trim();
        if (normalized.Length > 128 || normalized.Any(char.IsControl))
            throw new ArgumentException("The remote filename query is invalid.", nameof(query));
        if (maximumResults is < 1 or > MaximumResults)
            throw new ArgumentOutOfRangeException(
                nameof(maximumResults),
                $"Search results must be limited to 1-{MaximumResults} entries.");
        return (normalized, maximumResults);
    }
}
