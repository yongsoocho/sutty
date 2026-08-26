using sutty.Core.Models;
using sutty.Core.Routing;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sutty.Core.Diagnostics;

/// <summary>
/// Reviewed support-bundle inputs. The public contract intentionally has no endpoint,
/// hostname, username, filesystem path, transcript, or command-output field.
/// </summary>
public sealed record SupportBundleContext(
    string AppVersion,
    string AppBuild,
    string WindowsBuild,
    Architecture ProcessArchitecture,
    ConnectionRouteType RouteType,
    SshAuthMethod AuthenticationType,
    string StableErrorCode,
    string CorrelationId,
    int SettingsSchemaVersion,
    ConnectionDiagnosticStage? FallbackFailureStage = null,
    string? ExpectedDiagnosticSnapshotSha256 = null);

public sealed record SupportBundleDiagnosticPreview(
    string StableErrorCode,
    int EventCount,
    string SnapshotSha256,
    ConnectionDiagnosticStage? FailureStage,
    ConnectionDiagnosticStatus? FailureStatus,
    long? FailureSequence,
    DateTimeOffset? FailureTimestampUtc);

public sealed record SupportBundleResult(
    string FilePath,
    long SizeBytes,
    string Sha256,
    IReadOnlyList<string> Entries);

/// <summary>
/// Raised when the selected UI context and the captured diagnostic event snapshot
/// identify different failures. The message intentionally contains no endpoint or
/// other user-provided value.
/// </summary>
public sealed class SupportBundleDiagnosticCodeMismatchException : Exception
{
    public SupportBundleDiagnosticCodeMismatchException()
        : base("Support-bundle diagnostic code mismatch.")
    {
    }
}

public sealed class SupportBundleDiagnosticSnapshotChangedException : Exception
{
    public SupportBundleDiagnosticSnapshotChangedException()
        : base("Support-bundle diagnostic snapshot changed.")
    {
    }
}

/// <summary>
/// Creates a deterministic two-entry ZIP from an explicit allowlist. The archive is
/// fully written and flushed in the destination directory before an atomic rename.
/// </summary>
public sealed class SupportBundleService
{
    public const int SchemaVersion = 1;
    public const int MaximumRecentEvents = 128;
    public const int ArchiveEntryCount = 2;
    public const int MaximumReportBytes = 256 * 1024;
    public const int MaximumArchiveBytes = 512 * 1024;

    public const string ManifestEntryName = "manifest.json";
    public const string ReportEntryName = "report.json";

