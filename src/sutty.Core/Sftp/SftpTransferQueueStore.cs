using System.Diagnostics;
using System.Security.Cryptography;
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
    Paused,
    Interrupted,
    Failed,
    Completed,
    Cancelled,
}

public enum SftpQueueTargetState
{
    Pending,
    Running,
    Paused,
    Interrupted,
    Failed,
    Succeeded,
    Cancelled,
}

public enum SftpTransferQueueChangeKind
{
    Recovered,
    Upserted,
    TargetUpdated,
    Removed,
    CompletedRemoved,
}

public sealed class SftpTransferQueueChangedEventArgs : EventArgs
{
    public SftpTransferQueueChangedEventArgs(
        SftpTransferQueueChangeKind kind,
        string? jobId = null)
    {
        Kind = kind;
        JobId = jobId;
    }

    public SftpTransferQueueChangeKind Kind { get; }

    public string? JobId { get; }
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
    private static readonly object TargetLeaseGate = new();
    private static readonly Dictionary<TargetLeaseKey, TargetLeaseClaim> TargetLeases = [];
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _targetLeaseScope;
    private readonly string _lockDirectory;
    private readonly string _documentLockPath;
    private readonly string _runtimeOwnerId = ProcessRuntimeOwnerId;

    public static SftpTransferQueueStore Default { get; } = new();

