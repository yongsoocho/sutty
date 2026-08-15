using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sutty.Core.Sftp;

public enum SftpQueueMode
{
    Single,
    FanOut,
    FanIn,
}

public enum SftpQueueJobState
{
    Pending,
    Running,
    Interrupted,
    Failed,
    Completed,
    Cancelled,
}

public enum SftpQueueTargetState
{
    Pending,
    Running,
    Interrupted,
    Failed,
    Succeeded,
    Cancelled,
}

/// <summary>A credential-free, restart-safe transfer target.</summary>
public sealed record SftpQueuedTarget
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string DestinationPath { get; init; } = "";
    public SftpQueueTargetState State { get; init; } = SftpQueueTargetState.Pending;
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Durable transfer intent. It stores stable host/profile identifiers and paths only;
/// authentication values remain in the encrypted credential vault.
/// </summary>
public sealed record SftpQueuedJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string RuntimeOwnerId { get; init; } = "";
    public SftpQueueMode Mode { get; init; }
    public SftpTransferDirection Direction { get; init; }
    public string SourcePath { get; init; } = "";
    public string DestinationPath { get; init; } = "";
    public SftpTransferOptions Options { get; init; } = SftpTransferOptions.Default;
    public SftpQueueJobState State { get; init; } = SftpQueueJobState.Pending;
    public List<SftpQueuedTarget> Targets { get; init; } = [];
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Atomic JSON store for the transfer queue. Running work is converted to Interrupted
/// on recovery; already successful servers stay successful and are never selected by
/// <see cref="GetRetryTargetIds"/>.
/// </summary>
public sealed class SftpTransferQueueStore
{
    private const int MaximumJobs = 256;
    private const int MaximumTargets = 16;
    private static readonly string ProcessRuntimeOwnerId = Guid.NewGuid().ToString("N");
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _runtimeOwnerId = ProcessRuntimeOwnerId;

    public static SftpTransferQueueStore Default { get; } = new();

