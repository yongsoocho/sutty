using sutty.Core.Sftp;
using sutty.UI.Services;
using System.Collections.Concurrent;

internal static class TransferCenterServiceSelfTests
{
    public static async Task RunAsync(string scratch)
    {
        var queuePath = Path.Combine(scratch, "transfer-center-queue.json");
        var queue = new SftpTransferQueueStore(queuePath);
        using var service = new TransferCenterService(queue, watchFileSystem: false);

        var retryJob = CreateJob(
            "failed-only-job",
            SftpQueueJobState.Failed,
            ("failed-target", SftpQueueTargetState.Failed),
            ("interrupted-target", SftpQueueTargetState.Interrupted),
            ("completed-target", SftpQueueTargetState.Succeeded));
        queue.Upsert(retryJob);
        service.RefreshNow();

        Assert(!service.CanExecute(retryJob, TransferCenterAction.RetryFailed),
            "unregistered transfer executors keep controls disabled");
        var unavailable = await service.ExecuteAsync(
            retryJob.Id,
            TransferCenterAction.RetryFailed);
        Assert(unavailable.Status == TransferCenterControlStatus.ExecutorUnavailable &&
               unavailable.EligibleTargetCount == 1,
            "unregistered retry fails closed without changing durable state");

        var executor = new RecordingExecutor();
        using (service.RegisterExecutor("failed-target", executor))
        {
            Assert(service.CanExecute(retryJob, TransferCenterAction.RetryFailed),
                "registered target executor enables failed retry");
            var retried = await service.ExecuteAsync(
                retryJob.Id,
                TransferCenterAction.RetryFailed);
            Assert(retried.Status == TransferCenterControlStatus.Accepted &&
                   retried.AcceptedTargetCount == 1,
                "failed-only retry accepts the failed target");
            Assert(executor.Calls.SequenceEqual(
                    [("failed-target", TransferCenterAction.RetryFailed)]),
                "failed-only retry never replays interrupted or successful targets");
        }
        Assert(!service.CanExecute(retryJob, TransferCenterAction.RetryFailed),
            "disposing executor registration disables the control");

        var actionJob = CreateJob(
            "action-routing-job",
            SftpQueueJobState.Running,
            ("running-target", SftpQueueTargetState.Running),
            ("paused-target", SftpQueueTargetState.Paused),
            ("failed-action-target", SftpQueueTargetState.Failed));
        queue.Upsert(actionJob);
        var runningExecutor = new RecordingExecutor();
        var pausedExecutor = new RecordingExecutor();
        var failedExecutor = new RecordingExecutor();
        using var runningRegistration = service.RegisterExecutor(
            "running-target",
            runningExecutor);
        using var pausedRegistration = service.RegisterExecutor(
            "paused-target",
            pausedExecutor);
        using var failedRegistration = service.RegisterExecutor(
            "failed-action-target",
            failedExecutor);

        var paused = await service.ExecuteAsync(actionJob.Id, TransferCenterAction.Pause);
        Assert(paused.EligibleTargetCount == 1 &&
               runningExecutor.Calls.Contains(("running-target", TransferCenterAction.Pause)),
            "pause routes only to running or pending targets");
        var resumed = await service.ExecuteAsync(actionJob.Id, TransferCenterAction.Resume);
        Assert(resumed.EligibleTargetCount == 1 &&
               pausedExecutor.Calls.Contains(("paused-target", TransferCenterAction.Resume)),
            "resume routes only to paused or interrupted targets");
        var cancelled = await service.ExecuteAsync(actionJob.Id, TransferCenterAction.Cancel);
        Assert(cancelled.EligibleTargetCount == 2 &&
               failedExecutor.Calls.All(call => call.Action != TransferCenterAction.Cancel),
            "cancel never rewrites an already failed result as cancelled");

        var busyJob = CreateJob(
            "busy-job",
            SftpQueueJobState.Failed,
            ("busy-target", SftpQueueTargetState.Failed));
        queue.Upsert(busyJob);
        var blocking = new BlockingExecutor();
        using var busyRegistration = service.RegisterExecutor("busy-target", blocking);
        var firstControl = service.ExecuteAsync(busyJob.Id, TransferCenterAction.RetryFailed);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicateControl = await service.ExecuteAsync(
            busyJob.Id,
            TransferCenterAction.RetryFailed);
        Assert(duplicateControl.Status == TransferCenterControlStatus.Busy,
            "parallel controls for one durable job are serialized");
        blocking.Release.TrySetResult();
        Assert((await firstControl).Status == TransferCenterControlStatus.Accepted,
            "the original serialized control completes normally");

        var completed = CreateJob(
            "completed-removal-job",
            SftpQueueJobState.Completed,
            ("completed-removal-target", SftpQueueTargetState.Succeeded));
        queue.Upsert(completed);
        Assert(!service.RemoveCompleted(actionJob.Id) && queue.Get(actionJob.Id) is not null,
            "remove completed refuses a non-completed job");
        Assert(service.RemoveCompleted(completed.Id) && queue.Get(completed.Id) is null,
            "remove completed deletes only the completed queue record");

        var snapshotChanged = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.SnapshotChanged += (_, args) =>
        {
            if (args.Jobs.Any(job => job.Id == "live-refresh-job"))
                snapshotChanged.TrySetResult();
        };
        queue.Upsert(CreateJob(
            "live-refresh-job",
            SftpQueueJobState.Pending,
            ("live-refresh-target", SftpQueueTargetState.Pending)));
        await snapshotChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(service.Snapshot.Any(job => job.Id == "live-refresh-job"),
            "queue mutation events refresh the shared transfer snapshot");

        var watchedQueuePath = Path.Combine(scratch, "watched-transfer-center-queue.json");
        var watchedQueue = new SftpTransferQueueStore(watchedQueuePath);
        using var watchedService = new TransferCenterService(watchedQueue);
        var externalRefresh = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        watchedService.SnapshotChanged += (_, args) =>
        {
            if (args.Jobs.Any(job => job.Id == "external-live-refresh-job"))
                externalRefresh.TrySetResult();
        };
        new SftpTransferQueueStore(watchedQueuePath).Upsert(CreateJob(
            "external-live-refresh-job",
            SftpQueueJobState.Pending,
            ("external-live-refresh-target", SftpQueueTargetState.Pending)));
        await externalRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(watchedService.Snapshot.Any(job => job.Id == "external-live-refresh-job"),
            "file changes from another queue producer refresh the live snapshot");

        Console.WriteLine("Transfer Center routing and concurrency self-tests passed.");
    }

