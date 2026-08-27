using sutty.Core.Sftp;
using System.Collections.Concurrent;

Assert(RemotePath.Normalize(@"/srv/a\b") == @"/srv/a\b",
    "POSIX backslash filename is preserved");
Assert(RemotePath.Combine("/srv", @"a\b") == @"/srv/a\b",
    "POSIX path combine preserves backslash");
Assert(RemotePath.GetDirectory(@"/srv/a\b") == "/srv",
    "POSIX path parent treats only slash as separator");
Assert(RemotePath.Normalize("/srv/one/../two/./") == "/srv/two",
    "dot segments normalize without escaping root");

var scratch = Path.Combine(Path.GetTempPath(), $"sutty-sftp-self-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratch);
try
{
    var source = Path.Combine(scratch, "source.txt");
    var remote = Path.Combine(scratch, "remote");
    var downloads = Path.Combine(scratch, "downloads");
    Directory.CreateDirectory(remote);
    Directory.CreateDirectory(downloads);
    await File.WriteAllTextAsync(source, "first");

    var files = new LocalFileService();
    var uploadProgress = new List<double>();
    await files.UploadFileAsync(source, remote, progress: new InlineProgress<double>(uploadProgress.Add));
    var remoteFile = Path.Combine(remote, "source.txt");
    Assert(await File.ReadAllTextAsync(remoteFile) == "first", "upload writes complete file");
    Assert(uploadProgress.Count >= 2 && uploadProgress[0] == 0.0,
        "upload progress starts at zero");
    Assert(uploadProgress[^1] == 1.0,
        "completed upload progress is 100 percent");
    Assert(uploadProgress.Zip(uploadProgress.Skip(1), (left, right) => right >= left).All(value => value),
        "upload progress is monotonic");

    var emptySource = Path.Combine(scratch, "empty.txt");
    await File.WriteAllBytesAsync(emptySource, []);
    var emptyProgress = new List<double>();
    await files.UploadFileAsync(
        emptySource, remote, progress: new InlineProgress<double>(emptyProgress.Add));
    Assert(emptyProgress.SequenceEqual([0.0, 1.0]),
        "zero-byte upload reports zero then 100 percent");

    await File.WriteAllTextAsync(source, "second");
    await AssertThrowsAsync<IOException>(
        () => files.UploadFileAsync(source, remote, overwrite: false),
        "upload collision does not overwrite");
    Assert(await File.ReadAllTextAsync(remoteFile) == "first",
        "rejected upload preserves destination");

    await files.UploadFileAsync(source, remote, overwrite: true);
    Assert(await File.ReadAllTextAsync(remoteFile) == "second",
        "confirmed upload replaces destination");

    var localDownload = Path.Combine(downloads, "saved.txt");
    await File.WriteAllTextAsync(localDownload, "keep");
    await AssertThrowsAsync<IOException>(
        () => files.DownloadFileAsync(remoteFile, localDownload, overwrite: false),
        "download collision does not overwrite");
    Assert(await File.ReadAllTextAsync(localDownload) == "keep",
        "rejected download preserves destination");

    await files.DownloadFileAsync(remoteFile, localDownload, overwrite: true);
    Assert(await File.ReadAllTextAsync(localDownload) == "second",
        "confirmed download replaces destination");

    Assert(SftpTransferOptions.Default.RetryEnabled,
        "SFTP retries are enabled by default");
    Assert(SftpTransferOptions.Default.MaxRetries == 3,
        "SFTP default retry count is three");

    var sourceTree = Path.Combine(scratch, "source-tree");
    Directory.CreateDirectory(Path.Combine(sourceTree, "nested", "empty"));
    await File.WriteAllTextAsync(Path.Combine(sourceTree, "root.json"), "{\"ok\":true}");
    await File.WriteAllTextAsync(Path.Combine(sourceTree, "nested", "child.yaml"), "value: 42");
    var uploadedTree = Path.Combine(remote, "uploaded-tree");
    var treeProgress = new List<SftpTransferProgress>();
    var uploadTreeResult = await files.UploadPathAsync(
        sourceTree,
        uploadedTree,
        progress: new InlineProgress<SftpTransferProgress>(treeProgress.Add));
    Assert(uploadTreeResult.FilesTransferred == 2, "recursive upload transfers every file");
    Assert(File.Exists(Path.Combine(uploadedTree, "nested", "child.yaml")),
        "recursive upload preserves nested paths");
    Assert(Directory.Exists(Path.Combine(uploadedTree, "nested", "empty")),
        "recursive upload preserves empty directories");
    Assert(treeProgress.Last().Phase == SftpTransferPhase.Completed &&
           treeProgress.Last().Fraction == 1.0,
        "recursive upload reports completion");

    var enumeratedTree = await files.EnumerateTreeAsync(uploadedTree);
    Assert(enumeratedTree.Any(entry => entry.RelativePath.EndsWith("child.yaml")),
        "folder tree enumeration includes nested files");
    Assert(enumeratedTree.Any(entry => entry.Entry.IsDirectory &&
                                      entry.RelativePath.EndsWith("empty")),
        "folder tree enumeration includes empty folders");

    var filenameMatches = await files.SearchByNameAsync(uploadedTree, "CHILD");
    Assert(filenameMatches.Count == 1 &&
           filenameMatches[0].RelativePath.EndsWith("child.yaml", StringComparison.OrdinalIgnoreCase),
        "bounded recursive filename search is case insensitive");
    Assert((await files.SearchByNameAsync(uploadedTree, ".", maximumResults: 1)).Count == 1,
        "remote filename search honors its result limit");
    await AssertThrowsAsync<ArgumentException>(
        () => files.SearchByNameAsync(uploadedTree, "\u0001"),
        "remote filename search rejects control characters");

    var moveRoot = Path.Combine(remote, "move-root");
    var moveSourceFolder = Path.Combine(moveRoot, "source");
    var moveDestinationFolder = Path.Combine(moveRoot, "destination");
    Directory.CreateDirectory(moveSourceFolder);
    Directory.CreateDirectory(moveDestinationFolder);
    var moveSourceFile = Path.Combine(moveSourceFolder, "move.txt");
    await File.WriteAllTextAsync(moveSourceFile, "move me");
    var movedFile = Path.Combine(moveDestinationFolder, "moved.txt");
    await files.MoveAsync(moveSourceFile, movedFile);
    Assert(!File.Exists(moveSourceFile) && await File.ReadAllTextAsync(movedFile) == "move me",
        "cross-directory file move preserves contents");
    var moveDirectory = Path.Combine(moveSourceFolder, "tree");
    Directory.CreateDirectory(moveDirectory);
    await File.WriteAllTextAsync(Path.Combine(moveDirectory, "nested.txt"), "nested");
    var movedDirectory = Path.Combine(moveDestinationFolder, "tree");
    await files.MoveAsync(moveDirectory, movedDirectory);
    Assert(File.Exists(Path.Combine(movedDirectory, "nested.txt")),
        "cross-directory folder move preserves descendants");
    await AssertThrowsAsync<IOException>(
        () => files.MoveAsync(movedDirectory, Path.Combine(movedDirectory, "inside")),
        "folder move rejects moving a directory inside itself");
    await AssertThrowsAsync<IOException>(
        () => files.MoveAsync(Path.GetPathRoot(scratch)!, Path.Combine(scratch, "moved-root")),
        "folder move rejects a filesystem root");

    var downloadedTree = Path.Combine(downloads, "downloaded-tree");
    var downloadTreeResult = await files.DownloadPathAsync(uploadedTree, downloadedTree);
    Assert(downloadTreeResult.FilesTransferred == 2, "recursive download transfers every file");
    Assert(await File.ReadAllTextAsync(Path.Combine(downloadedTree, "nested", "child.yaml")) ==
           "value: 42", "recursive download preserves contents");

    var resumeSource = Path.Combine(remote, "resume.bin");
    var resumeBytes = Enumerable.Range(0, 32_768).Select(index => (byte)(index % 251)).ToArray();
    await File.WriteAllBytesAsync(resumeSource, resumeBytes);
    var resumeDestination = Path.Combine(downloads, "resume.bin");
    await File.WriteAllBytesAsync(resumeDestination + ".sutty.part", resumeBytes[..8_192]);
    var resumed = await files.DownloadPathAsync(
        resumeSource,
        resumeDestination,
        new SftpTransferOptions { Resume = true, VerifyChecksum = true });
    Assert(resumed.ResumedBytes == 8_192, "resume continues from the partial-file offset");
    Assert(resumed.Sha256 is { Length: 64 }, "completed transfer returns a SHA-256 checksum");
    Assert((await File.ReadAllBytesAsync(resumeDestination)).SequenceEqual(resumeBytes),
        "checksum-verified resumed download matches the source");

    var policySource = Path.Combine(scratch, "policy-source.txt");
    await File.WriteAllTextAsync(policySource, "incoming");
    var skipDestination = Path.Combine(remote, "skip-policy.txt");
    await File.WriteAllTextAsync(skipDestination, "existing");
    var skippedUpload = await files.UploadPathAsync(
        policySource,
        skipDestination,
        new SftpTransferOptions { ConflictPolicy = SftpConflictPolicy.Skip, VerifyChecksum = false });
    Assert(skippedUpload.FilesTransferred == 0 && skippedUpload.FilesSkipped == 1 &&
           await File.ReadAllTextAsync(skipDestination) == "existing",
        "skip conflict policy preserves an existing upload destination");

    var renameDestination = Path.Combine(remote, "rename-policy.txt");
    await File.WriteAllTextAsync(renameDestination, "existing");
    var renamedUpload = await files.UploadPathAsync(
        policySource,
        renameDestination,
        new SftpTransferOptions { ConflictPolicy = SftpConflictPolicy.Rename, VerifyChecksum = false });
    Assert(renamedUpload.FilesTransferred == 1 &&
           await File.ReadAllTextAsync(Path.Combine(remote, "rename-policy (1).txt")) == "incoming",
        "rename conflict policy creates a non-destructive upload name");

    var newerDestination = Path.Combine(remote, "newer-policy.txt");
    await File.WriteAllTextAsync(newerDestination, "existing");
    File.SetLastWriteTimeUtc(policySource, DateTime.UtcNow.AddMinutes(-10));
    File.SetLastWriteTimeUtc(newerDestination, DateTime.UtcNow);
    var newerOnlySkipped = await files.UploadPathAsync(
        policySource,
        newerDestination,
        new SftpTransferOptions { ConflictPolicy = SftpConflictPolicy.NewerOnly, VerifyChecksum = false });
    Assert(newerOnlySkipped.FilesSkipped == 1 &&
           await File.ReadAllTextAsync(newerDestination) == "existing",
        "newer-only conflict policy preserves a newer destination");
    File.SetLastWriteTimeUtc(policySource, DateTime.UtcNow.AddMinutes(10));
    var newerOnlyReplaced = await files.UploadPathAsync(
        policySource,
        newerDestination,
        new SftpTransferOptions { ConflictPolicy = SftpConflictPolicy.NewerOnly, VerifyChecksum = false });
    Assert(newerOnlyReplaced.FilesTransferred == 1 &&
           await File.ReadAllTextAsync(newerDestination) == "incoming",
        "newer-only conflict policy replaces an older destination");

    await AssertThrowsAsync<SftpTransferConflictException>(
        () => files.UploadPathAsync(
            policySource,
            newerDestination,
            new SftpTransferOptions { ConflictPolicy = SftpConflictPolicy.Ask, VerifyChecksum = false }),
        "ask conflict policy never silently chooses an unattended action");

    var downloadPolicySource = Path.Combine(remote, "download-policy-source.txt");
    await File.WriteAllTextAsync(downloadPolicySource, "remote incoming");
    var downloadSkipDestination = Path.Combine(downloads, "download-skip-policy.txt");
    await File.WriteAllTextAsync(downloadSkipDestination, "existing");
    var skippedDownload = await files.DownloadPathAsync(
        downloadPolicySource,
        downloadSkipDestination,
        new SftpTransferOptions { ConflictPolicy = SftpConflictPolicy.Skip, VerifyChecksum = false });
    Assert(skippedDownload.FilesSkipped == 1 &&
           await File.ReadAllTextAsync(downloadSkipDestination) == "existing",
        "skip conflict policy preserves an existing download destination");

    var deleteRoot = Path.Combine(scratch, "delete-preview");
    Directory.CreateDirectory(Path.Combine(deleteRoot, "nested"));
    await File.WriteAllTextAsync(Path.Combine(deleteRoot, "nested", "payload.txt"), "remove me");
    var deletePreview = await files.PreviewDeleteAsync(deleteRoot);
    Assert(deletePreview.DirectoryCount == 2 && deletePreview.FileCount == 1 &&
           deletePreview.PreviewPaths.Contains(Path.Combine("nested", "payload.txt")),
        "recursive delete preview reports files, folders, and bounded paths");
    await files.DeletePathRecursiveAsync(deleteRoot);
    Assert(!Directory.Exists(deleteRoot), "safe recursive delete removes the selected tree");
    await AssertThrowsAsync<IOException>(
        () => files.PreviewDeleteAsync(Path.GetPathRoot(scratch)!),
        "recursive delete rejects a filesystem root");

    var checkpointPath = Path.Combine(scratch, "checkpoints.json");
    var checkpointStore = new SftpTransferCheckpointStore(checkpointPath);
    var checkpointId = SftpTransferCheckpointStore.CreateId(
        "server-a",
        sutty.Core.Sftp.SftpTransferDirection.Upload,
        source,
        "/srv/source.txt");
    checkpointStore.Save(new SftpTransferCheckpoint
    {
        Id = checkpointId,
        Scope = "server-a",
        Direction = sutty.Core.Sftp.SftpTransferDirection.Upload,
        SourcePath = source,
        DestinationPath = "/srv/source.txt",
        PartialPath = "/srv/source.txt.sutty.part",
        TotalBytes = 100,
        TransferredBytes = 40,
        SourceLastWriteUtcTicks = 123,
    });
    Assert(checkpointStore.Load(checkpointId)?.TransferredBytes == 40,
        "transfer checkpoint survives a store reload");
    Assert(!File.ReadAllText(checkpointPath).Contains("password", StringComparison.OrdinalIgnoreCase),
        "transfer checkpoint document contains no credential fields");
    checkpointStore.Delete(checkpointId);
    Assert(checkpointStore.Load(checkpointId) is null, "completed checkpoint is removed");

    var goodTarget = new RecordingSftpService();
    var retryTarget = new RecordingSftpService(failFirstUpload: true);
    var coordinator = new MultiSftpTransferCoordinator(maximumParallelism: 2);
    var batch = await coordinator.UploadAsync(
        source,
        [
            new MultiSftpTarget("good", "good-server", goodTarget, "/deploy"),
            new MultiSftpTarget("retry", "retry-server", retryTarget, "/deploy"),
        ],
        new SftpTransferOptions { RetryEnabled = false });
    Assert(batch.Failed.Count == 1 && batch.Failed[0].Target.Id == "retry",
        "multi-server upload isolates a failed server");
    Assert(goodTarget.UploadCalls == 1 && retryTarget.UploadCalls == 1,
        "initial multi-server upload invokes every selected server once");
    var retryBatch = await coordinator.RetryFailedAsync(
        source,
        batch,
        new SftpTransferOptions { RetryEnabled = false });
    Assert(retryBatch.IsSuccessful, "failed-server retry succeeds independently");
    Assert(goodTarget.UploadCalls == 1 && retryTarget.UploadCalls == 2,
        "retry invokes failed servers only");

    var fanInRemote = Path.Combine(remote, "fan-in.txt");
    await File.WriteAllTextAsync(fanInRemote, "aggregate");
    var downloadOne = new RecordingSftpService(fanInRemote);
    var downloadTwo = new RecordingSftpService(fanInRemote);
    var fanInDirectory = Path.Combine(downloads, "fan-in");
    var fanIn = await coordinator.DownloadAsync(
        "/var/export/fan-in.txt",
        fanInDirectory,
        [
            new MultiSftpTarget("alpha", "same-name", downloadOne, "/var/export/fan-in.txt"),
            new MultiSftpTarget("beta", "same-name", downloadTwo, "/var/export/fan-in.txt"),
        ]);
    Assert(fanIn.IsSuccessful && downloadOne.DownloadCalls == 1 && downloadTwo.DownloadCalls == 1,
        "multi-server N-to-one download invokes every source");
    Assert(Directory.GetFiles(fanInDirectory, "fan-in.txt", SearchOption.AllDirectories).Length == 2,
        "N-to-one download isolates equal names by server");

    var stableFanInDirectory = Path.Combine(downloads, "fan-in-stable");
    await coordinator.DownloadAsync(
        "/var/export/fan-in.txt",
        stableFanInDirectory,
        [new MultiSftpTarget("session-old", "stable-server", downloadOne, "/var/export")
            { PersistenceId = "saved-host-42" }]);
    await coordinator.DownloadAsync(
        "/var/export/fan-in.txt",
        stableFanInDirectory,
        [new MultiSftpTarget("session-new", "stable-server", downloadOne, "/var/export")
            { PersistenceId = "saved-host-42" }]);
    Assert(Directory.GetFiles(stableFanInDirectory, "fan-in.txt", SearchOption.AllDirectories).Length == 1,
        "N-to-one destination remains stable when a session is recreated");

    var queuePath = Path.Combine(scratch, "transfer-queue.json");
    var queue = new SftpTransferQueueStore(queuePath);
    var queuedJob = new SftpQueuedJob
    {
        Id = "restart-safe-job",
        Mode = SftpQueueMode.FanOut,
        Direction = sutty.Core.Sftp.SftpTransferDirection.Upload,
        SourcePath = source,
        DestinationPath = "/deploy/source.txt",
        Options = new SftpTransferOptions
        {
            ConflictPolicy = SftpConflictPolicy.Rename,
            Overwrite = false,
            Resume = true,
        },
        State = SftpQueueJobState.Running,
        Targets =
        [
            new SftpQueuedTarget
            {
                Id = "saved-alpha",
                DisplayName = "alpha",
                SourcePath = source,
                DestinationPath = "/deploy/source.txt",
                State = SftpQueueTargetState.Succeeded,
                BytesTransferred = 100,
                TotalBytes = 100,
            },
            new SftpQueuedTarget
            {
                Id = "saved-beta",
                DisplayName = "beta",
                SourcePath = source,
                DestinationPath = "/deploy/source.txt",
                State = SftpQueueTargetState.Running,
                TotalBytes = 100,
            },
        ],
    };
    queue.Upsert(queuedJob);
    Assert(queue.RecoverIncomplete().Single().State == SftpQueueJobState.Running,
        "reading the queue in the same app does not interrupt active work");
    Assert(new SftpTransferQueueStore(queuePath).RecoverIncomplete().Single().State ==
           SftpQueueJobState.Running,
        "multiple queue readers in the same app share the active runtime owner");
    var activeOwner = queue.Get(queuedJob.Id)!.RuntimeOwnerId;
    File.WriteAllText(
        queuePath,
        File.ReadAllText(queuePath).Replace(activeOwner, "previousruntime", StringComparison.Ordinal));
    var recovered = new SftpTransferQueueStore(queuePath).RecoverIncomplete().Single();
    Assert(recovered.State == SftpQueueJobState.Interrupted,
        "running transfer job becomes interrupted after restart");
    Assert(recovered.Targets.Single(item => item.Id == "saved-alpha").State ==
           SftpQueueTargetState.Succeeded,
        "restart recovery preserves successful servers");
    Assert(SftpTransferQueueStore.GetRetryTargetIds(recovered).SetEquals(["saved-beta"]),
        "restart recovery selects interrupted servers only");
    Assert(recovered.Options.ConflictPolicy == SftpConflictPolicy.Rename,
        "durable transfer queue preserves the per-job conflict policy");
    Assert(SftpTransferQueueStore.GetProgressPercentage(recovered) == 50,
        "durable transfer progress projects byte counters across targets");
    Assert(!File.ReadAllText(queuePath).Contains("password", StringComparison.OrdinalIgnoreCase),
        "durable transfer queue contains no credentials");

    queue.UpdateTarget(queuedJob.Id, "saved-beta", SftpQueueTargetState.Paused);
    var paused = queue.Get(queuedJob.Id)!;
    Assert(paused.State == SftpQueueJobState.Paused &&
           SftpTransferQueueStore.GetRetryTargetIds(paused).SetEquals(["saved-beta"]),
        "paused transfer remains durable and is explicitly resumable");

    Assert(queue.TryAcquireRetryTargetLease(
               queuedJob.Id, "saved-beta", "files-panel-one", out var firstLease) &&
           firstLease is not null,
        "first files panel atomically claims a queued target");
    Assert(!queue.TryAcquireRetryTargetLease(
               queuedJob.Id, "saved-beta", "files-panel-one", out var duplicateLease) &&
           duplicateLease is null,
        "one files panel cannot claim the same queued target twice");
    var secondQueueReader = new SftpTransferQueueStore(queuePath);
    Assert(!secondQueueReader.TryAcquireRetryTargetLease(
               queuedJob.Id, "saved-beta", "files-panel-two", out var competingLease) &&
           competingLease is null,
        "queue readers in the same process share target leases");
    Assert(!File.ReadAllText(queuePath).Contains("files-panel-one", StringComparison.Ordinal),
        "in-process target lease owner is never persisted");
    firstLease!.Dispose();
    Assert(secondQueueReader.TryAcquireRetryTargetLease(
               queuedJob.Id, "saved-beta", "files-panel-two", out var releasedLease) &&
           releasedLease is not null,
        "disposing a target lease makes the target claimable again");
    firstLease.Dispose();
    Assert(!queue.TryAcquireRetryTargetLease(
               queuedJob.Id, "saved-beta", "files-panel-three", out var staleReleaseLease) &&
           staleReleaseLease is null,
        "disposing a stale lease twice cannot release the current owner");
    releasedLease!.Dispose();

    var concurrentLeases = new ConcurrentBag<IDisposable>();
    Parallel.For(0, 32, contender =>
    {
        if (queue.TryAcquireRetryTargetLease(
                queuedJob.Id,
                "saved-beta",
                $"parallel-files-panel-{contender}",
                out var concurrentLease))
        {
            concurrentLeases.Add(concurrentLease!);
        }
    });
    Assert(concurrentLeases.Count == 1,
        "parallel files panels produce exactly one queued-target lease winner");
    foreach (var concurrentLease in concurrentLeases)
        concurrentLease.Dispose();

    queue.UpdateTarget(queuedJob.Id, "saved-beta", SftpQueueTargetState.Succeeded);
    Assert(SftpTransferQueueStore.GetProgressPercentage(queue.Get(queuedJob.Id)!) == 100,
        "a completed durable job is always presented as 100 percent");
    Assert(!queue.TryAcquireRetryTargetLease(
               queuedJob.Id, "saved-beta", "stale-files-panel", out var completedLease) &&
           completedLease is null,
        "a stale files panel cannot claim a target completed by another panel");

    queue.UpdateTarget(queuedJob.Id, "saved-beta", SftpQueueTargetState.Cancelled);
    Assert(SftpTransferQueueStore.GetRetryTargetIds(queue.Get(queuedJob.Id)!)
            .Contains("saved-beta"),
        "an explicitly cancelled target remains eligible for a user retry");
    Assert(queue.TryAcquireRetryTargetLease(
               queuedJob.Id, "saved-beta", "cancelled-target-retry", out var cancelledLease) &&
           cancelledLease is not null,
        "retry lease policy matches cancelled-target retry selection");
    cancelledLease!.Dispose();

    var cancelledOnlyJob = queuedJob with
    {
        Id = "cancelled-only-job",
        State = SftpQueueJobState.Cancelled,
        Targets =
        [
            queuedJob.Targets[1] with
            {
                Id = "cancelled-only-target",
                State = SftpQueueTargetState.Cancelled,
            },
        ],
    };
    queue.Upsert(cancelledOnlyJob);
    Assert(queue.TryAcquireRetryTargetLease(
               cancelledOnlyJob.Id,
               "cancelled-only-target",
               "cancelled-job-retry",
               out var cancelledJobLease) &&
           cancelledJobLease is not null,
        "an entirely cancelled durable job remains claimable for explicit retry");
    cancelledJobLease!.Dispose();

    var cancelledPath = Path.Combine(downloads, "cancelled.txt");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await AssertThrowsAsync<OperationCanceledException>(
        () => files.DownloadFileAsync(remoteFile, cancelledPath, ct: cancelled.Token),
        "cancelled transfer reports cancellation");
    Assert(!File.Exists(cancelledPath), "cancelled transfer leaves no destination or partial file");

    Console.WriteLine("SFTP path and safe local-transfer self-tests passed.");
}
finally
{
    Directory.Delete(scratch, recursive: true);
}

static void Assert(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException($"Self-test failed: {description}.");
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string description)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(
        $"Self-test failed: {description} did not throw {typeof(TException).Name}.");
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

sealed class RecordingSftpService(string? downloadSource = null, bool failFirstUpload = false) : ISftpService
{
    public int UploadCalls { get; private set; }
    public int DownloadCalls { get; private set; }

    public Task<SftpTransferResult> UploadPathAsync(
        string localPath,
        string remotePath,
        SftpTransferOptions? options = null,
        IProgress<SftpTransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        UploadCalls++;
        ct.ThrowIfCancellationRequested();
        if (failFirstUpload && UploadCalls == 1)
            throw new IOException("simulated target failure");
        var bytes = new FileInfo(localPath).Length;
        progress?.Report(new SftpTransferProgress(
            sutty.Core.Sftp.SftpTransferDirection.Upload,
            SftpTransferPhase.Completed,
            Path.GetFileName(localPath),
            bytes,
            bytes,
            1,
            1,
            1));
        return Task.FromResult(new SftpTransferResult(
            sutty.Core.Sftp.SftpTransferDirection.Upload,
            localPath,
            remotePath,
            1,
            bytes,
            0,
            null,
            TimeSpan.Zero));
    }

    public Task<IReadOnlyList<RemoteTreeEntry>> EnumerateTreeAsync(
        string path,
        CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<RemoteTreeEntry>> SearchByNameAsync(
        string path,
        string query,
        int maximumResults = 500,
        CancellationToken ct = default) => throw new NotSupportedException();

    public Task<SftpTransferResult> DownloadPathAsync(
        string remotePath,
        string localPath,
        SftpTransferOptions? options = null,
        IProgress<SftpTransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        DownloadCalls++;
        ct.ThrowIfCancellationRequested();
        var sourcePath = downloadSource ?? remotePath;
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.Copy(sourcePath, localPath, overwrite: true);
        var bytes = new FileInfo(localPath).Length;
        progress?.Report(new SftpTransferProgress(
            sutty.Core.Sftp.SftpTransferDirection.Download,
            SftpTransferPhase.Completed,
            Path.GetFileName(remotePath),
            bytes,
            bytes,
            1,
            1,
            1));
        return Task.FromResult(new SftpTransferResult(
            sutty.Core.Sftp.SftpTransferDirection.Download,
            remotePath,
            localPath,
            1,
            bytes,
            0,
            null,
            TimeSpan.Zero));
    }

    public Task<IReadOnlyList<sutty.Core.Models.RemoteFileEntry>> ListDirectoryAsync(
        string path,
        CancellationToken ct = default) => throw new NotSupportedException();

    public Task UploadFileAsync(string localPath, string remoteDirectory, bool overwrite = false,
        IProgress<double>? progress = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task DownloadFileAsync(string remotePath, string localPath, bool overwrite = false,
        IProgress<double>? progress = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task MoveAsync(string sourcePath, string destinationPath,
        CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteFileAsync(string path, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task DeleteDirectoryAsync(string path, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<SftpDeletePreview> PreviewDeleteAsync(string path, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task DeletePathRecursiveAsync(string path, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task ChangePermissionsAsync(string path, int unixMode, bool recursive = false,
        CancellationToken ct = default) => throw new NotSupportedException();
    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
