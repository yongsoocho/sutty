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
    int SettingsSchemaVersion);

public sealed record SupportBundleResult(
    string FilePath,
    long SizeBytes,
    string Sha256,
    IReadOnlyList<string> Entries);

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
        _ = ConnectionDiagnosticErrorCodes.NormalizeKnown(
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

        var capturedEvents = _events.Snapshot(
            normalizedCorrelationId,
            ConnectionDiagnosticEventStore.MaximumSnapshotEntries);
        var reportEvents = SelectReportEvents(capturedEvents);
        var stableErrorCode = ResolveStableErrorCode(capturedEvents);
        var report = new SupportBundleReport
        {
            SchemaVersion = SchemaVersion,
            AppVersion = DiagnosticValueNormalizer.AppVersion(context.AppVersion),
            AppBuild = DiagnosticValueNormalizer.AppBuild(context.AppBuild),
            WindowsBuild = DiagnosticValueNormalizer.WindowsBuild(context.WindowsBuild),
            ProcessArchitecture = NormalizeEnum(context.ProcessArchitecture),
            RouteType = context.RouteType.ToString(),
            AuthenticationType = context.AuthenticationType.ToString(),
            StableErrorCode = stableErrorCode,
            CorrelationId = normalizedCorrelationId,
            SettingsSchemaVersion = context.SettingsSchemaVersion,
            Events = reportEvents
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

    private static string ResolveStableErrorCode(
        IReadOnlyList<ConnectionDiagnosticEvent> capturedEvents) => capturedEvents
        .GroupBy(entry => entry.Stage)
        .Select(group => group.MaxBy(entry => entry.Sequence)!)
        .Where(entry => entry.Status is ConnectionDiagnosticStatus.Failed or
                                        ConnectionDiagnosticStatus.Cancelled)
        .MaxBy(entry => entry.Sequence)?
        .ErrorCode ?? ConnectionDiagnosticErrorCodes.None;

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
internal sealed partial class SupportBundleJsonContext : JsonSerializerContext;
