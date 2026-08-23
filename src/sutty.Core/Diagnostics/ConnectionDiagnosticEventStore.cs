using System.Text.RegularExpressions;

namespace sutty.Core.Diagnostics;

/// <summary>
/// A structured diagnostic event that is safe to include in a support bundle. It has no
/// free-text message, endpoint, hostname, username, path, transcript, or command output.
/// </summary>
public sealed record ConnectionDiagnosticEvent(
    long Sequence,
    DateTimeOffset TimestampUtc,
    string CorrelationId,
    ConnectionDiagnosticStage Stage,
    ConnectionDiagnosticStatus Status,
    string ErrorCode,
    long? ElapsedMilliseconds);

/// <summary>Thread-safe bounded in-memory storage for recent Connection Doctor events.</summary>
public sealed class ConnectionDiagnosticEventStore
{
    public const int DefaultCapacity = 256;
    public const int MaximumCapacity = 2_048;
    public const int MaximumSnapshotEntries = 512;
    public const long MaximumElapsedMilliseconds = 86_400_000;

    private readonly object _gate = new();
    private readonly LinkedList<ConnectionDiagnosticEvent> _entries = [];
    private readonly int _capacity;
    private long _nextSequence;

    /// <summary>Process-wide event stream used by live Session and UI integrations.</summary>
    public static ConnectionDiagnosticEventStore Shared { get; } = new();

    public ConnectionDiagnosticEventStore(int capacity = DefaultCapacity)
    {
        if (capacity is < 1 or > MaximumCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                $"Capacity must be between 1 and {MaximumCapacity}.");
        }
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    public ConnectionDiagnosticEvent Append(
        string correlationId,
        ConnectionDiagnosticStage stage,
        ConnectionDiagnosticStatus status,
        string errorCode = ConnectionDiagnosticErrorCodes.None,
        TimeSpan? elapsed = null)
    {
        var normalizedCorrelationId = DiagnosticValueNormalizer.CorrelationId(correlationId);
        var normalizedErrorCode = ConnectionDiagnosticErrorCodes.NormalizeKnown(
            errorCode,
            nameof(errorCode));
        DiagnosticContract.ValidateOutcome(stage, status, normalizedErrorCode);

        long? elapsedMilliseconds = null;
        if (elapsed is { } duration)
        {
            var totalMilliseconds = duration.TotalMilliseconds;
            if (!double.IsFinite(totalMilliseconds) || totalMilliseconds < 0 ||
                totalMilliseconds > MaximumElapsedMilliseconds)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsed));
            }
            elapsedMilliseconds = checked((long)Math.Round(
                totalMilliseconds,
                MidpointRounding.AwayFromZero));
        }

        lock (_gate)
        {
            var entry = new ConnectionDiagnosticEvent(
                ++_nextSequence,
                DateTimeOffset.UtcNow,
                normalizedCorrelationId,
                stage,
                status,
                normalizedErrorCode,
                elapsedMilliseconds);
            _entries.AddLast(entry);
            while (_entries.Count > _capacity)
                EvictOldestRedundantEvent();
            return entry;
        }
    }

    public ConnectionDiagnosticEvent Append(
        string correlationId,
        ConnectionDiagnosticResult result,
        TimeSpan? elapsed = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Append(
            correlationId,
            result.Stage,
            result.Status,
            result.ErrorCode,
            elapsed);
    }

    public IReadOnlyList<ConnectionDiagnosticEvent> Snapshot(int maxEntries = MaximumSnapshotEntries)
    {
        ValidateSnapshotLimit(maxEntries);
        lock (_gate)
            return _entries.TakeLast(maxEntries).ToArray();
    }

    public IReadOnlyList<ConnectionDiagnosticEvent> Snapshot(
        string correlationId,
        int maxEntries = MaximumSnapshotEntries)
    {
        var normalizedCorrelationId = DiagnosticValueNormalizer.CorrelationId(correlationId);
        ValidateSnapshotLimit(maxEntries);
        lock (_gate)
        {
            return _entries
                .Where(entry => string.Equals(
                    entry.CorrelationId,
                    normalizedCorrelationId,
                    StringComparison.Ordinal))
                .TakeLast(maxEntries)
                .ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
    }

    /// <summary>
    /// Keeps the global store bounded without allowing one noisy connection/stage to
    /// evict the only retained result for an otherwise quiet connection stage. When
    /// possible, the oldest event that has a newer event for the same correlation and
    /// stage is removed. If every entry is already the sole retained result for its
    /// pair, the oldest entry is removed to preserve the hard capacity bound.
    /// </summary>
    private void EvictOldestRedundantEvent()
    {
        var latestPairs = new HashSet<(string CorrelationId, ConnectionDiagnosticStage Stage)>();
        LinkedListNode<ConnectionDiagnosticEvent>? oldestRedundant = null;
        for (var node = _entries.Last; node is not null; node = node.Previous)
        {
            var pair = (node.Value.CorrelationId, node.Value.Stage);
            if (!latestPairs.Add(pair))
                oldestRedundant = node;
        }

        _entries.Remove(oldestRedundant ?? _entries.First!);
    }

    private static void ValidateSnapshotLimit(int maxEntries)
    {
        if (maxEntries is < 1 or > MaximumSnapshotEntries)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
    }
}

internal static partial class DiagnosticValueNormalizer
{
    private const int MaximumVersionLength = 64;
    private const int MaximumBuildLength = 128;
    private const int MaximumWindowsBuildLength = 32;

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex CorrelationPattern();

    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z.+_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z._+-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex BuildPattern();

    [GeneratedRegex("^[0-9]{1,6}(?:\\.[0-9]{1,6}){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsBuildPattern();

    internal static string CorrelationId(string? value)
    {
        var normalized = (value ?? "")
            .Trim()
            .Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();
        if (!CorrelationPattern().IsMatch(normalized))
        {
            throw new ArgumentException(
                "CorrelationId must be a GUID or 32 lowercase hexadecimal characters.",
                nameof(value));
        }
        return normalized;
    }

    internal static string AppVersion(string? value) =>
        MatchBounded(value, MaximumVersionLength, VersionPattern(), "app version");

    internal static string AppBuild(string? value) =>
        MatchBounded(value, MaximumBuildLength, BuildPattern(), "app build");

    internal static string WindowsBuild(string? value) =>
        MatchBounded(value, MaximumWindowsBuildLength, WindowsBuildPattern(), "Windows build");

    private static string MatchBounded(
        string? value,
        int maximumLength,
        Regex pattern,
        string description)
    {
        var normalized = (value ?? "").Trim();
        if (normalized.Length is < 1 || normalized.Length > maximumLength ||
            !pattern.IsMatch(normalized))
        {
            throw new ArgumentException($"The {description} value is invalid.", nameof(value));
        }
        return normalized;
    }
}
