using sutty.Core.Sftp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace sutty.UI.Services;

public enum TransferCenterAction
{
    Pause,
    Resume,
    RetryFailed,
    Cancel,
}

public enum TransferCenterControlStatus
{
    Accepted,
    PartiallyAccepted,
    NotFound,
    NoEligibleTargets,
    ExecutorUnavailable,
    Busy,
    Failed,
}

public sealed record TransferCenterExecutorResult(bool Accepted, string? Message = null)
{
    public static TransferCenterExecutorResult Success(string? message = null) =>
        new(true, message);

    public static TransferCenterExecutorResult Rejected(string? message = null) =>
        new(false, message);
}

public sealed record TransferCenterControlResult(
    TransferCenterControlStatus Status,
    int EligibleTargetCount = 0,
    int AcceptedTargetCount = 0,
    string? Message = null)
{
    public bool Accepted => Status is TransferCenterControlStatus.Accepted or
        TransferCenterControlStatus.PartiallyAccepted;
}

/// <summary>
/// A live execution surface for one durable target. Implementations must return success only
/// after the real worker accepted the request. Resume/retry implementations must acquire the
/// queue target lease before starting work; this keeps staging and verification owned by the
/// existing transfer engine instead of the global UI.
/// </summary>
public interface ITransferCenterExecutor
{
    bool CanExecute(
        SftpQueuedJob job,
        SftpQueuedTarget target,
        TransferCenterAction action);

    Task<TransferCenterExecutorResult> ExecuteAsync(
        SftpQueuedJob job,
        SftpQueuedTarget target,
        TransferCenterAction action,
        CancellationToken cancellationToken);
}

public sealed class TransferQueueSnapshotChangedEventArgs : EventArgs
{
    public TransferQueueSnapshotChangedEventArgs(IReadOnlyList<SftpQueuedJob> jobs) =>
        Jobs = jobs;

    public IReadOnlyList<SftpQueuedJob> Jobs { get; }
}

/// <summary>
/// Process-wide live projection and control broker for the durable SFTP queue. The broker never
/// opens SSH/SFTP connections or touches staging files. It delegates execution to a registered
/// session owner and stays fail-closed when that owner is unavailable.
/// </summary>
public sealed class TransferCenterService : IDisposable
{
    private static readonly Lazy<TransferCenterService> Shared = new(
        () => new TransferCenterService(SftpTransferQueueStore.Default));

    private readonly object _gate = new();
    private readonly object _refreshGate = new();
    private readonly SftpTransferQueueStore _queue;
    private readonly Dictionary<string, List<ExecutorRegistration>> _executors =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlightJobs = new(StringComparer.Ordinal);
    private readonly Timer _refreshTimer;
    private readonly FileSystemWatcher? _watcher;
    private IReadOnlyList<SftpQueuedJob> _snapshot = [];
    private bool _disposed;

    public TransferCenterService(
        SftpTransferQueueStore queue,
        bool watchFileSystem = true)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _queue.Changed += Queue_Changed;
        _refreshTimer = new Timer(
            _ => RefreshNow(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _watcher = watchFileSystem ? TryCreateWatcher(queue.StoragePath) : null;
        RefreshNow();
    }

    public static TransferCenterService Default => Shared.Value;

    public event EventHandler<TransferQueueSnapshotChangedEventArgs>? SnapshotChanged;

    public IReadOnlyList<SftpQueuedJob> Snapshot
    {
        get
        {
            lock (_gate)
                return _snapshot;
        }
    }

    public IDisposable RegisterExecutor(string targetId, ITransferCenterExecutor executor)
    {
        targetId = NormalizeTargetId(targetId);
        ArgumentNullException.ThrowIfNull(executor);
        var registration = new ExecutorRegistration(targetId, executor);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_executors.TryGetValue(targetId, out var registrations))
            {
                registrations = [];
                _executors.Add(targetId, registrations);
            }
            registrations.Add(registration);
        }

