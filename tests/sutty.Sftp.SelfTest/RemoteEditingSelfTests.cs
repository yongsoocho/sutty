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

            var remote = new EditReadService(edit.RemoteFilePath, "first"u8.ToArray(), stamp.Modified);
            var originalVersion = await edit.ReadRemoteVersionAsync(remote, default);
            Check(!edit.HasRemoteContentConflict(originalVersion), "unchanged remote content matches the downloaded baseline");
            remote.Content = "other"u8.ToArray();
            var changedVersion = await edit.ReadRemoteVersionAsync(remote, default);
            Check(changedVersion!.Stamp == stamp && edit.HasRemoteContentConflict(changedVersion),
                "same-size same-timestamp remote content change requires explicit review");
            remote.Content = "three"u8.ToArray();
            Check(!changedVersion.Matches(await edit.ReadRemoteVersionAsync(remote, default)),
                "content change after confirmation is detected by the upload preflight");
            Check(remote.LastMaximumBytes == RemoteEditSession.MaximumBytes, "remote content reads carry the 8 MiB bound");

            var originalHash = edit.UploadedHash;
            remote.ReadError = new IOException("simulated remote read failure");
            await ThrowsAsync<IOException>(() => edit.ReadRemoteVersionAsync(remote, default), "remote read failure blocks verification");
            Check(edit.NeedsReview && edit.UploadedHash == originalHash && edit.Baseline == stamp && await File.ReadAllTextAsync(local) == "first",
                "failed remote verification requires review and preserves the local copy and baseline");
            remote.ReadError = null;
            remote.Content = "first"u8.ToArray();
            remote.AfterRead = () => remote.Modified = stamp.Modified!.Value.AddSeconds(1);
            await ThrowsAsync<IOException>(() => edit.ReadRemoteVersionAsync(remote, default), "remote mutation during verification fails closed");
            remote.AfterRead = null;
            remote.Modified = stamp.Modified;
            await edit.AcceptDownloadAsync(stamp, stamp, default);
            await ThrowsAsync<OperationCanceledException>(() => edit.ReadRemoteVersionAsync(remote, new CancellationToken(true)),
                "remote verification respects cancellation");
            Check(edit.NeedsReview, "cancelled verification cannot silently resume automatic upload");
            await edit.AcceptDownloadAsync(stamp, stamp, default);

            // Editors commonly replace files atomically rather than writing in place.
            var replacement = Path.Combine(scratch, "replacement");
            await File.WriteAllTextAsync(replacement, "second");
            File.Move(replacement, local, true);
            Check(await edit.HasChangesAsync(), "editor atomic replacement detected");
            var snapshot = await edit.CreateUploadAsync();
            await File.WriteAllTextAsync(local, "third");
            Check(await File.ReadAllTextAsync(snapshot.LocalPath) == "second", "later editor saves cannot mutate an in-flight upload snapshot");
            var uploadedStamp = new RemoteEditStamp(6, DateTime.UtcNow);
            await edit.AcceptUploadAsync(snapshot, "/srv/한글 설정.json", uploadedStamp, default);
            Check(await edit.HasChangesAsync(), "save during upload remains pending after previous snapshot succeeds");
            remote.Content = "second"u8.ToArray();
            remote.Modified = uploadedStamp.Modified;
            Check(!edit.HasRemoteContentConflict(await edit.ReadRemoteVersionAsync(remote, default)),
                "successful upload advances the content baseline to the immutable snapshot, not the newer editor save");
            Check(File.Exists(local) && File.Exists(Path.Combine(edit.WorkingDirectory, "RECOVER.txt")), "working copy and recovery location remain available");
            var recovery = await File.ReadAllTextAsync(Path.Combine(edit.WorkingDirectory, "RECOVER.txt"));
            Check(recovery.Contains("sensitive server settings") && recovery.Contains("original server") && recovery.Contains("upload-*.txt"),
                "recovery notes explain sensitive copies, the original server and retained upload snapshots");
            var candidate = edit.CreateReloadCandidate();
            await File.WriteAllBytesAsync(candidate.AllocateWorkingCopy(), [1, 0, 2]);
            await ThrowsAsync<IOException>(() => candidate.AcceptDownloadAsync(new(3, DateTime.UtcNow), null, default),
                "invalid replacement download is rejected");
            Check(edit.LocalFilePath == local && edit.UploadedHash == snapshot.Sha256 && edit.Baseline == uploadedStamp &&
                await File.ReadAllTextAsync(local) == "third" && await File.ReadAllTextAsync(snapshot.LocalPath) == "second",
                "failed reload preserves the original editor path, baseline, dirty contents and upload snapshot");
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
            var localReader = new LocalFileService();
            await ThrowsAsync<IOException>(() => localReader.ReadFileBytesAsync(binary, (int)RemoteEditSession.MaximumBytes),
                "bounded SFTP-compatible reader rejects files above 8 MiB");
            await File.WriteAllBytesAsync(binary, [1, 2, 3, 4]);
            Check((await localReader.ReadFileBytesAsync(binary, 4)).SequenceEqual(new byte[] { 1, 2, 3, 4 }),
                "bounded reader accepts content exactly at its limit");
            await ThrowsAsync<IOException>(() => localReader.ReadFileBytesAsync(binary, 3), "bounded reader rejects one byte over its limit");
            await ThrowsAsync<OperationCanceledException>(() => localReader.ReadFileBytesAsync(binary, 4, new CancellationToken(true)),
                "bounded reader respects cancellation");
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

    private sealed class EditReadService(string path, byte[] content, DateTime? modified) : ISftpService
    {
        public byte[] Content { get; set; } = content;
        public DateTime? Modified { get; set; } = modified;
        public Exception? ReadError { get; set; }
        public Action? AfterRead { get; set; }
        public int LastMaximumBytes { get; private set; }
        public Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string directory, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<RemoteFileEntry>>([new()
            {
                Name = RemotePath.GetName(path), FullPath = path, Size = Content.Length, Modified = Modified, IsRegularFile = true,
            }]);
        }
        public Task<byte[]> ReadFileBytesAsync(string remotePath, int maximumBytes, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            LastMaximumBytes = maximumBytes;
            if (ReadError is not null) throw ReadError;
            var bytes = Content.ToArray();
            AfterRead?.Invoke();
            return Task.FromResult(bytes);
        }
        public Task<IReadOnlyList<RemoteTreeEntry>> EnumerateTreeAsync(string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemoteTreeEntry>> SearchByNameAsync(string p, string q, int maximumResults = 500, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SftpTransferResult> UploadPathAsync(string l, string r, SftpTransferOptions? options = null, IProgress<SftpTransferProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SftpTransferResult> DownloadPathAsync(string r, string l, SftpTransferOptions? options = null, IProgress<SftpTransferProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UploadFileAsync(string l, string r, bool overwrite = false, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DownloadFileAsync(string r, string l, bool overwrite = false, IProgress<double>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MoveAsync(string s, string d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteFileAsync(string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteDirectoryAsync(string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SftpDeletePreview> PreviewDeleteAsync(string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeletePathRecursiveAsync(string p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ChangePermissionsAsync(string p, int mode, bool recursive = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(string p, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
