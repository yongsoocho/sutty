using sutty.Core.Sftp;
using sutty.UI.Services;

internal static class TransferPhaseSelfTests
{
    public static async Task RunAsync()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"sutty-transfer-phase-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            var source = Path.Combine(scratch, "source.txt");
            var destination = Path.Combine(scratch, "destination.txt");
            await File.WriteAllTextAsync(source, "new content");
            await File.WriteAllTextAsync(destination, "old content");
            var phases = new List<SftpTransferPhase>();
            await new LocalFileService().UploadPathAsync(source, destination,
                progress: new InlineProgress(progress =>
                {
                    phases.Add(progress.Phase);
                    if (progress.Phase is SftpTransferPhase.Verifying or SftpTransferPhase.Promoting)
                    {
                        Assert(progress.Fraction == 1, "all data can be transferred before final completion");
                        Assert(File.ReadAllText(destination) == "old content", "destination remains unchanged before promotion");
                    }
                }));
            Assert(phases.IndexOf(SftpTransferPhase.Verifying) >= 0 &&
                   phases.IndexOf(SftpTransferPhase.Promoting) > phases.IndexOf(SftpTransferPhase.Verifying) &&
                   phases[^1] == SftpTransferPhase.Completed,
                "verification and promotion precede completed progress");
            Assert(await File.ReadAllTextAsync(destination) == "new content", "completed transfer has promoted new data");

            var queue = new SftpTransferQueueStore(Path.Combine(scratch, "queue.json"));
            var job = new SftpQueuedJob
            {
                SourcePath = source,
                DestinationPath = destination,
                Targets = [new SftpQueuedTarget { Id = "host-a", SourcePath = source, DestinationPath = destination }],
            };
            queue.Upsert(job);
            queue.UpdateTarget(job.Id, "host-a", SftpQueueTargetState.Running, 11, 11, phase: SftpTransferPhase.Verifying);
            var verifying = queue.Get(job.Id)!;
            Assert(verifying.State == SftpQueueJobState.Running &&
                   verifying.Targets[0].Phase == SftpTransferPhase.Verifying &&
                   SftpTransferQueueStore.GetProgressPercentage(verifying) == 100,
                "persisted 100 percent verification is still running");
            queue.UpdateTarget(job.Id, "host-a", SftpQueueTargetState.Succeeded);
            queue.UpdateTarget(job.Id, "host-a", SftpQueueTargetState.Pending, isProgressReport: true);
            queue.UpdateTarget(job.Id, "host-a", SftpQueueTargetState.Running, isProgressReport: true);
            Assert(queue.Get(job.Id)!.State == SftpQueueJobState.Completed,
                "delayed no-phase initial reports cannot revive a synchronously completed attempt");
            queue.UpdateTarget(job.Id, "host-a", SftpQueueTargetState.Running, 11, 11, phase: SftpTransferPhase.Promoting);
            Assert(queue.Get(job.Id)!.State == SftpQueueJobState.Completed &&
                   queue.Get(job.Id)!.Targets[0].Phase == SftpTransferPhase.Completed,
                "late phase delivery cannot overwrite completed queue state");
            queue.UpdateTarget(job.Id, "host-a", SftpQueueTargetState.Failed, error: "network disconnected");
            queue.UpdateTarget(job.Id, "host-a", SftpQueueTargetState.Pending, isProgressReport: true);
            queue.UpdateTarget(job.Id, "host-a", SftpQueueTargetState.Running, isProgressReport: true);
            queue.UpdateTarget(job.Id, "host-a", SftpQueueTargetState.Running, phase: SftpTransferPhase.Transferring);
            Assert(queue.Get(job.Id)!.State == SftpQueueJobState.Failed, "late progress cannot overwrite failure");
            queue.UpdateTarget(job.Id, "host-a", SftpQueueTargetState.Running);
            queue.UpdateTarget(job.Id, "host-a", SftpQueueTargetState.Pending, isProgressReport: true);
            Assert(queue.Get(job.Id)!.Targets[0].Phase == SftpTransferPhase.Preparing,
                "explicit retry clears stale completion phase");
            Assert(queue.Get(job.Id)!.State == SftpQueueJobState.Running,
                "delayed initial pending report cannot move an explicit retry backwards");

            using var service = new TransferCenterService(queue, watchFileSystem: false);
            var failed = job with { Targets = [job.Targets[0] with { State = SftpQueueTargetState.Failed }] };
            Assert(service.GetUnavailableReason(failed, TransferCenterAction.RetryFailed) ==
                   TransferCenterUnavailableReason.OriginalConnectionRequired,
                "disabled retry identifies original connection requirement");
            Assert(service.GetUnavailableReason(failed with { RequiresEditReview = true }, TransferCenterAction.RetryFailed) ==
                   TransferCenterUnavailableReason.EditReviewRequired,
                "edit review reason takes precedence over reconnect advice");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class InlineProgress(Action<SftpTransferProgress> report) : IProgress<SftpTransferProgress>
    {
        public void Report(SftpTransferProgress value) => report(value);
    }
}