    public SftpTransferQueueStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "sutty",
            "sftp-transfer-queue.json");
    }

    public IReadOnlyList<SftpQueuedJob> GetAll()
    {
        lock (_gate)
            return ReadDocument().Jobs.OrderBy(job => job.CreatedAtUtc).ToArray();
    }

    public IReadOnlyList<SftpQueuedJob> RecoverIncomplete()
    {
        lock (_gate)
        {
            var document = ReadDocument();
            var changed = false;
            for (var index = 0; index < document.Jobs.Count; index++)
            {
                var job = document.Jobs[index];
                if (job.State is SftpQueueJobState.Completed or SftpQueueJobState.Cancelled)
                    continue;

                // Re-reading the queue inside the same running app must not make active
                // transfers look crashed. A different owner means the prior process ended.
                if (string.Equals(job.RuntimeOwnerId, _runtimeOwnerId, StringComparison.Ordinal))
                    continue;

                var targets = job.Targets.Select(target =>
                    target.State == SftpQueueTargetState.Running
                        ? target with
                        {
                            State = SftpQueueTargetState.Interrupted,
                            UpdatedAtUtc = DateTimeOffset.UtcNow,
                        }
                        : target).ToList();
                var state = job.State == SftpQueueJobState.Running
                    ? SftpQueueJobState.Interrupted
                    : job.State;
                if (state != job.State || !targets.SequenceEqual(job.Targets))
                {
                    document.Jobs[index] = job with
                    {
                        State = state,
                        Targets = targets,
                        RuntimeOwnerId = _runtimeOwnerId,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                    };
                    changed = true;
                }
            }

            if (changed)
                WriteDocument(document);
            return document.Jobs
                .Where(job => job.State is not SftpQueueJobState.Completed and
                              not SftpQueueJobState.Cancelled)
                .OrderBy(job => job.CreatedAtUtc)
                .ToArray();
        }
    }

    public void Upsert(SftpQueuedJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var normalized = Normalize(job) with { RuntimeOwnerId = _runtimeOwnerId };
        lock (_gate)
        {
            var document = ReadDocument();
            var index = document.Jobs.FindIndex(item => item.Id == normalized.Id);
            if (index >= 0)
            {
                normalized = normalized with { CreatedAtUtc = document.Jobs[index].CreatedAtUtc };
                document.Jobs[index] = normalized;
            }
            else
            {
                document.Jobs.RemoveAll(item =>
                    (item.State is SftpQueueJobState.Completed or SftpQueueJobState.Cancelled) &&
                    item.UpdatedAtUtc < DateTimeOffset.UtcNow.AddDays(-7));
                if (document.Jobs.Count >= MaximumJobs)
                    throw new InvalidOperationException($"At most {MaximumJobs} transfer jobs are retained.");
                document.Jobs.Add(normalized);
            }
            WriteDocument(document);
        }
    }

    public SftpQueuedJob? Get(string id)
    {
        id = NormalizeId(id);
        lock (_gate)
            return ReadDocument().Jobs.FirstOrDefault(job => job.Id == id);
    }

    public void UpdateTarget(
        string jobId,
        string targetId,
        SftpQueueTargetState state,
        long bytesTransferred = 0,
        long totalBytes = 0,
        string? error = null)
    {
        jobId = NormalizeId(jobId);
        targetId = NormalizeTargetId(targetId);
        lock (_gate)
        {
            var document = ReadDocument();
            var jobIndex = document.Jobs.FindIndex(job => job.Id == jobId);
            if (jobIndex < 0)
                return;

            var job = document.Jobs[jobIndex];
            var targetIndex = job.Targets.FindIndex(target => target.Id == targetId);
            if (targetIndex < 0)
                return;

            var targets = job.Targets.ToList();
            var previous = targets[targetIndex];
            targets[targetIndex] = previous with
            {
                State = state,
                BytesTransferred = Math.Max(previous.BytesTransferred, bytesTransferred),
                TotalBytes = Math.Max(previous.TotalBytes, totalBytes),
                Error = string.IsNullOrWhiteSpace(error) ? null : Limit(error, 2_048),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            var jobState = ComputeJobState(targets);
            document.Jobs[jobIndex] = job with
            {
                State = jobState,
                Targets = targets,
                RuntimeOwnerId = state == SftpQueueTargetState.Running
                    ? _runtimeOwnerId
                    : job.RuntimeOwnerId,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            WriteDocument(document);
        }
    }

    public bool Delete(string id)
    {
        id = NormalizeId(id);
        lock (_gate)
        {
            var document = ReadDocument();
            if (document.Jobs.RemoveAll(job => job.Id == id) == 0)
                return false;
            WriteDocument(document);
            return true;
        }
    }

    public static IReadOnlySet<string> GetRetryTargetIds(SftpQueuedJob job) => job.Targets
        .Where(target => target.State is SftpQueueTargetState.Pending or
                                      SftpQueueTargetState.Running or
                                      SftpQueueTargetState.Interrupted or
                                      SftpQueueTargetState.Failed or
                                      SftpQueueTargetState.Cancelled)
        .Select(target => target.Id)
        .ToHashSet(StringComparer.Ordinal);

    private static SftpQueueJobState ComputeJobState(IReadOnlyCollection<SftpQueuedTarget> targets)
    {
        if (targets.Count > 0 && targets.All(target => target.State == SftpQueueTargetState.Succeeded))
            return SftpQueueJobState.Completed;
        if (targets.Any(target => target.State == SftpQueueTargetState.Running))
            return SftpQueueJobState.Running;
        if (targets.Any(target => target.State == SftpQueueTargetState.Failed))
            return SftpQueueJobState.Failed;
        if (targets.Any(target => target.State == SftpQueueTargetState.Interrupted))
            return SftpQueueJobState.Interrupted;
        if (targets.Count > 0 && targets.All(target => target.State == SftpQueueTargetState.Cancelled))
            return SftpQueueJobState.Cancelled;
        return SftpQueueJobState.Pending;
    }

    private static SftpQueuedJob Normalize(SftpQueuedJob job)
    {
        var id = NormalizeId(job.Id);
        if (string.IsNullOrWhiteSpace(job.SourcePath) || string.IsNullOrWhiteSpace(job.DestinationPath))
            throw new ArgumentException("Transfer source and destination paths are required.", nameof(job));
        if (job.SourcePath.Length > 32_768 || job.DestinationPath.Length > 32_768)
            throw new ArgumentException("A transfer path is too long.", nameof(job));
        if (job.Targets.Count is < 1 or > MaximumTargets)
            throw new ArgumentOutOfRangeException(nameof(job), $"A job requires 1-{MaximumTargets} targets.");

        var targets = job.Targets.Select(target => target with
        {
            Id = NormalizeTargetId(target.Id),
            DisplayName = Limit(target.DisplayName.Trim(), 256),
            SourcePath = Limit(target.SourcePath, 32_768),
            DestinationPath = Limit(target.DestinationPath, 32_768),
            Error = string.IsNullOrWhiteSpace(target.Error) ? null : Limit(target.Error, 2_048),
        }).ToList();
        if (targets.Select(target => target.Id).Distinct(StringComparer.Ordinal).Count() != targets.Count)
            throw new ArgumentException("Transfer target identifiers must be unique.", nameof(job));

        return job with
        {
            Id = id,
            SourcePath = job.SourcePath.Trim(),
            DestinationPath = job.DestinationPath.Trim(),
            Options = (job.Options ?? SftpTransferOptions.Default).Normalize(),
            Targets = targets,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private static string NormalizeId(string value)
    {
        value = value?.Trim() ?? "";
        if (value.Length is < 1 or > 128 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("The transfer job id is invalid.", nameof(value));
        return value;
    }

    private static string NormalizeTargetId(string value)
    {
        value = value?.Trim() ?? "";
        if (value.Length is < 1 or > 512 || value.Any(char.IsControl))
            throw new ArgumentException("The transfer target id is invalid.", nameof(value));
        return value;
    }

    private SftpTransferQueueDocument ReadDocument()
    {
        try
        {
            if (!File.Exists(_path))
                return new SftpTransferQueueDocument();
            var document = JsonSerializer.Deserialize(
                File.ReadAllText(_path, Encoding.UTF8),
                SftpQueueJsonContext.Default.SftpTransferQueueDocument)
                ?? new SftpTransferQueueDocument();
            document.Jobs ??= [];
            if (document.Jobs.Count > MaximumJobs)
                throw new JsonException("The transfer queue contains too many jobs.");
            return document;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SftpTransferQueueDocument();
        }
    }

    private void WriteDocument(SftpTransferQueueDocument document)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(
                document,
                SftpQueueJsonContext.Default.SftpTransferQueueDocument);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
                File.Replace(temporaryPath, _path, null);
            else
                File.Move(temporaryPath, _path);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}

public sealed class SftpTransferQueueDocument
{
    public int Version { get; set; } = 1;
    public List<SftpQueuedJob> Jobs { get; set; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SftpTransferQueueDocument))]
internal sealed partial class SftpQueueJsonContext : JsonSerializerContext;