    private static SftpQueuedJob CreateJob(
        string id,
        SftpQueueJobState state,
        params (string Id, SftpQueueTargetState State)[] targets) => new()
    {
        Id = id,
        Mode = targets.Length > 1 ? SftpQueueMode.FanOut : SftpQueueMode.Single,
        Direction = SftpTransferDirection.Upload,
        SourcePath = "C:\\source.txt",
        DestinationPath = "/srv/source.txt",
        State = state,
        Targets = targets.Select(target => new SftpQueuedTarget
        {
            Id = target.Id,
            DisplayName = target.Id,
            SourcePath = "C:\\source.txt",
            DestinationPath = "/srv/source.txt",
            State = target.State,
        }).ToList(),
    };

    private static void Assert(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException($"Self-test failed: {description}.");
    }

    private sealed class RecordingExecutor : ITransferCenterExecutor
    {
        public ConcurrentBag<(string TargetId, TransferCenterAction Action)> Calls { get; } = [];

        public bool CanExecute(
            SftpQueuedJob job,
            SftpQueuedTarget target,
            TransferCenterAction action) => true;

        public Task<TransferCenterExecutorResult> ExecuteAsync(
            SftpQueuedJob job,
            SftpQueuedTarget target,
            TransferCenterAction action,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((target.Id, action));
            return Task.FromResult(TransferCenterExecutorResult.Success());
        }
    }

    private sealed class BlockingExecutor : ITransferCenterExecutor
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanExecute(
            SftpQueuedJob job,
            SftpQueuedTarget target,
            TransferCenterAction action) => true;

        public async Task<TransferCenterExecutorResult> ExecuteAsync(
            SftpQueuedJob job,
            SftpQueuedTarget target,
            TransferCenterAction action,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return TransferCenterExecutorResult.Success();
        }
    }
}