        ScheduleRefresh();
        return new RegistrationToken(() => RemoveRegistration(registration));
    }

    public bool CanExecute(SftpQueuedJob job, TransferCenterAction action)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.RequiresEditReview && action != TransferCenterAction.Cancel) return false;
        lock (_gate)
        {
            if (_disposed)
                return false;
        }
        foreach (var target in EligibleTargets(job, action))
        {
            if (FindExecutor(job, target, action) is not null)
                return true;
        }
        return false;
    }

    public async Task<TransferCenterControlResult> ExecuteAsync(
        string jobId,
        TransferCenterAction action,
        CancellationToken cancellationToken = default)
    {
        jobId = NormalizeJobId(jobId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_inFlightJobs.Add(jobId))
                return new TransferCenterControlResult(TransferCenterControlStatus.Busy);
        }

        try
        {
            var job = _queue.Get(jobId);
            if (job is null)
                return new TransferCenterControlResult(TransferCenterControlStatus.NotFound);

            var eligible = EligibleTargets(job, action).ToArray();
            if (eligible.Length == 0)
            {
                return new TransferCenterControlResult(
                    TransferCenterControlStatus.NoEligibleTargets);
            }

            var accepted = 0;
            var attempted = 0;
            var errors = new List<string>();
            foreach (var target in eligible)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var executor = FindExecutor(job, target, action);
                if (executor is null)
                    continue;

                attempted++;
                try
                {
                    var result = await executor.ExecuteAsync(
                        job,
                        target,
                        action,
                        cancellationToken);
                    if (result.Accepted)
                        accepted++;
                    else if (!string.IsNullOrWhiteSpace(result.Message))
                        errors.Add(result.Message!);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    errors.Add(error.Message);
                }
            }

            RefreshNow();
            if (attempted == 0)
            {
                return new TransferCenterControlResult(
                    TransferCenterControlStatus.ExecutorUnavailable,
                    eligible.Length);
            }
            if (accepted == eligible.Length)
            {
                return new TransferCenterControlResult(
                    TransferCenterControlStatus.Accepted,
                    eligible.Length,
                    accepted);
            }
            if (accepted > 0)
            {
                return new TransferCenterControlResult(
                    TransferCenterControlStatus.PartiallyAccepted,
                    eligible.Length,
                    accepted,
                    JoinErrors(errors));
            }
            return new TransferCenterControlResult(
                TransferCenterControlStatus.Failed,
                eligible.Length,
                0,
                JoinErrors(errors));
        }
        finally
        {
            lock (_gate)
                _inFlightJobs.Remove(jobId);
            ScheduleRefresh();
        }
    }

    public bool RemoveCompleted(string jobId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var removed = _queue.DeleteCompleted(jobId);
        if (removed)
            RefreshNow();
        return removed;
    }

    public int RemoveAllCompleted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var removed = _queue.DeleteAllCompleted();
        if (removed > 0)
            RefreshNow();
        return removed;
    }

    public void RefreshNow()
    {
        lock (_refreshGate)
        {
            if (_disposed)
                return;

            IReadOnlyList<SftpQueuedJob> jobs;
            try
            {
                jobs = _queue.GetAll()
                    .OrderByDescending(job => job.UpdatedAtUtc)
                    .ThenByDescending(job => job.CreatedAtUtc)
                    .ToArray();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                    return;
                _snapshot = jobs;
            }
            PublishSnapshot(jobs);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _executors.Clear();
            _inFlightJobs.Clear();
        }

        _queue.Changed -= Queue_Changed;
        _watcher?.Dispose();
        _refreshTimer.Dispose();
    }

    private static IEnumerable<SftpQueuedTarget> EligibleTargets(
        SftpQueuedJob job,
        TransferCenterAction action) => job.Targets.Where(target =>
        (!job.RequiresEditReview || action == TransferCenterAction.Cancel) && (action switch
        {
            TransferCenterAction.Pause => target.State is SftpQueueTargetState.Pending or
                SftpQueueTargetState.Running,
            TransferCenterAction.Resume => target.State is SftpQueueTargetState.Paused or
                SftpQueueTargetState.Interrupted,
            TransferCenterAction.RetryFailed => target.State == SftpQueueTargetState.Failed,
            TransferCenterAction.Cancel => target.State is SftpQueueTargetState.Pending or
                SftpQueueTargetState.Running or SftpQueueTargetState.Paused or
                SftpQueueTargetState.Interrupted,
            _ => false,
        }));

    private ITransferCenterExecutor? FindExecutor(
        SftpQueuedJob job,
        SftpQueuedTarget target,
        TransferCenterAction action)
    {
        ITransferCenterExecutor[] candidates;
        lock (_gate)
        {
            if (!_executors.TryGetValue(target.Id, out var registrations))
                return null;
            candidates = registrations.Select(item => item.Executor).ToArray();
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (candidate.CanExecute(job, target, action))
                    return candidate;
            }
            catch
            {
                // A stale view registration is unavailable, never an authorization to act.
            }
        }
        return null;
    }

    private void RemoveRegistration(ExecutorRegistration registration)
    {
        lock (_gate)
        {
            if (!_executors.TryGetValue(registration.TargetId, out var registrations))
                return;
            registrations.Remove(registration);
            if (registrations.Count == 0)
                _executors.Remove(registration.TargetId);
        }
        ScheduleRefresh();
    }

    private void Queue_Changed(object? sender, SftpTransferQueueChangedEventArgs args) =>
        ScheduleRefresh();

    private void ScheduleRefresh()
    {
        if (_disposed)
            return;
        try
        {
            _refreshTimer.Change(TimeSpan.FromMilliseconds(60), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private FileSystemWatcher? TryCreateWatcher(string storagePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(storagePath)!;
            Directory.CreateDirectory(directory);
            var watcher = new FileSystemWatcher(directory, Path.GetFileName(storagePath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                    NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = false,
            };
            watcher.Changed += QueueFile_Changed;
            watcher.Created += QueueFile_Changed;
            watcher.Deleted += QueueFile_Changed;
            watcher.Renamed += QueueFile_Renamed;
            watcher.Error += QueueFile_Error;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                      ArgumentException)
        {
            return null;
        }
    }

    private void QueueFile_Changed(object sender, FileSystemEventArgs args) => ScheduleRefresh();

    private void QueueFile_Renamed(object sender, RenamedEventArgs args) => ScheduleRefresh();

    private void QueueFile_Error(object sender, ErrorEventArgs args) => ScheduleRefresh();

    private static string NormalizeTargetId(string value)
    {
        value = value?.Trim() ?? "";
        if (value.Length is < 1 or > 512 || value.Any(char.IsControl))
            throw new ArgumentException("The transfer target id is invalid.", nameof(value));
        return value;
    }

    private static string NormalizeJobId(string value)
    {
        value = value?.Trim() ?? "";
        if (value.Length is < 1 or > 128 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("The transfer job id is invalid.", nameof(value));
        }
        return value;
    }

    private static string? JoinErrors(IReadOnlyCollection<string> errors) =>
        errors.Count == 0 ? null : string.Join(" · ", errors.Distinct(StringComparer.Ordinal).Take(3));

    private void PublishSnapshot(IReadOnlyList<SftpQueuedJob> jobs)
    {
        var handlers = SnapshotChanged;
        if (handlers is null)
            return;

        var args = new TransferQueueSnapshotChangedEventArgs(jobs);
        foreach (EventHandler<TransferQueueSnapshotChangedEventArgs> handler in
                 handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // A closed view cannot make durable controls appear to have failed.
            }
        }
    }

    private sealed record ExecutorRegistration(
        string TargetId,
        ITransferCenterExecutor Executor);

    private sealed class RegistrationToken : IDisposable
    {
        private Action? _dispose;

        public RegistrationToken(Action dispose) => _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
