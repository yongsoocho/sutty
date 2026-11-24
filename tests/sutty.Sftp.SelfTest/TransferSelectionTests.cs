using sutty.Core.Sftp;

internal static class TransferSelectionTests
{
    public static void Run(string directory, Action<bool, string> assert)
    {
        var file = Path.Combine(directory, "selection-queue.json");
        var queue = new SftpTransferQueueStore(file);
        var endpoint = SftpEndpointIdentity.Create("app.example", 22, "Admin", "fingerprint-a");
        assert(endpoint == SftpEndpointIdentity.Create("APP.example", 22, "Admin", "fingerprint-a") &&
               endpoint != SftpEndpointIdentity.Create("app.example", 22, "admin", "fingerprint-a") &&
               endpoint != SftpEndpointIdentity.Create("app.example", 22, "Admin", "fingerprint-b"),
            "a verified target survives routing changes, while case-sensitive accounts and key rotation stay isolated");

        var jobs = Enumerable.Range(1, 1000).Select(i => new SftpQueuedJob
        {
            SourcePath = $"file-{i}", DestinationPath = $"/files/{i}",
            Targets = [new() { Id = endpoint }],
        }).ToArray();
        queue.EnqueueBatch(jobs[..20]);
        assert(queue.GetAll().Count == 20, "all 20 selected files enter the same transaction");
        queue.EnqueueBatch(jobs[20..]);
        var restored = new SftpTransferQueueStore(file);
        assert(restored.RecoverIncomplete().Select(job => job.Id).ToHashSet().SetEquals(jobs.Select(job => job.Id)),
            "all 1000 individual selections survive restart");
        try
        {
            queue.EnqueueBatch([jobs[1] with { Id = "new-id" }, jobs[0]]);
            throw new Exception("duplicate queue admission succeeded");
        }
        catch (ArgumentException) { }
        assert(restored.Get("new-id") is null, "rejected selection is atomic");
        var result = new SftpTransferResult(SftpTransferDirection.Upload, "src", "dst", 0, 0, 0, null, TimeSpan.Zero, 1);
        queue.UpdateTarget(jobs[0].Id, endpoint, SftpQueueTargetState.Succeeded, result: result);
        queue.UpdateTarget(jobs[1].Id, endpoint, SftpQueueTargetState.Succeeded,
            result: result with { FilesTransferred = 2, FilesSkipped = 1, ResumedBytes = 8192 });
        assert(restored.Get(jobs[0].Id)!.WasSkipped && !restored.Get(jobs[1].Id)!.WasSkipped &&
               restored.Get(jobs[1].Id)!.Targets[0].ResumedBytes == 8192,
            "all-skipped and mixed outcomes survive reload, including resumed byte count");
        queue.UpdateTarget(jobs[2].Id, endpoint, SftpQueueTargetState.Cancelled);
        assert(!queue.TryAcquireRetryTargetLease(jobs[2].Id, endpoint, "selection", out _, pendingOnly: true),
            "a worker waiting its turn cannot restart an explicitly cancelled target");
    }
}
