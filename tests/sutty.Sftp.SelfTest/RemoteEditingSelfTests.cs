using sutty.Core.Models;
using sutty.Core.Sftp;
using sutty.Core.Sessions;
using sutty.UI.Services;

internal static class RemoteEditingSelfTests
{
    public static async Task RunAsync()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "sutty-edit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var edit = new RemoteEditSession("test@server.example:22", "/srv/한글 설정.json", scratch);
            var local = edit.AllocateWorkingCopy();
            await File.WriteAllTextAsync(local, "first");
            var stamp = new RemoteEditStamp(5, DateTime.UtcNow);
            await edit.AcceptDownloadAsync(stamp, stamp, default);
            Check(!await edit.HasChangesAsync(), "fresh verified working copy is clean");
            Check(!edit.HasRemoteConflict(stamp), "unchanged comparable remote stamp accepted");
            Check(edit.HasRemoteConflict(new RemoteEditStamp(5, stamp.Modified!.Value.AddSeconds(1))), "same-size remote save detected");
            Check(edit.HasRemoteConflict(null), "missing remote file requires review");
            Check(edit.HasRemoteConflict(new RemoteEditStamp(5, null)), "unknown timestamp requires review");

            // Editors commonly replace files atomically rather than writing in place.
            var replacement = Path.Combine(scratch, "replacement");
            await File.WriteAllTextAsync(replacement, "second");
            File.Move(replacement, local, true);
            Check(await edit.HasChangesAsync(), "editor atomic replacement detected");
            var snapshot = await edit.CreateUploadAsync();
            await File.WriteAllTextAsync(local, "third");
            Check(await File.ReadAllTextAsync(snapshot.LocalPath) == "second", "later editor saves cannot mutate an in-flight upload snapshot");
            await edit.AcceptUploadAsync(snapshot, "/srv/한글 설정.json", new RemoteEditStamp(6, DateTime.UtcNow), default);
            Check(await edit.HasChangesAsync(), "save during upload remains pending after previous snapshot succeeds");
            Check(File.Exists(local) && File.Exists(Path.Combine(edit.WorkingDirectory, "RECOVER.txt")), "working copy and recovery location remain available");
            using (var noteLock = new FileStream(Path.Combine(edit.WorkingDirectory, "RECOVER.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await edit.AcceptUploadAsync(snapshot, "/srv/new-name.json", new RemoteEditStamp(6, DateTime.UtcNow), default);
                Check(edit.UploadedHash == snapshot.Sha256 && edit.RemoteFilePath == "/srv/new-name.json" && edit.RecoveryNoteError is not null,
                    "recovery-note write failure does not misreport an already-promoted upload");
            }
            edit.RequireReview();
            Check(edit.HasRemoteConflict(edit.Baseline), "failure requires explicit review even when metadata matches");

            var raced = new RemoteEditSession("test@server.example:22", "/srv/file.txt", scratch);
            await File.WriteAllTextAsync(raced.AllocateWorkingCopy(), "first");
            await raced.AcceptDownloadAsync(stamp, stamp with { Size = 6 }, default);
            Check(raced.NeedsReview, "remote mutation during download requires review");
            var uncertain = new RemoteEditSession("test@server.example:22", "/srv/unknown.txt", scratch);
            await File.WriteAllTextAsync(uncertain.AllocateWorkingCopy(), "first");
            await uncertain.AcceptDownloadAsync(new(5, null), new(5, null), default);
            Check(uncertain.NeedsReview, "missing timestamps never imply an unchanged remote file");

            var binary = Path.Combine(scratch, "binary");
            await File.WriteAllBytesAsync(binary, [1, 0, 2, 3]);
            await ThrowsAsync<IOException>(() => RemoteEditSession.ReadStableTextAsync(binary, default), "binary content rejected");
            await File.WriteAllBytesAsync(binary, [0xff, 0xfe, 65, 0]);
            Check((await RemoteEditSession.ReadStableTextAsync(binary, default)).Length == 4, "BOM-marked UTF16 text supported");
            using (var large = File.Create(binary)) large.SetLength(RemoteEditSession.MaximumBytes + 1);
            await ThrowsAsync<IOException>(() => RemoteEditSession.ReadStableTextAsync(binary, default), "oversized edits bounded");
            await ThrowsAsync<OperationCanceledException>(() => edit.CreateUploadAsync(new CancellationToken(true)), "snapshot respects cancellation");
            Throws<IOException>(() => RemoteEditSession.ValidateEntry(new RemoteFileEntry { IsSymbolicLink = true }), "symlinks cannot redirect edits");
            Throws<ArgumentException>(() => new RemoteEditSession("test", "relative/path", scratch), "relative remote path rejected");
            Throws<ArgumentException>(() => new RemoteEditSession("test", "/srv/a\ncommand", scratch), "control characters rejected");

            Check(ExternalEditorCommand.QuoteArgument("C:\\a b\\file.txt") == "\"C:\\a b\\file.txt\"", "editor argument spaces quoted");
            Check(ExternalEditorCommand.QuoteArgument("x\"y") == "\"x\\\"y\"", "embedded quote escaped");
            Check(ExternalEditorCommand.QuoteArgument("C:\\folder\\") == "\"C:\\folder\\\\\"", "trailing slash escaped before final quote");
            Throws<ArgumentException>(() => ExternalEditorCommand.Create("cmd.exe", "/c {file}", local), "relative editor commands rejected");
            Throws<ArgumentException>(() => ExternalEditorCommand.Create(Path.Combine(scratch, "editor.cmd"), "{file}", local), "shell scripts are not editor executables");

            var queue = new SftpTransferQueueStore(Path.Combine(scratch, "queue.json"));
            var job = new SftpQueuedJob
            {
                RequiresEditReview = true,
                Direction = SftpTransferDirection.Upload,
                SourcePath = snapshot.LocalPath,
                DestinationPath = "/srv/file.txt",
                State = SftpQueueJobState.Failed,
                Targets = [new() { Id = "test-target", DisplayName = "test", SourcePath = snapshot.LocalPath,
                    DestinationPath = "/srv/file.txt", State = SftpQueueTargetState.Failed }],
            };
            queue.Upsert(job);
            var restored = new SftpTransferQueueStore(queue.StoragePath).Get(job.Id)!;
            Check(restored.RequiresEditReview, "edit retry-review boundary survives restart");
            Check(SftpTransferQueueStore.GetRetryTargetIds(restored).Count == 0, "generic retry never selects editor jobs");
            Check(!queue.TryAcquireRetryTargetLease(job.Id, "test-target", "owner", out var blocked) && blocked is null,
                "authoritative retry lease blocks bypassing remote edit review");
            using var service = new TransferCenterService(queue, watchFileSystem: false);
            Check(!service.CanExecute(restored, TransferCenterAction.RetryFailed), "global transfer center disables unsafe edit retries");
            var result = await service.ExecuteAsync(restored.Id, TransferCenterAction.RetryFailed);
            Check(result.Status == TransferCenterControlStatus.NoEligibleTargets, "programmatic global retry also enforces edit review");
            var interruptedEdit = restored with
            {
                Id = Guid.NewGuid().ToString("N"), State = SftpQueueJobState.Interrupted,
                Targets = [restored.Targets[0] with { State = SftpQueueTargetState.Interrupted }],
            };
            queue.Upsert(interruptedEdit);
            Check(queue.TryAcquireTargetLease(interruptedEdit.Id, "test-target", "cancel-owner", out var cancelLease),
                "editor recovery cancellation can claim an exclusive non-executing lease");
            using (cancelLease)
            {
                Check(!queue.TryAcquireTargetLease(interruptedEdit.Id, "test-target", "other-owner", out _),
                    "cancellation claim excludes another worker");
                queue.UpdateTarget(interruptedEdit.Id, "test-target", SftpQueueTargetState.Cancelled, 0, 0);
            }
            Check(queue.Get(interruptedEdit.Id)!.State == SftpQueueJobState.Cancelled && File.Exists(snapshot.LocalPath),
                "cancelled editor recovery keeps the local upload snapshot and never retries it");
            var ordinary = restored with { Id = Guid.NewGuid().ToString("N"), RequiresEditReview = false };
            queue.Upsert(ordinary);
            Check(SftpTransferQueueStore.GetRetryTargetIds(ordinary).Count == 1, "ordinary transfer retries remain available");
            var manager = new SessionManager();
            await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(async () =>
            {
                var session = manager.Create(new SshConnectionInfo { Host = "unused.example", Username = "test" });
                foreach (var entry in manager.Sessions) _ = entry.Id;
                await manager.CloseAsync(session); // Never connected; this opens no network connection.
            })));
            Check(manager.Sessions.Count == 0, "parallel window-close removals and session snapshots remain consistent");
            Console.WriteLine("Remote editing snapshots, conflict detection, recovery and retry-boundary self-tests passed.");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Remote edit test failed: " + message);
    }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + message);
    }
    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + message);
    }
}
