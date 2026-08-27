using Renci.SshNet;
using Renci.SshNet.Common;
using sutty.Core.Models;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace sutty.Core.Sftp;

public sealed partial class SshNetSftpService
{
    private const int TransferBufferSize = 128 * 1024;
    private const int MaxTreeEntries = 100_000;
    private const int MaxTreeDepth = 256;
    private const long CheckpointByteInterval = 1024 * 1024;
    private static readonly TimeSpan CheckpointTimeInterval = TimeSpan.FromSeconds(1);

    public Task<IReadOnlyList<RemoteTreeEntry>> EnumerateTreeAsync(
        string path,
        CancellationToken ct = default) => SerializedAsync<IReadOnlyList<RemoteTreeEntry>>(
        () => EnumerateRemoteTreeCore(Client, path, ct),
        ct);

    public Task<IReadOnlyList<RemoteTreeEntry>> SearchByNameAsync(
        string path,
        string query,
        int maximumResults = 500,
        CancellationToken ct = default) => SerializedAsync<IReadOnlyList<RemoteTreeEntry>>(() =>
    {
        var normalized = SftpSearchRules.Normalize(query, maximumResults);
        return EnumerateRemoteTreeCore(Client, path, ct)
            .Where(entry => entry.Entry.Name.Contains(
                normalized.Query,
                StringComparison.OrdinalIgnoreCase))
            .Take(normalized.MaximumResults)
            .ToList();
    }, ct);

    public Task<SftpDeletePreview> PreviewDeleteAsync(
        string path,
        CancellationToken ct = default) => SerializedAsync(
        () => CreateDeletePreview(Client, path, ct),
        ct);

    public Task DeletePathRecursiveAsync(string path, CancellationToken ct = default)
        => SerializedAsync(() => DeletePathRecursively(Client, path, ct), ct);