    private const string BundleFormat = "sutty-support-bundle";
    private static readonly DateTimeOffset DeterministicZipTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] AllowedEntries = [ManifestEntryName, ReportEntryName];

    private readonly ConnectionDiagnosticEventStore _events;

    public SupportBundleService(ConnectionDiagnosticEventStore events)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
    }

    public SupportBundleDiagnosticPreview Preview(
        string correlationId,
        string requestedStableErrorCode = ConnectionDiagnosticErrorCodes.None,
        ConnectionDiagnosticStage? fallbackFailureStage = null)
    {
        var normalizedCorrelationId = DiagnosticValueNormalizer.CorrelationId(correlationId);
        var normalizedRequestedCode = ConnectionDiagnosticErrorCodes.NormalizeKnown(
            requestedStableErrorCode,
            nameof(requestedStableErrorCode));
        if (fallbackFailureStage is { } stage && !Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(fallbackFailureStage));
        ValidateRequestedFallbackStage(
            normalizedRequestedCode,
            fallbackFailureStage,
            nameof(fallbackFailureStage));
        var capturedEvents = _events.Snapshot(
            normalizedCorrelationId,
            ConnectionDiagnosticEventStore.MaximumSnapshotEntries);
        return CaptureDiagnostics(
            capturedEvents,
            normalizedRequestedCode,
            fallbackFailureStage).Preview;
    }

    public SupportBundleResult Create(
        string destinationPath,
        SupportBundleContext context,
        bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(context);

        var fullPath = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A support bundle must use the .zip extension.", nameof(destinationPath));
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException("The support-bundle destination directory does not exist.");
        if (Directory.Exists(fullPath))
            throw new IOException("The support-bundle destination is a directory.");
        if (!overwrite && File.Exists(fullPath))
            throw new IOException("The support-bundle destination already exists.");

        var normalizedCorrelationId = DiagnosticValueNormalizer.CorrelationId(context.CorrelationId);
        var requestedStableErrorCode = ConnectionDiagnosticErrorCodes.NormalizeKnown(
            context.StableErrorCode,
            nameof(context.StableErrorCode));
        if (!Enum.IsDefined(context.ProcessArchitecture))
            throw new ArgumentOutOfRangeException(nameof(context.ProcessArchitecture));
        if (!Enum.IsDefined(context.RouteType))
            throw new ArgumentOutOfRangeException(nameof(context.RouteType));
        if (!Enum.IsDefined(context.AuthenticationType))
            throw new ArgumentOutOfRangeException(nameof(context.AuthenticationType));
        if (context.SettingsSchemaVersion is < 0 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(context.SettingsSchemaVersion));
        if (context.FallbackFailureStage is { } fallbackFailureStage &&
            !Enum.IsDefined(fallbackFailureStage))
        {
            throw new ArgumentOutOfRangeException(nameof(context.FallbackFailureStage));
        }
        ValidateRequestedFallbackStage(
            requestedStableErrorCode,
            context.FallbackFailureStage,
            nameof(context.FallbackFailureStage));
        var expectedSnapshotSha256 = context.ExpectedDiagnosticSnapshotSha256;
        if (expectedSnapshotSha256 is not null &&
            !IsLowercaseSha256(expectedSnapshotSha256))
        {
            throw new ArgumentException(
                "Expected diagnostic snapshot SHA-256 is invalid.",
                nameof(context.ExpectedDiagnosticSnapshotSha256));
        }

        var capturedEvents = _events.Snapshot(
            normalizedCorrelationId,
            ConnectionDiagnosticEventStore.MaximumSnapshotEntries);
        var diagnosticCapture = CaptureDiagnostics(
            capturedEvents,
            requestedStableErrorCode,
            context.FallbackFailureStage);
        if (expectedSnapshotSha256 is not null &&
            !string.Equals(
                expectedSnapshotSha256,
                diagnosticCapture.Preview.SnapshotSha256,
                StringComparison.Ordinal))
        {
            throw new SupportBundleDiagnosticSnapshotChangedException();
        }
        var report = new SupportBundleReport
        {
            SchemaVersion = SchemaVersion,
            AppVersion = DiagnosticValueNormalizer.AppVersion(context.AppVersion),
            AppBuild = DiagnosticValueNormalizer.AppBuild(context.AppBuild),
            WindowsBuild = DiagnosticValueNormalizer.WindowsBuild(context.WindowsBuild),
            ProcessArchitecture = NormalizeEnum(context.ProcessArchitecture),
            RouteType = context.RouteType.ToString(),
            AuthenticationType = context.AuthenticationType.ToString(),
            StableErrorCode = diagnosticCapture.Preview.StableErrorCode,
            CorrelationId = normalizedCorrelationId,
            SettingsSchemaVersion = context.SettingsSchemaVersion,
            Events = diagnosticCapture.ReportEvents
                .Select(ToReportEvent)
                .ToArray(),
        };
        var reportBytes = JsonSerializer.SerializeToUtf8Bytes(
            report,
            SupportBundleJsonContext.Default.SupportBundleReport);
        if (reportBytes.Length > MaximumReportBytes)
            throw new InvalidDataException("The support-bundle report exceeds its size limit.");

        var reportSha256 = Hash(reportBytes);
        var manifest = new SupportBundleManifest
        {
            SchemaVersion = SchemaVersion,
            Format = BundleFormat,
            Files = AllowedEntries.ToArray(),
            ReportSizeBytes = reportBytes.LongLength,
            ReportSha256 = reportSha256,
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            SupportBundleJsonContext.Default.SupportBundleManifest);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteArchive(temporaryPath, manifestBytes, reportBytes);
            var archiveSize = new FileInfo(temporaryPath).Length;
            if (archiveSize > MaximumArchiveBytes)
                throw new InvalidDataException("The support bundle exceeds its archive size limit.");
            var archiveSha256 = HashFile(temporaryPath);
            CommitAtomically(temporaryPath, fullPath, overwrite);

            return new SupportBundleResult(
                fullPath,
                archiveSize,
                archiveSha256,
                Array.AsReadOnly(AllowedEntries.ToArray()));
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // A cleanup failure must not hide the original archive or commit error.
            }
        }
    }

    private static DiagnosticCapture CaptureDiagnostics(
        IReadOnlyList<ConnectionDiagnosticEvent> capturedEvents,
        string requestedStableErrorCode,
        ConnectionDiagnosticStage? fallbackFailureStage)
    {
        var reportEvents = SelectReportEvents(capturedEvents);
        var latestFailure = ResolveLatestUnresolvedFailure(capturedEvents);
        var eventStableErrorCode = latestFailure?.ErrorCode ??
            ConnectionDiagnosticErrorCodes.None;
        if (requestedStableErrorCode != ConnectionDiagnosticErrorCodes.None &&
            eventStableErrorCode != ConnectionDiagnosticErrorCodes.None &&
            !string.Equals(
                requestedStableErrorCode,
                eventStableErrorCode,
                StringComparison.Ordinal))
        {
            throw new SupportBundleDiagnosticCodeMismatchException();
        }

        var useRequestedFallback =
            requestedStableErrorCode != ConnectionDiagnosticErrorCodes.None &&
            CanUseRequestedFallback(capturedEvents, fallbackFailureStage);
        var stableErrorCode = eventStableErrorCode != ConnectionDiagnosticErrorCodes.None
            ? eventStableErrorCode
            : useRequestedFallback
                ? requestedStableErrorCode
                : ConnectionDiagnosticErrorCodes.None;
        var reportEventModels = reportEvents
            .Select(ToReportEvent)
            .ToArray();
        var snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(
            reportEventModels,
            typeof(SupportBundleEventReport[]),
            SupportBundleJsonContext.Default);
        return new DiagnosticCapture(
            reportEvents,
            new SupportBundleDiagnosticPreview(
                stableErrorCode,
                reportEvents.Count,
                Hash(snapshotBytes),
                latestFailure?.Stage ?? (useRequestedFallback ? fallbackFailureStage : null),
                latestFailure?.Status ?? (useRequestedFallback
                    ? requestedStableErrorCode == ConnectionDiagnosticErrorCodes.ConnectionCancelled
                        ? ConnectionDiagnosticStatus.Cancelled
                        : ConnectionDiagnosticStatus.Failed
                    : null),
                latestFailure?.Sequence,
                latestFailure?.TimestampUtc));
    }

    /// <summary>
    /// A caller-supplied failure is used only when the event stream cannot contradict
    /// it. Legacy callers without stage provenance may fall back only for a completely
    /// empty correlation. A stage-aware caller may also recover a missing failure append
    /// after earlier stages were recorded, while a later success or skip for that same
    /// stage explicitly resolves the stale failure.
    /// </summary>
    private static bool CanUseRequestedFallback(
        IReadOnlyList<ConnectionDiagnosticEvent> capturedEvents,
        ConnectionDiagnosticStage? fallbackFailureStage)
    {
        if (fallbackFailureStage is not { } stage)
            return capturedEvents.Count == 0;

        var latestForStage = capturedEvents
            .Where(entry => entry.Stage == stage)
            .MaxBy(entry => entry.Sequence);
        return latestForStage is null || latestForStage.Status is
            ConnectionDiagnosticStatus.NotStarted or ConnectionDiagnosticStatus.Running;
    }

    /// <summary>
    /// Validates only caller-supplied fallback provenance. Diagnostic events may use
    /// a different stage while closing a causal operation, so their existing event
    /// contract remains authoritative and is intentionally not constrained here.
    /// </summary>
    private static void ValidateRequestedFallbackStage(
        string stableErrorCode,
        ConnectionDiagnosticStage? fallbackFailureStage,
        string parameterName)
    {
        if (fallbackFailureStage is not { } stage)
            return;

        if (stableErrorCode == ConnectionDiagnosticErrorCodes.ConnectionCancelled ||
            stableErrorCode == ConnectionDiagnosticErrorCodes.UnexpectedFailure)
        {
            return;
        }

        var expectedStage = stableErrorCode switch
        {
            ConnectionDiagnosticErrorCodes.InputInvalid =>
                ConnectionDiagnosticStage.InputValidation,
            ConnectionDiagnosticErrorCodes.DnsLookupFailed or
            ConnectionDiagnosticErrorCodes.TcpConnectionRefused or
            ConnectionDiagnosticErrorCodes.TcpTimedOut or
            ConnectionDiagnosticErrorCodes.TcpUnreachable or
            ConnectionDiagnosticErrorCodes.TcpFailed =>
                ConnectionDiagnosticStage.DnsAndTcp,
            ConnectionDiagnosticErrorCodes.RoutePolicyBlocked or
            ConnectionDiagnosticErrorCodes.RouteSocks5Refused or
            ConnectionDiagnosticErrorCodes.RouteProxyRefused or
            ConnectionDiagnosticErrorCodes.RouteJumpRefused or
            ConnectionDiagnosticErrorCodes.RouteAuthenticationFailed or
            ConnectionDiagnosticErrorCodes.RouteTimedOut or
            ConnectionDiagnosticErrorCodes.RouteFailed =>
                ConnectionDiagnosticStage.ProxyOrJumpRoute,
            ConnectionDiagnosticErrorCodes.SshHandshakeTimedOut or
            ConnectionDiagnosticErrorCodes.SshHandshakeFailed =>
                ConnectionDiagnosticStage.SshHandshake,
            ConnectionDiagnosticErrorCodes.HostKeyChanged or
            ConnectionDiagnosticErrorCodes.HostKeyRejected =>
                ConnectionDiagnosticStage.HostKey,
            ConnectionDiagnosticErrorCodes.AuthenticationFailed or
            ConnectionDiagnosticErrorCodes.AuthenticationTimedOut or
            ConnectionDiagnosticErrorCodes.AuthenticationKeyFileMissing or
            ConnectionDiagnosticErrorCodes.AuthenticationKeyFileDenied =>
                ConnectionDiagnosticStage.Authentication,
            ConnectionDiagnosticErrorCodes.PtyRequestFailed =>
                ConnectionDiagnosticStage.Pty,
            ConnectionDiagnosticErrorCodes.SftpSubsystemUnavailable =>
                ConnectionDiagnosticStage.SftpSubsystem,
            ConnectionDiagnosticErrorCodes.PortForwardingFailed =>
                ConnectionDiagnosticStage.PortForwarding,
            _ => (ConnectionDiagnosticStage?)null,
        };

        if (expectedStage != stage)
        {
            throw new ArgumentException(
                "The fallback failure stage does not match the diagnostic error code.",
                parameterName);
        }
    }

    private static SupportBundleEventReport ToReportEvent(ConnectionDiagnosticEvent entry) => new()
    {
        Sequence = entry.Sequence,
        TimestampUtc = entry.TimestampUtc.ToUniversalTime(),
        CorrelationId = entry.CorrelationId,
        Stage = entry.Stage.ToString(),
        Status = entry.Status.ToString(),
        ErrorCode = entry.ErrorCode,
        ElapsedMilliseconds = entry.ElapsedMilliseconds,
    };

    private static IReadOnlyList<ConnectionDiagnosticEvent> SelectReportEvents(
        IReadOnlyList<ConnectionDiagnosticEvent> capturedEvents)
    {
        var selected = new Dictionary<long, ConnectionDiagnosticEvent>();
        foreach (var latestForStage in capturedEvents
                     .GroupBy(entry => entry.Stage)
                     .Select(group => group.MaxBy(entry => entry.Sequence)!))
        {
            selected[latestForStage.Sequence] = latestForStage;
        }

        for (var index = capturedEvents.Count - 1;
             index >= 0 && selected.Count < MaximumRecentEvents;
             index--)
        {
            var entry = capturedEvents[index];
            selected.TryAdd(entry.Sequence, entry);
        }

        return selected.Values
            .OrderBy(entry => entry.Sequence)
            .ToArray();
    }

    private static ConnectionDiagnosticEvent? ResolveLatestUnresolvedFailure(
        IReadOnlyList<ConnectionDiagnosticEvent> capturedEvents) => capturedEvents
        .GroupBy(entry => entry.Stage)
        .Select(group => group.MaxBy(entry => entry.Sequence)!)
        .Where(entry => entry.Status is ConnectionDiagnosticStatus.Failed or
                                        ConnectionDiagnosticStatus.Cancelled)
        .MaxBy(entry => entry.Sequence);

    private static string NormalizeEnum<TEnum>(TEnum value)
        where TEnum : struct, Enum => value.ToString().ToLowerInvariant();

    private static void WriteArchive(
        string temporaryPath,
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> reportBytes)
    {
        using var output = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.WriteThrough);
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, ManifestEntryName, manifestBytes);
            WriteEntry(archive, ReportEntryName, reportBytes);
        }
        output.Flush(flushToDisk: true);
    }

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        ReadOnlySpan<byte> content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = DeterministicZipTimestamp;
        entry.ExternalAttributes = 0;
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static void CommitAtomically(string temporaryPath, string destinationPath, bool overwrite)
    {
        if (!overwrite)
        {
            File.Move(temporaryPath, destinationPath);
            return;
        }

        if (!File.Exists(destinationPath))
        {
            try
            {
                File.Move(temporaryPath, destinationPath);
                return;
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                // Another writer won the race. Replace that complete file atomically below.
            }
        }

        try
        {
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
    }

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static bool IsLowercaseSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record DiagnosticCapture(
        IReadOnlyList<ConnectionDiagnosticEvent> ReportEvents,
        SupportBundleDiagnosticPreview Preview);
}

internal sealed class SupportBundleManifest
{
    public int SchemaVersion { get; set; }
    public string Format { get; set; } = "";
    public string[] Files { get; set; } = [];
    public long ReportSizeBytes { get; set; }
    public string ReportSha256 { get; set; } = "";
}

internal sealed class SupportBundleReport
{
    public int SchemaVersion { get; set; }
    public string AppVersion { get; set; } = "";
    public string AppBuild { get; set; } = "";
    public string WindowsBuild { get; set; } = "";
    public string ProcessArchitecture { get; set; } = "";
    public string RouteType { get; set; } = "";
    public string AuthenticationType { get; set; } = "";
    public string StableErrorCode { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public int SettingsSchemaVersion { get; set; }
    public SupportBundleEventReport[] Events { get; set; } = [];
}

internal sealed class SupportBundleEventReport
{
    public long Sequence { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public string CorrelationId { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Status { get; set; } = "";
    public string ErrorCode { get; set; } = "";
    public long? ElapsedMilliseconds { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SupportBundleManifest))]
[JsonSerializable(typeof(SupportBundleReport))]
[JsonSerializable(typeof(SupportBundleEventReport[]))]
internal sealed partial class SupportBundleJsonContext : JsonSerializerContext;