    public SftpTransferQueueStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "sutty",
            "sftp-transfer-queue.json"));
        _targetLeaseScope = NormalizeTargetLeaseScope(_path);
        _lockDirectory = Path.Combine(Path.GetDirectoryName(_path)!, ".sutty-transfer-locks");
        _documentLockPath = Path.Combine(
            _lockDirectory,
            $"queue-{HashLockIdentity(_targetLeaseScope)}.lock");
    }

    public event EventHandler<SftpTransferQueueChangedEventArgs>? Changed;

    public string StoragePath => _path;

    public IReadOnlyList<SftpQueuedJob> GetAll()
    {
        lock (_gate)
        {
            using var documentLock = AcquireDocumentMutationLock();
            return ReadDocument(tolerateInvalid: false).Jobs
                .OrderBy(job => job.CreatedAtUtc)
                .ToArray();
        }
    }

    public IReadOnlyList<SftpQueuedJob> RecoverIncomplete()
    {
        IReadOnlyList<SftpQueuedJob> recovered;
        var changed = false;
        lock (_gate)
        {
            using var documentLock = AcquireDocumentMutationLock();
            var document = ReadDocument(tolerateInvalid: false);
            for (var index = 0; index < document.Jobs.Count; index++)
            {
                var job = document.Jobs[index];
                if (job.State is SftpQueueJobState.Completed or SftpQueueJobState.Cancelled)
                    continue;

                // Runtime owner ids distinguish restarts for diagnostics, but are not proof
                // that a worker is still alive. A Running target must hold its OS execution
                // lease even in this process; otherwise a failed final queue write could
                // leave an unmanageable orphan until the whole application restarts.
                var targets = job.Targets.Select(target =>
                    target.State == SftpQueueTargetState.Running &&
                    !IsTargetExecutionLeaseActive(job.Id, target.Id)
                        ? target with
                        {
                            State = SftpQueueTargetState.Interrupted,
                            UpdatedAtUtc = DateTimeOffset.UtcNow,
                        }
                        : target).ToList();
                var state = ComputeJobState(targets);
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
            recovered = document.Jobs
                .Where(job => job.State is not SftpQueueJobState.Completed and
                               not SftpQueueJobState.Cancelled)
                .OrderBy(job => job.CreatedAtUtc)
                .ToArray();
        }

        if (changed)
            PublishChange(SftpTransferQueueChangeKind.Recovered);
        return recovered;
    }

    public void Upsert(SftpQueuedJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var normalized = Normalize(job) with { RuntimeOwnerId = _runtimeOwnerId };
        lock (_gate)
        {
            using var documentLock = AcquireDocumentMutationLock();
            var document = ReadDocument(tolerateInvalid: false);
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

        PublishChange(SftpTransferQueueChangeKind.Upserted, normalized.Id);
    }

    public SftpQueuedJob? Get(string id)
    {
        id = NormalizeId(id);
        lock (_gate)
        {
            using var documentLock = AcquireDocumentMutationLock();
            return ReadDocument(tolerateInvalid: false).Jobs.FirstOrDefault(job => job.Id == id);
        }
    }

    /// <summary>
    /// Atomically acquires a process-local claim plus an operating-system file lease for one
    /// durable queue target. Store instances and Sutty processes addressing the same queue
    /// cannot execute that target concurrently. The owner token is intentionally never
    /// persisted. Dispose the returned lease after every success, cancellation, or failure path.
    /// </summary>
    public bool TryAcquireTargetLease(
        string jobId,
        string targetId,
        string ownerToken,
        out IDisposable? lease)
    {
        jobId = NormalizeId(jobId);
        targetId = NormalizeTargetId(targetId);
        ownerToken = NormalizeTargetLeaseOwner(ownerToken);
        return TryAcquireTargetLeaseCore(jobId, targetId, ownerToken, out lease);
    }

    /// <summary>
    /// Acquires a target lease only while the durable job and target are still eligible
    /// for an explicit retry. The state check and claim are serialized with all in-process
    /// leases for the queue file, preventing a stale Files view from re-running a target
    /// completed by another view. The execution claim is also exclusive across processes.
    /// </summary>
    public bool TryAcquireRetryTargetLease(
        string jobId,
        string targetId,
        string ownerToken,
        out IDisposable? lease)
    {
        jobId = NormalizeId(jobId);
        targetId = NormalizeTargetId(targetId);
        ownerToken = NormalizeTargetLeaseOwner(ownerToken);
        var key = new TargetLeaseKey(_targetLeaseScope, jobId, targetId);
        lock (TargetLeaseGate)
        {
            if (TargetLeases.ContainsKey(key))
            {
                lease = null;
                return false;
            }

            if (!TryOpenTargetExecutionLock(jobId, targetId, out var executionLock))
            {
                lease = null;
                return false;
            }

            var claim = new TargetLeaseClaim(ownerToken, executionLock!);
            lock (_gate)
            {
                using var documentLock = AcquireDocumentMutationLock();
                var job = ReadDocument(tolerateInvalid: false).Jobs
                    .FirstOrDefault(item => item.Id == jobId);
                var target = job?.Targets.FirstOrDefault(item => item.Id == targetId);
                if (job is null || target is null ||
                    job.State == SftpQueueJobState.Completed ||
                    target.State is not (SftpQueueTargetState.Pending or
                        SftpQueueTargetState.Running or
                        SftpQueueTargetState.Paused or
                        SftpQueueTargetState.Interrupted or
                        SftpQueueTargetState.Failed or
                        SftpQueueTargetState.Cancelled))
                {
                    claim.Dispose();
                    lease = null;
                    return false;
                }
            }

            TargetLeases.Add(key, claim);
            lease = new TargetLease(() => ReleaseTargetLease(key, claim));
        }

        return true;
    }

    private bool TryAcquireTargetLeaseCore(
        string jobId,
        string targetId,
        string ownerToken,
        out IDisposable? lease)
    {
        lease = null;

        var key = new TargetLeaseKey(_targetLeaseScope, jobId, targetId);
        lock (TargetLeaseGate)
        {
            if (TargetLeases.ContainsKey(key))
                return false;

            if (!TryOpenTargetExecutionLock(jobId, targetId, out var executionLock))
                return false;

            var claim = new TargetLeaseClaim(ownerToken, executionLock!);
            TargetLeases.Add(key, claim);
            lease = new TargetLease(() => ReleaseTargetLease(key, claim));
        }

        return true;
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
        var changed = false;
        lock (_gate)
        {
            using var documentLock = AcquireDocumentMutationLock();
            var document = ReadDocument(tolerateInvalid: false);
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
            changed = true;
        }

        if (changed)
            PublishChange(SftpTransferQueueChangeKind.TargetUpdated, jobId);
    }

    public bool Delete(string id)
    {
        id = NormalizeId(id);
        var removed = false;
        lock (_gate)
        {
            using var documentLock = AcquireDocumentMutationLock();
            var document = ReadDocument(tolerateInvalid: false);
            if (document.Jobs.RemoveAll(job => job.Id == id) == 0)
                return false;
            WriteDocument(document);
            removed = true;
        }

        if (removed)
            PublishChange(SftpTransferQueueChangeKind.Removed, id);
        return removed;
    }

    public bool DeleteCompleted(string id)
    {
        id = NormalizeId(id);
        var removed = false;
        lock (_gate)
        {
            using var documentLock = AcquireDocumentMutationLock();
            var document = ReadDocument(tolerateInvalid: false);
            var index = document.Jobs.FindIndex(job => job.Id == id);
            if (index < 0 || document.Jobs[index].State != SftpQueueJobState.Completed)
                return false;
            document.Jobs.RemoveAt(index);
            WriteDocument(document);
            removed = true;
        }

        if (removed)
            PublishChange(SftpTransferQueueChangeKind.CompletedRemoved, id);
        return removed;
    }

    public int DeleteAllCompleted()
    {
        var removed = 0;
        lock (_gate)
        {
            using var documentLock = AcquireDocumentMutationLock();
            var document = ReadDocument(tolerateInvalid: false);
            removed = document.Jobs.RemoveAll(job => job.State == SftpQueueJobState.Completed);
            if (removed > 0)
                WriteDocument(document);
        }

        if (removed > 0)
            PublishChange(SftpTransferQueueChangeKind.CompletedRemoved);
        return removed;
    }

    public static IReadOnlySet<string> GetRetryTargetIds(SftpQueuedJob job) => job.Targets
        .Where(target => target.State is SftpQueueTargetState.Pending or
                                      SftpQueueTargetState.Running or
                                      SftpQueueTargetState.Paused or
                                      SftpQueueTargetState.Interrupted or
                                      SftpQueueTargetState.Failed or
                                      SftpQueueTargetState.Cancelled)
        .Select(target => target.Id)
        .ToHashSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> GetFailedTargetIds(SftpQueuedJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.Targets
            .Where(target => target.State == SftpQueueTargetState.Failed)
            .Select(target => target.Id)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Projects durable byte counters to a user-facing percentage. A completed job is
    /// authoritative even when an older producer did not persist its final byte counter.
    /// </summary>
    public static double GetProgressPercentage(SftpQueuedJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.State == SftpQueueJobState.Completed)
            return 100;

        var transferred = job.Targets.Sum(target => Math.Max(0, target.BytesTransferred));
        var total = job.Targets.Sum(target => Math.Max(0, target.TotalBytes));
        return total > 0
            ? Math.Clamp(transferred * 100d / total, 0, 100)
            : 0;
    }

    private static SftpQueueJobState ComputeJobState(IReadOnlyCollection<SftpQueuedTarget> targets)
    {
        if (targets.Count > 0 && targets.All(target => target.State == SftpQueueTargetState.Succeeded))
            return SftpQueueJobState.Completed;
        if (targets.Any(target => target.State == SftpQueueTargetState.Running))
            return SftpQueueJobState.Running;
        if (targets.Any(target => target.State == SftpQueueTargetState.Paused))
            return SftpQueueJobState.Paused;
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

    private static string NormalizeTargetLeaseOwner(string value)
    {
        value = value?.Trim() ?? "";
        if (value.Length is < 1 or > 256 || value.Any(char.IsControl))
            throw new ArgumentException("The transfer target lease owner is invalid.", nameof(value));
        return value;
    }

    private static string NormalizeTargetLeaseScope(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
    }

    private static string HashLockIdentity(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private FileStream AcquireDocumentMutationLock()
    {
        Directory.CreateDirectory(_lockDirectory);
        var wait = Stopwatch.StartNew();
        Exception? lastError = null;
        while (wait.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                return new FileStream(
                    _documentLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException error)
            {
                lastError = error;
                Thread.Sleep(20);
            }
        }

        throw new IOException("The transfer queue is busy in another process.", lastError);
    }

    private bool TryOpenTargetExecutionLock(
        string jobId,
        string targetId,
        out FileStream? executionLock)
    {
        executionLock = null;
        try
        {
            Directory.CreateDirectory(_lockDirectory);
            var identity = $"{_targetLeaseScope}\0{jobId}\0{targetId}";
            var lockPath = Path.Combine(
                _lockDirectory,
                $"target-{HashLockIdentity(identity)}.lock");
            executionLock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            executionLock?.Dispose();
            executionLock = null;
            return false;
        }
    }

    private bool IsTargetExecutionLeaseActive(string jobId, string targetId)
    {
        if (!TryOpenTargetExecutionLock(jobId, targetId, out var probe))
            return true;
        probe!.Dispose();
        return false;
    }

    private static void ReleaseTargetLease(TargetLeaseKey key, TargetLeaseClaim claim)
    {
        var removed = false;
        lock (TargetLeaseGate)
        {
            if (TargetLeases.TryGetValue(key, out var current) && ReferenceEquals(current, claim))
            {
                TargetLeases.Remove(key);
                removed = true;
            }
        }

        if (removed)
            claim.Dispose();
    }

    private SftpTransferQueueDocument ReadDocument(bool tolerateInvalid = true)
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
        catch (JsonException error) when (!tolerateInvalid)
        {
            throw new InvalidDataException("The transfer queue document is invalid.", error);
        }
        catch (Exception error) when (tolerateInvalid &&
            error is IOException or UnauthorizedAccessException or JsonException)
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

    private void PublishChange(SftpTransferQueueChangeKind kind, string? jobId = null)
    {
        var handlers = Changed;
        if (handlers is null)
            return;

        var args = new SftpTransferQueueChangedEventArgs(kind, jobId);
        foreach (EventHandler<SftpTransferQueueChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // Queue persistence must never fail because a live UI observer was closed.
            }
        }
    }

    private readonly record struct TargetLeaseKey(string Scope, string JobId, string TargetId);

    private sealed class TargetLeaseClaim : IDisposable
    {
        private FileStream? _executionLock;

        public TargetLeaseClaim(string ownerToken, FileStream executionLock)
        {
            OwnerToken = ownerToken;
            _executionLock = executionLock;
        }

        public string OwnerToken { get; }

        public void Dispose() => Interlocked.Exchange(ref _executionLock, null)?.Dispose();
    }

    private sealed class TargetLease : IDisposable
    {
        private Action? _release;

        public TargetLease(Action release) => _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
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