    public Task ChangePermissionsAsync(
        string path,
        int unixMode,
        bool recursive = false,
        CancellationToken ct = default) => SerializedAsync(() =>
    {
        if (unixMode is < 0 or > 0x0FFF)
            throw new ArgumentOutOfRangeException(nameof(unixMode), "Unix permissions must be between 0000 and 7777.");

        var root = RemotePath.Normalize(path);
        if (!Client.Exists(root))
            throw new FileNotFoundException($"Remote path does not exist: {root}", root);

        var entries = recursive && IsDirectoryNotLink(Client, root)
            ? EnumerateRemoteTreeCore(Client, root, ct)
            : [];
        ApplyPermissions(Client, root, unixMode, ct);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.Entry.IsSymbolicLink)
                ApplyPermissions(Client, entry.Entry.FullPath, unixMode, ct);
        }
    }, ct);

    public async Task<SftpTransferResult> UploadPathAsync(
        string localPath,
        string remotePath,
        SftpTransferOptions? options = null,
        IProgress<SftpTransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        var normalizedOptions = (options ?? SftpTransferOptions.Default).Normalize();
        var destination = RemotePath.Normalize(remotePath);
        var started = Stopwatch.GetTimestamp();

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(localPath))
            {
                var info = new FileInfo(localPath);
                var file = await UploadFileWithRetryAsync(
                    info.FullName,
                    destination,
                    info.Name,
                    normalizedOptions,
                    progress,
                    previousBytes: 0,
                    totalBytes: info.Length,
                    filesCompleted: 0,
                    totalFiles: 1,
                    ct).ConfigureAwait(false);
                ReportCompleted(
                    progress,
                    SftpTransferDirection.Upload,
                    info.Name,
                    info.Length,
                    1,
                    1);
                return new SftpTransferResult(
                    SftpTransferDirection.Upload,
                    info.FullName,
                    destination,
                    file.Skipped ? 0 : 1,
                    file.Bytes,
                    file.ResumedBytes,
                    file.Sha256,
                    Stopwatch.GetElapsedTime(started),
                    file.Skipped ? 1 : 0);
            }

            if (!Directory.Exists(localPath))
                throw new FileNotFoundException($"Local path does not exist: {localPath}", localPath);

            var root = new DirectoryInfo(localPath);
            progress?.Report(new SftpTransferProgress(
                SftpTransferDirection.Upload,
                SftpTransferPhase.Enumerating,
                root.Name,
                0,
                0,
                0,
                0,
                1));
            var tree = EnumerateLocalTree(root, ct);
            var totalBytes = tree.Files.Sum(file => file.Info.Length);
            var transferredBytes = 0L;
            var copiedBytes = 0L;
            var resumedBytes = 0L;
            var filesCompleted = 0;
            var filesSkipped = 0;

            await RunWithRetryAsync(
                SftpTransferDirection.Upload,
                root.Name,
                normalizedOptions,
                progress,
                transferredBytes,
                totalBytes,
                filesCompleted,
                tree.Files.Count,
                operation: () => EnsureRemoteDirectory(Client, destination),
                ct).ConfigureAwait(false);

            foreach (var directory in tree.Directories)
            {
                ct.ThrowIfCancellationRequested();
                var remoteDirectory = CombineRelative(destination, directory.RelativePath);
                await RunWithRetryAsync(
                    SftpTransferDirection.Upload,
                    directory.RelativePath,
                    normalizedOptions,
                    progress,
                    transferredBytes,
                    totalBytes,
                    filesCompleted,
                    tree.Files.Count,
                    operation: () => EnsureRemoteDirectory(Client, remoteDirectory),
                    ct).ConfigureAwait(false);
            }

            string? singleChecksum = null;
            foreach (var localFile in tree.Files)
            {
                ct.ThrowIfCancellationRequested();
                var remoteFile = CombineRelative(destination, localFile.RelativePath);
                var result = await UploadFileWithRetryAsync(
                    localFile.Info.FullName,
                    remoteFile,
                    localFile.RelativePath,
                    normalizedOptions,
                    progress,
                    transferredBytes,
                    totalBytes,
                    filesCompleted,
                    tree.Files.Count,
                    ct).ConfigureAwait(false);
                transferredBytes += localFile.Info.Length;
                copiedBytes += result.Bytes;
                resumedBytes += result.ResumedBytes;
                filesCompleted++;
                filesSkipped += result.Skipped ? 1 : 0;
                singleChecksum = tree.Files.Count == 1 ? result.Sha256 : null;
            }

            ReportCompleted(
                progress,
                SftpTransferDirection.Upload,
                root.Name,
                totalBytes,
                filesCompleted,
                tree.Files.Count);
            return new SftpTransferResult(
                SftpTransferDirection.Upload,
                root.FullName,
                destination,
                filesCompleted - filesSkipped,
                copiedBytes,
                resumedBytes,
                singleChecksum,
                Stopwatch.GetElapsedTime(started),
                filesSkipped);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<SftpTransferResult> DownloadPathAsync(
        string remotePath,
        string localPath,
        SftpTransferOptions? options = null,
        IProgress<SftpTransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        var normalizedOptions = (options ?? SftpTransferOptions.Default).Normalize();
        var source = RemotePath.Normalize(remotePath);
        var destination = Path.GetFullPath(localPath);
        var started = Stopwatch.GetTimestamp();

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var attributes = await RunWithRetryAsync(
                SftpTransferDirection.Download,
                RemotePath.GetName(source),
                normalizedOptions,
                progress,
                0,
                0,
                0,
                1,
                () => Client.GetAttributes(source),
                ct).ConfigureAwait(false);

            if (!attributes.IsDirectory)
            {
                var file = await DownloadFileWithRetryAsync(
                    source,
                    destination,
                    RemotePath.GetName(source),
                    normalizedOptions,
                    progress,
                    previousBytes: 0,
                    totalBytes: attributes.Size,
                    filesCompleted: 0,
                    totalFiles: 1,
                    ct).ConfigureAwait(false);
                ReportCompleted(
                    progress,
                    SftpTransferDirection.Download,
                    RemotePath.GetName(source),
                    attributes.Size,
                    1,
                    1);
                return new SftpTransferResult(
                    SftpTransferDirection.Download,
                    source,
                    destination,
                    file.Skipped ? 0 : 1,
                    file.Bytes,
                    file.ResumedBytes,
                    file.Sha256,
                    Stopwatch.GetElapsedTime(started),
                    file.Skipped ? 1 : 0);
            }

            progress?.Report(new SftpTransferProgress(
                SftpTransferDirection.Download,
                SftpTransferPhase.Enumerating,
                RemotePath.GetName(source),
                0,
                0,
                0,
                0,
                1));
            var tree = await Task.Run(
                () => EnumerateRemoteTreeCore(Client, source, ct),
                ct).ConfigureAwait(false);
            var files = tree.Where(item =>
                    !item.Entry.IsDirectory &&
                    !item.Entry.IsSymbolicLink &&
                    item.Entry.IsRegularFile)
                .ToList();
            var directories = tree.Where(item => item.Entry.IsDirectory && !item.Entry.IsSymbolicLink)
                .ToList();
            var totalBytes = files.Sum(item => item.Entry.Size);
            var transferredBytes = 0L;
            var copiedBytes = 0L;
            var resumedBytes = 0L;
            var filesCompleted = 0;
            var filesSkipped = 0;

            Directory.CreateDirectory(destination);
            foreach (var directory in directories)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(CombineLocalRelative(destination, directory.RelativePath));
            }

            string? singleChecksum = null;
            foreach (var remoteFile in files)
            {
                ct.ThrowIfCancellationRequested();
                var localFile = CombineLocalRelative(destination, remoteFile.RelativePath);
                var result = await DownloadFileWithRetryAsync(
                    remoteFile.Entry.FullPath,
                    localFile,
                    remoteFile.RelativePath,
                    normalizedOptions,
                    progress,
                    transferredBytes,
                    totalBytes,
                    filesCompleted,
                    files.Count,
                    ct).ConfigureAwait(false);
                transferredBytes += remoteFile.Entry.Size;
                copiedBytes += result.Bytes;
                resumedBytes += result.ResumedBytes;
                filesCompleted++;
                filesSkipped += result.Skipped ? 1 : 0;
                singleChecksum = files.Count == 1 ? result.Sha256 : null;
            }

            ReportCompleted(
                progress,
                SftpTransferDirection.Download,
                RemotePath.GetName(source),
                totalBytes,
                filesCompleted,
                files.Count);
            return new SftpTransferResult(
                SftpTransferDirection.Download,
                source,
                destination,
                filesCompleted - filesSkipped,
                copiedBytes,
                resumedBytes,
                singleChecksum,
                Stopwatch.GetElapsedTime(started),
                filesSkipped);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<FileTransferResult> UploadFileWithRetryAsync(
        string localPath,
        string remotePath,
        string relativePath,
        SftpTransferOptions options,
        IProgress<SftpTransferProgress>? progress,
        long previousBytes,
        long totalBytes,
        int filesCompleted,
        int totalFiles,
        CancellationToken ct) => await RunWithRetryAsync(
        SftpTransferDirection.Upload,
        relativePath,
        options,
        progress,
        previousBytes,
        totalBytes,
        filesCompleted,
        totalFiles,
        (attempt, reportFileBytes) => UploadFileCore(
            Client,
            localPath,
            remotePath,
            options,
            attempt,
            reportFileBytes,
            ct),
        ct).ConfigureAwait(false);

    private async Task<FileTransferResult> DownloadFileWithRetryAsync(
        string remotePath,
        string localPath,
        string relativePath,
        SftpTransferOptions options,
        IProgress<SftpTransferProgress>? progress,
        long previousBytes,
        long totalBytes,
        int filesCompleted,
        int totalFiles,
        CancellationToken ct) => await RunWithRetryAsync(
        SftpTransferDirection.Download,
        relativePath,
        options,
        progress,
        previousBytes,
        totalBytes,
        filesCompleted,
        totalFiles,
        (attempt, reportFileBytes) => DownloadFileCore(
            Client,
            remotePath,
            localPath,
            options,
            attempt,
            reportFileBytes,
            ct),
        ct).ConfigureAwait(false);

    private FileTransferResult UploadFileCore(
        SftpClient client,
        string localPath,
        string remotePath,
        SftpTransferOptions options,
        int attempt,
        Action<long> reportBytes,
        CancellationToken ct)
    {
        var source = new FileInfo(localPath);
        if (!source.Exists)
            throw new FileNotFoundException($"Local file does not exist: {localPath}", localPath);
        EnsureRemoteDirectory(client, RemotePath.GetDirectory(remotePath));

        var id = SftpTransferCheckpointStore.CreateId(
            _checkpointScope,
            SftpTransferDirection.Upload,
            source.FullName,
            remotePath);
        var partialPath = remotePath + $".sutty-{id[..12]}.part";
        var checkpoint = _checkpointStore.Load(id);
        var sourceTicks = source.LastWriteTimeUtc.Ticks;
        var offset = 0L;

        var replaceExistingDestination = false;
        if (client.Exists(remotePath))
        {
            var destinationAttributes = client.GetAttributes(remotePath);
            if (destinationAttributes.IsDirectory)
                throw new IOException($"A remote directory already exists: {remotePath}");

            if (options.VerifyChecksum && destinationAttributes.Size == source.Length)
            {
                var localHash = ComputeLocalSha256(source.FullName, ct);
                var remoteHash = ComputeRemoteSha256(client, remotePath, ct);
                if (CryptographicOperations.FixedTimeEquals(localHash, remoteHash))
                {
                    DeleteRemoteIfExists(client, partialPath);
                    _checkpointStore.Delete(id);
                    reportBytes(source.Length);
                    return new FileTransferResult(source.Length, source.Length, Convert.ToHexString(localHash));
                }
            }

            switch (options.EffectiveConflictPolicy)
            {
                case SftpConflictPolicy.Overwrite:
                    replaceExistingDestination = true;
                    break;
                case SftpConflictPolicy.Skip:
                    reportBytes(source.Length);
                    return new FileTransferResult(0, 0, null, Skipped: true);
                case SftpConflictPolicy.Rename:
                    // The old deterministic partial belongs to the colliding destination,
                    // not to the newly generated name. Never resume it under a new file.
                    DeleteRemoteIfExists(client, partialPath);
                    _checkpointStore.Delete(id);
                    remotePath = CreateAvailableRemotePath(client, remotePath);
                    id = SftpTransferCheckpointStore.CreateId(
                        _checkpointScope,
                        SftpTransferDirection.Upload,
                        source.FullName,
                        remotePath);
                    partialPath = remotePath + $".sutty-{id[..12]}.part";
                    checkpoint = _checkpointStore.Load(id);
                    break;
                case SftpConflictPolicy.NewerOnly:
                    if (source.LastWriteTimeUtc <= destinationAttributes.LastWriteTimeUtc)
                    {
                        reportBytes(source.Length);
                        return new FileTransferResult(0, 0, null, Skipped: true);
                    }
                    replaceExistingDestination = true;
                    break;
                default:
                    throw new SftpTransferConflictException(source.FullName, remotePath);
            }
        }

        if (options.Resume && checkpoint is not null &&
            checkpoint.TotalBytes == source.Length &&
            checkpoint.SourceLastWriteUtcTicks == sourceTicks &&
            checkpoint.PartialPath == partialPath &&
            client.Exists(partialPath))
        {
            var partialLength = client.GetAttributes(partialPath).Size;
            if (partialLength >= 0 && partialLength <= source.Length)
                offset = partialLength;
        }

        if (!options.Resume || offset == 0)
        {
            DeleteRemoteIfExists(client, partialPath);
            _checkpointStore.Delete(id);
            offset = 0;
        }

        reportBytes(offset);
        SaveCheckpoint(id, SftpTransferDirection.Upload, source.FullName, remotePath,
            partialPath, source.Length, offset, sourceTicks);
        try
        {
            using var local = source.OpenRead();
            local.Position = offset;
            using var remote = client.Open(
                partialPath,
                offset > 0 ? FileMode.Open : FileMode.CreateNew,
                FileAccess.Write);
            if (offset > 0)
                remote.Seek(offset, SeekOrigin.Begin);

            CopyWithCheckpoint(
                local,
                remote,
                offset,
                source.Length,
                transferred =>
                {
                    reportBytes(transferred);
                    SaveCheckpoint(id, SftpTransferDirection.Upload, source.FullName, remotePath,
                        partialPath, source.Length, transferred, sourceTicks);
                },
                ct);
            remote.Flush();

            string? checksum = null;
            if (options.VerifyChecksum)
            {
                var localHash = ComputeLocalSha256(source.FullName, ct);
                var remoteHash = ComputeRemoteSha256(client, partialPath, ct);
                if (!CryptographicOperations.FixedTimeEquals(localHash, remoteHash))
                    throw new SftpChecksumMismatchException(source.FullName, remotePath);
                checksum = Convert.ToHexString(localHash);
            }

            PromoteRemoteFile(client, partialPath, remotePath, replaceExistingDestination);
            _checkpointStore.Delete(id);
            reportBytes(source.Length);
            return new FileTransferResult(source.Length, offset, checksum);
        }
        catch (SftpChecksumMismatchException)
        {
            // A complete but corrupt partial cannot be repaired by resuming at EOF.
            // Remove only Sutty's deterministic partial/checkpoint so the retry starts fresh.
            DeleteRemoteIfExists(client, partialPath);
            _checkpointStore.Delete(id);
            throw;
        }
        catch
        {
            if (!options.Resume)
            {
                DeleteRemoteIfExists(client, partialPath);
                _checkpointStore.Delete(id);
            }
            throw;
        }
    }

    private FileTransferResult DownloadFileCore(
        SftpClient client,
        string remotePath,
        string localPath,
        SftpTransferOptions options,
        int attempt,
        Action<long> reportBytes,
        CancellationToken ct)
    {
        var attributes = client.GetAttributes(remotePath);
        if (attributes.IsDirectory)
            throw new IOException($"Remote path is a directory: {remotePath}");
        if (attributes.IsSymbolicLink)
            throw new IOException($"Symbolic-link downloads are not followed: {remotePath}");

        var destination = Path.GetFullPath(localPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var replaceExistingDestination = false;
        if (File.Exists(destination) && options.VerifyChecksum)
        {
            var existing = new FileInfo(destination);
            if (existing.Length == attributes.Size)
            {
                var remoteHash = ComputeRemoteSha256(client, remotePath, ct);
                var localHash = ComputeLocalSha256(destination, ct);
                if (CryptographicOperations.FixedTimeEquals(localHash, remoteHash))
                {
                    reportBytes(attributes.Size);
                    return new FileTransferResult(attributes.Size, attributes.Size,
                        Convert.ToHexString(localHash));
                }
            }
        }

        if (File.Exists(destination))
        {
            switch (options.EffectiveConflictPolicy)
            {
                case SftpConflictPolicy.Overwrite:
                    replaceExistingDestination = true;
                    break;
                case SftpConflictPolicy.Skip:
                    reportBytes(attributes.Size);
                    return new FileTransferResult(0, 0, null, Skipped: true);
                case SftpConflictPolicy.Rename:
                    destination = CreateAvailableLocalPath(destination);
                    break;
                case SftpConflictPolicy.NewerOnly:
                    if (new FileInfo(destination).LastWriteTimeUtc >= attributes.LastWriteTimeUtc)
                    {
                        reportBytes(attributes.Size);
                        return new FileTransferResult(0, 0, null, Skipped: true);
                    }
                    replaceExistingDestination = true;
                    break;
                default:
                    throw new SftpTransferConflictException(remotePath, destination);
            }
        }

        var id = SftpTransferCheckpointStore.CreateId(
            _checkpointScope,
            SftpTransferDirection.Download,
            remotePath,
            destination);
        var partialPath = destination + $".sutty-{id[..12]}.part";
        var sourceTicks = attributes.LastWriteTimeUtc.Ticks;
        var checkpoint = _checkpointStore.Load(id);
        var offset = 0L;
        if (options.Resume && checkpoint is not null &&
            checkpoint.TotalBytes == attributes.Size &&
            checkpoint.SourceLastWriteUtcTicks == sourceTicks &&
            checkpoint.PartialPath == partialPath &&
            File.Exists(partialPath))
        {
            var partialLength = new FileInfo(partialPath).Length;
            if (partialLength >= 0 && partialLength <= attributes.Size)
                offset = partialLength;
        }

        if (!options.Resume || offset == 0)
        {
            TryDeleteLocal(partialPath);
            _checkpointStore.Delete(id);
            offset = 0;
        }

        reportBytes(offset);
        SaveCheckpoint(id, SftpTransferDirection.Download, remotePath, destination,
            partialPath, attributes.Size, offset, sourceTicks);
        try
        {
            using var remote = client.OpenRead(remotePath);
            remote.Seek(offset, SeekOrigin.Begin);
            using var local = new FileStream(
                partialPath,
                offset > 0 ? FileMode.Open : FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                TransferBufferSize,
                FileOptions.SequentialScan);
            local.SetLength(offset);
            local.Position = offset;
            CopyWithCheckpoint(
                remote,
                local,
                offset,
                attributes.Size,
                transferred =>
                {
                    reportBytes(transferred);
                    SaveCheckpoint(id, SftpTransferDirection.Download, remotePath, destination,
                        partialPath, attributes.Size, transferred, sourceTicks);
                },
                ct);
            local.Flush(flushToDisk: true);

            string? checksum = null;
            if (options.VerifyChecksum)
            {
                var remoteHash = ComputeRemoteSha256(client, remotePath, ct);
                var localHash = ComputeLocalSha256(partialPath, ct);
                if (!CryptographicOperations.FixedTimeEquals(localHash, remoteHash))
                    throw new SftpChecksumMismatchException(remotePath, destination);
                checksum = Convert.ToHexString(localHash);
            }

            File.Move(partialPath, destination, replaceExistingDestination);
            _checkpointStore.Delete(id);
            reportBytes(attributes.Size);
            return new FileTransferResult(attributes.Size, offset, checksum);
        }
        catch (SftpChecksumMismatchException)
        {
            // A checksum retry must re-download bytes instead of repeatedly validating
            // the same corrupt completed partial.
            TryDeleteLocal(partialPath);
            _checkpointStore.Delete(id);
            throw;
        }
        catch
        {
            if (!options.Resume)
            {
                TryDeleteLocal(partialPath);
                _checkpointStore.Delete(id);
            }
            throw;
        }
    }

    private async Task<T> RunWithRetryAsync<T>(
        SftpTransferDirection direction,
        string relativePath,
        SftpTransferOptions options,
        IProgress<SftpTransferProgress>? progress,
        long previousBytes,
        long totalBytes,
        int filesCompleted,
        int totalFiles,
        Func<int, Action<long>, T> operation,
        CancellationToken ct)
    {
        var attempts = options.RetryEnabled ? options.MaxRetries + 1 : 1;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await Task.Run(() => operation(
                    attempt,
                    fileBytes => progress?.Report(new SftpTransferProgress(
                        direction,
                        SftpTransferPhase.Transferring,
                        relativePath,
                        Math.Min(totalBytes, previousBytes + Math.Max(0, fileBytes)),
                        totalBytes,
                        filesCompleted,
                        totalFiles,
                        attempt))), ct).ConfigureAwait(false);
            }
            catch (Exception error) when (
                attempt < attempts &&
                IsTransient(error) &&
                !ct.IsCancellationRequested)
            {
                lastError = error;
                progress?.Report(new SftpTransferProgress(
                    direction,
                    SftpTransferPhase.Retrying,
                    relativePath,
                    previousBytes,
                    totalBytes,
                    filesCompleted,
                    totalFiles,
                    attempt + 1,
                    error.Message));
                await ReconnectForRetryAsync(ct).ConfigureAwait(false);
                var delayMs = Math.Min(
                    30_000,
                    options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
            }
        }

        throw lastError ?? new IOException("SFTP transfer failed.");
    }

    private async Task RunWithRetryAsync(
        SftpTransferDirection direction,
        string relativePath,
        SftpTransferOptions options,
        IProgress<SftpTransferProgress>? progress,
        long previousBytes,
        long totalBytes,
        int filesCompleted,
        int totalFiles,
        Action operation,
        CancellationToken ct) => await RunWithRetryAsync(
        direction,
        relativePath,
        options,
        progress,
        previousBytes,
        totalBytes,
        filesCompleted,
        totalFiles,
        (_, _) =>
        {
            operation();
            return true;
        },
        ct).ConfigureAwait(false);

    private async Task<T> RunWithRetryAsync<T>(
        SftpTransferDirection direction,
        string relativePath,
        SftpTransferOptions options,
        IProgress<SftpTransferProgress>? progress,
        long previousBytes,
        long totalBytes,
        int filesCompleted,
        int totalFiles,
        Func<T> operation,
        CancellationToken ct) => await RunWithRetryAsync(
        direction,
        relativePath,
        options,
        progress,
        previousBytes,
        totalBytes,
        filesCompleted,
        totalFiles,
        (_, _) => operation(),
        ct).ConfigureAwait(false);

    private async Task ReconnectForRetryAsync(CancellationToken ct)
    {
        if (_reconnectAsync is null)
            return;
        var reconnected = await _reconnectAsync(ct).ConfigureAwait(false);
        if (reconnected is not { IsConnected: true })
            throw new InvalidOperationException("SFTP reconnect did not produce a connected client.");
    }

    private static bool IsTransient(Exception error) => error switch
    {
        OperationCanceledException => false,
        SftpPathNotFoundException => false,
        SftpPermissionDeniedException => false,
        SshAuthenticationException => false,
        SftpChecksumMismatchException => true,
        SshConnectionException => true,
        SshOperationTimeoutException => true,
        SocketException => true,
        TimeoutException => true,
        EndOfStreamException => true,
        ObjectDisposedException => true,
        IOException => true,
        InvalidOperationException invalid when invalid.Message.Contains(
            "not connected", StringComparison.OrdinalIgnoreCase) => true,
        _ => false,
    };

    private static IReadOnlyList<RemoteTreeEntry> EnumerateRemoteTreeCore(
        SftpClient client,
        string path,
        CancellationToken ct)
    {
        var root = RemotePath.Normalize(path);
        var result = new List<RemoteTreeEntry>();
        Walk(root, "", 0);
        return result;

        void Walk(string directory, string relativeDirectory, int depth)
        {
            ct.ThrowIfCancellationRequested();
            var entries = client.ListDirectory(directory)
                .Where(item => item.Name is not "." and not "..")
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var item in entries)
            {
                ct.ThrowIfCancellationRequested();
                ValidateRemotePathSegment(item.Name);
                if (result.Count >= MaxTreeEntries)
                    throw new IOException($"Remote tree exceeds {MaxTreeEntries:N0} entries.");
                var relativePath = string.IsNullOrEmpty(relativeDirectory)
                    ? item.Name
                    : relativeDirectory + "/" + item.Name;
                var entry = new RemoteFileEntry
                {
                    Name = item.Name,
                    FullPath = item.FullName,
                    IsDirectory = item.IsDirectory,
                    IsSymbolicLink = item.IsSymbolicLink,
                    IsRegularFile = item.IsRegularFile,
                    Size = item.Length,
                    Modified = item.LastWriteTime,
                };
                result.Add(new RemoteTreeEntry(entry, relativePath, depth));
                if (item.IsDirectory && !item.IsSymbolicLink)
                {
                    if (depth >= MaxTreeDepth)
                        throw new IOException($"Remote tree exceeds the maximum depth of {MaxTreeDepth}.");
                    Walk(item.FullName, relativePath, depth + 1);
                }
            }
        }
    }

    private static SftpDeletePreview CreateDeletePreview(
        SftpClient client,
        string path,
        CancellationToken ct)
    {
        var root = RequireSafeDeleteRoot(path);
        if (!client.Exists(root))
            throw new FileNotFoundException($"Remote path does not exist: {root}", root);

        if (!IsDirectoryNotLink(client, root))
        {
            var attributes = client.GetAttributes(root);
            return new SftpDeletePreview(
                root,
                FileCount: 1,
                DirectoryCount: 0,
                TotalBytes: Math.Max(0, attributes.Size),
                PreviewPaths: [RemotePath.GetName(root)]);
        }

        var tree = EnumerateRemoteTreeCore(client, root, ct);
        var files = tree.Where(entry => !entry.Entry.IsDirectory || entry.Entry.IsSymbolicLink).ToArray();
        var directories = tree.Count(entry => entry.Entry.IsDirectory && !entry.Entry.IsSymbolicLink) + 1;
        return new SftpDeletePreview(
            root,
            files.Length,
            directories,
            files.Sum(entry => Math.Max(0, entry.Entry.Size)),
            tree.Take(20).Select(entry => entry.RelativePath).ToArray());
    }

    private static void DeletePathRecursively(SftpClient client, string path, CancellationToken ct)
    {
        var root = RequireSafeDeleteRoot(path);
        if (!client.Exists(root))
            throw new FileNotFoundException($"Remote path does not exist: {root}", root);

        if (!IsDirectoryNotLink(client, root))
        {
            client.DeleteFile(root);
            return;
        }

        var tree = EnumerateRemoteTreeCore(client, root, ct);
        foreach (var entry in tree.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Entry.IsDirectory && !entry.Entry.IsSymbolicLink)
                client.DeleteDirectory(entry.Entry.FullPath);
            else
                client.DeleteFile(entry.Entry.FullPath);
        }
        ct.ThrowIfCancellationRequested();
        client.DeleteDirectory(root);
    }

    private static string RequireSafeDeleteRoot(string path)
    {
        var root = RemotePath.Normalize(path);
        if (root == "/")
            throw new IOException("The remote root cannot be deleted.");
        return root;
    }

    private static bool IsDirectoryNotLink(SftpClient client, string path)
    {
        var attributes = client.GetAttributes(path);
        return attributes.IsDirectory && !attributes.IsSymbolicLink;
    }

    private static void ApplyPermissions(
        SftpClient client,
        string path,
        int unixMode,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        client.ChangePermissions(path, unchecked((short)unixMode));
    }

    private static LocalTree EnumerateLocalTree(DirectoryInfo root, CancellationToken ct)
    {
        var directories = new List<LocalDirectory>();
        var files = new List<LocalFile>();
        Walk(root, "");
        return new LocalTree(directories, files);

        void Walk(DirectoryInfo directory, string relativeDirectory)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var childDirectory in directory.EnumerateDirectories()
                         .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (childDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                var relative = string.IsNullOrEmpty(relativeDirectory)
                    ? childDirectory.Name
                    : Path.Combine(relativeDirectory, childDirectory.Name);
                directories.Add(new LocalDirectory(childDirectory, relative));
                if (directories.Count + files.Count >= MaxTreeEntries)
                    throw new IOException($"Local tree exceeds {MaxTreeEntries:N0} entries.");
                if (relative.Count(ch => ch == Path.DirectorySeparatorChar) >= MaxTreeDepth)
                    throw new IOException($"Local tree exceeds the maximum depth of {MaxTreeDepth}.");
                Walk(childDirectory, relative);
            }

            foreach (var file in directory.EnumerateFiles()
                         .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                var relative = string.IsNullOrEmpty(relativeDirectory)
                    ? file.Name
                    : Path.Combine(relativeDirectory, file.Name);
                files.Add(new LocalFile(file, relative));
                if (directories.Count + files.Count >= MaxTreeEntries)
                    throw new IOException($"Local tree exceeds {MaxTreeEntries:N0} entries.");
            }
        }
    }

    private static void EnsureRemoteDirectory(SftpClient client, string path)
    {
        var normalized = RemotePath.Normalize(path);
        if (normalized == "/")
            return;
        if (client.Exists(normalized))
        {
            if (!client.GetAttributes(normalized).IsDirectory)
                throw new IOException($"Remote path is not a directory: {normalized}");
            return;
        }
        EnsureRemoteDirectory(client, RemotePath.GetDirectory(normalized));
        client.CreateDirectory(normalized);
    }

    private static string CombineRelative(string remoteRoot, string relativePath)
    {
        var result = remoteRoot;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            result = RemotePath.Combine(result, segment);
        }
        return result;
    }

    private static string CombineLocalRelative(string localRoot, string relativePath)
    {
        var root = Path.GetFullPath(localRoot);
        var result = root;
        foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            ValidateLocalFileName(segment);
            result = Path.Combine(result, segment);
        }
        var candidate = Path.GetFullPath(result);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The remote path would escape the selected local directory.");
        }
        return candidate;
    }

    private static string CreateAvailableRemotePath(SftpClient client, string path)
    {
        var directory = RemotePath.GetDirectory(path);
        var name = RemotePath.GetName(path);
        var extension = Path.GetExtension(name);
        var stem = extension.Length == 0 ? name : name[..^extension.Length];
        for (var index = 1; index <= 9_999; index++)
        {
            var candidate = RemotePath.Combine(directory, $"{stem} ({index}){extension}");
            if (!client.Exists(candidate))
                return candidate;
        }
        throw new IOException($"Could not find an available remote name for: {path}");
    }

    private static string CreateAvailableLocalPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name);
        var stem = extension.Length == 0 ? name : name[..^extension.Length];
        for (var index = 1; index <= 9_999; index++)
        {
            var candidate = Path.Combine(directory!, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
        throw new IOException($"Could not find an available local name for: {path}");
    }

    private static void ValidateRemotePathSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name is "." or ".." ||
            name.Contains('/') ||
            name.Contains('\0') ||
            name.Any(char.IsControl))
        {
            throw new IOException($"The server returned an unsafe remote path segment: {name}");
        }
    }

    private static void ValidateLocalFileName(string name)
    {
        ValidateRemotePathSegment(name);
        if (string.IsNullOrWhiteSpace(name) ||
            name.Length > 255 ||
            name is "." or ".." ||
            name.EndsWith(' ') ||
            name.EndsWith('.') ||
            name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\']) >= 0 ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Path.IsPathRooted(name) ||
            IsReservedWindowsDeviceName(name))
        {
            throw new IOException($"The server returned an unsafe file name: {name}");
        }
    }

    private static bool IsReservedWindowsDeviceName(string name)
    {
        var stem = name.Split('.', 2)[0].TrimEnd(' ', '.');
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[3] is >= '1' and <= '9';
    }

    private static void CopyWithCheckpoint(
        Stream source,
        Stream destination,
        long initialOffset,
        long totalBytes,
        Action<long> checkpoint,
        CancellationToken ct)
    {
        var buffer = new byte[TransferBufferSize];
        var transferred = initialOffset;
        var lastCheckpointBytes = initialOffset;
        var lastCheckpointAt = Stopwatch.GetTimestamp();
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
            transferred += read;
            var now = Stopwatch.GetTimestamp();
            if (transferred - lastCheckpointBytes >= CheckpointByteInterval ||
                Stopwatch.GetElapsedTime(lastCheckpointAt, now) >= CheckpointTimeInterval)
            {
                checkpoint(Math.Min(transferred, totalBytes));
                lastCheckpointBytes = transferred;
                lastCheckpointAt = now;
            }
        }
        checkpoint(Math.Min(transferred, totalBytes));
    }

    private void SaveCheckpoint(
        string id,
        SftpTransferDirection direction,
        string sourcePath,
        string destinationPath,
        string partialPath,
        long totalBytes,
        long transferredBytes,
        long sourceLastWriteUtcTicks) => _checkpointStore.Save(new SftpTransferCheckpoint
    {
        Id = id,
        Scope = _checkpointScope,
        Direction = direction,
        SourcePath = sourcePath,
        DestinationPath = destinationPath,
        PartialPath = partialPath,
        TotalBytes = totalBytes,
        TransferredBytes = transferredBytes,
        SourceLastWriteUtcTicks = sourceLastWriteUtcTicks,
    });

    private static byte[] ComputeLocalSha256(string path, CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        return ComputeSha256(stream, ct);
    }

    private static byte[] ComputeRemoteSha256(SftpClient client, string path, CancellationToken ct)
    {
        using var stream = client.OpenRead(path);
        return ComputeSha256(stream, ct);
    }

    private static byte[] ComputeSha256(Stream stream, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[TransferBufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return hash.GetHashAndReset();
    }

    private static void PromoteRemoteFile(
        SftpClient client,
        string partialPath,
        string destinationPath,
        bool overwrite)
    {
        string? backupPath = null;
        if (client.Exists(destinationPath))
        {
            if (!overwrite)
                throw new IOException($"Remote file already exists: {destinationPath}");
            if (client.GetAttributes(destinationPath).IsDirectory)
                throw new IOException($"A remote directory already exists: {destinationPath}");
            backupPath = destinationPath + $".sutty-{Guid.NewGuid():N}.backup";
            client.RenameFile(destinationPath, backupPath);
        }

        try
        {
            client.RenameFile(partialPath, destinationPath);
        }
        catch
        {
            if (backupPath is not null && client.Exists(backupPath) && !client.Exists(destinationPath))
                client.RenameFile(backupPath, destinationPath);
            throw;
        }

        if (backupPath is not null)
            DeleteRemoteIfExists(client, backupPath);
    }

    private static void DeleteRemoteIfExists(SftpClient client, string path)
    {
        try
        {
            if (client.Exists(path) && !client.GetAttributes(path).IsDirectory)
                client.DeleteFile(path);
        }
        catch
        {
            // Cleanup must not hide the original transfer error.
        }
    }

    private static void TryDeleteLocal(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void ReportCompleted(
        IProgress<SftpTransferProgress>? progress,
        SftpTransferDirection direction,
        string relativePath,
        long totalBytes,
        int filesCompleted,
        int totalFiles) => progress?.Report(new SftpTransferProgress(
        direction,
        SftpTransferPhase.Completed,
        relativePath,
        totalBytes,
        totalBytes,
        filesCompleted,
        totalFiles,
        1));

    private sealed record FileTransferResult(
        long Bytes,
        long ResumedBytes,
        string? Sha256,
        bool Skipped = false);
    private sealed record LocalDirectory(DirectoryInfo Info, string RelativePath);
    private sealed record LocalFile(FileInfo Info, string RelativePath);
    private sealed record LocalTree(List<LocalDirectory> Directories, List<LocalFile> Files);
}

public sealed class SftpChecksumMismatchException(string sourcePath, string destinationPath)
    : IOException($"SHA-256 verification failed: {sourcePath} -> {destinationPath}")
{
    public string SourcePath { get; } = sourcePath;
    public string DestinationPath { get; } = destinationPath;
}

/// <summary>Raised when a job retained the interactive Ask policy but reaches a collision.</summary>
public sealed class SftpTransferConflictException(string sourcePath, string destinationPath)
    : IOException($"A transfer conflict requires a decision: {sourcePath} -> {destinationPath}")
{
    public string SourcePath { get; } = sourcePath;
    public string DestinationPath { get; } = destinationPath;
}
