using sutty.Core.Models;
using System.Diagnostics;
using System.Security.Cryptography;

namespace sutty.Core.Sftp;

/// <summary>
/// SFTP 전송 계약을 로컬 파일 복사로 검증하는 테스트/도구용 어댑터.
/// 제품 UI는 세션이 없을 때 이 서비스를 노출하지 않는다.
/// </summary>
public sealed class LocalFileService : ISftpService
{
    public Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<RemoteFileEntry>>(() =>
        {
            if (path == "/")
            {
                return DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d => new RemoteFileEntry
                    {
                        Name = d.Name.TrimEnd('\\'),
                        FullPath = d.Name,
                        IsDirectory = true,
                    })
                    .ToList();
            }

            var dir = new DirectoryInfo(path);
            if (!dir.Exists)
                return Array.Empty<RemoteFileEntry>();

            var list = new List<RemoteFileEntry>();
            try
            {
                foreach (var d in dir.EnumerateDirectories())
                {
                    if ((d.Attributes & FileAttributes.Hidden) != 0) continue;
                    list.Add(new RemoteFileEntry
                    {
                        Name = d.Name,
                        FullPath = d.FullName,
                        IsDirectory = true,
                        Modified = d.LastWriteTime,
                    });
                }
                foreach (var f in dir.EnumerateFiles())
                {
                    if ((f.Attributes & FileAttributes.Hidden) != 0) continue;
                    list.Add(new RemoteFileEntry
                    {
                        Name = f.Name,
                        FullPath = f.FullName,
                        Size = f.Length,
                        Modified = f.LastWriteTime,
                    });
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 접근 불가 폴더는 조용히 비워 둔다
            }

            return list
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);

    public Task<IReadOnlyList<RemoteTreeEntry>> EnumerateTreeAsync(
        string path,
        CancellationToken ct = default) => Task.Run<IReadOnlyList<RemoteTreeEntry>>(() =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = new DirectoryInfo(path);
        if (!root.Exists)
            throw new DirectoryNotFoundException(path);

        var result = new List<RemoteTreeEntry>();
        Walk(root, "", 0);

        return result;

        void Walk(DirectoryInfo directory, string relativeDirectory, int depth)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos()
                         .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                var isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                var relativePath = string.IsNullOrEmpty(relativeDirectory)
                    ? entry.Name
                    : Path.Combine(relativeDirectory, entry.Name);
                result.Add(new RemoteTreeEntry(
                    new RemoteFileEntry
                    {
                        Name = entry.Name,
                        FullPath = entry.FullName,
                        IsDirectory = isDirectory,
                        IsSymbolicLink = isLink,
                        IsRegularFile = !isDirectory && !isLink,
                        Size = entry is FileInfo file ? file.Length : 0,
                        Modified = entry.LastWriteTimeUtc,
                    },
                    relativePath,
                    depth));

                if (isDirectory && !isLink)
                    Walk((DirectoryInfo)entry, relativePath, depth + 1);
            }
        }
    }, ct);

    public async Task<IReadOnlyList<RemoteTreeEntry>> SearchByNameAsync(
        string path,
        string query,
        int maximumResults = 500,
        CancellationToken ct = default)
    {
        var normalized = SftpSearchRules.Normalize(query, maximumResults);
        var entries = await EnumerateTreeAsync(path, ct).ConfigureAwait(false);
        return entries
            .Where(entry => entry.Entry.Name.Contains(
                normalized.Query,
                StringComparison.OrdinalIgnoreCase))
            .Take(normalized.MaximumResults)
            .ToList();
    }

    public Task<SftpTransferResult> UploadPathAsync(
        string localPath,
        string remotePath,
        SftpTransferOptions? options = null,
        IProgress<SftpTransferProgress>? progress = null,
        CancellationToken ct = default) => CopyPathAdvancedAsync(
            localPath,
            remotePath,
            SftpTransferDirection.Upload,
            options,
            progress,
            ct);

    public Task<SftpTransferResult> DownloadPathAsync(
        string remotePath,
        string localPath,
        SftpTransferOptions? options = null,
        IProgress<SftpTransferProgress>? progress = null,
        CancellationToken ct = default) => CopyPathAdvancedAsync(
            remotePath,
            localPath,
            SftpTransferDirection.Download,
            options,
            progress,
            ct);

    /// <summary>로컬 모드의 "업로드"는 로컬 복사.</summary>
    public Task UploadFileAsync(string localPath, string remoteDirectory, bool overwrite = false,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dest = Path.Combine(remoteDirectory, Path.GetFileName(localPath));
        return CopyFileSafelyAsync(localPath, dest, overwrite, progress, ct);
    }

    public Task DownloadFileAsync(string remotePath, string localPath, bool overwrite = false,
        IProgress<double>? progress = null, CancellationToken ct = default)
        => CopyFileSafelyAsync(remotePath, localPath, overwrite, progress, ct);

    public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var source = Path.GetFullPath(sourcePath);
            var destination = Path.GetFullPath(destinationPath);
            var sourceRoot = Path.GetPathRoot(source);
            if (!string.IsNullOrEmpty(sourceRoot) && string.Equals(
                    source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("A filesystem root cannot be moved.");
            }
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The source and destination paths are the same.");
            if (!File.Exists(source) && !Directory.Exists(source))
                throw new FileNotFoundException($"Path does not exist: {source}", source);
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException($"Path already exists: {destination}");
            var destinationParent = Path.GetDirectoryName(destination);
            if (string.IsNullOrEmpty(destinationParent) || !Directory.Exists(destinationParent))
                throw new DirectoryNotFoundException(destinationParent);
            if (Directory.Exists(source) && destination.StartsWith(
                    source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("A directory cannot be moved inside itself.");
            }

            if (Directory.Exists(source)) Directory.Move(source, destination);
            else File.Move(source, destination);
        }, ct);

    public Task DeleteFileAsync(string path, CancellationToken ct = default)
        => Task.Run(() => { ct.ThrowIfCancellationRequested(); File.Delete(path); }, ct);

    public Task DeleteDirectoryAsync(string path, CancellationToken ct = default)
        => Task.Run(() => { ct.ThrowIfCancellationRequested(); Directory.Delete(path, recursive: false); }, ct);

    public Task<SftpDeletePreview> PreviewDeleteAsync(string path, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var root = RequireSafeLocalDeleteRoot(path);
            ct.ThrowIfCancellationRequested();
            if (File.Exists(root))
            {
                var file = new FileInfo(root);
                return new SftpDeletePreview(
                    root,
                    FileCount: 1,
                    DirectoryCount: 0,
                    TotalBytes: Math.Max(0, file.Length),
                    PreviewPaths: [file.Name]);
            }
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException(root);

            var tree = EnumerateTreeAsync(root, ct).GetAwaiter().GetResult();
            var files = tree.Where(entry => !entry.Entry.IsDirectory || entry.Entry.IsSymbolicLink).ToArray();
            return new SftpDeletePreview(
                root,
                files.Length,
                tree.Count(entry => entry.Entry.IsDirectory && !entry.Entry.IsSymbolicLink) + 1,
                files.Sum(entry => Math.Max(0, entry.Entry.Size)),
                tree.Take(20).Select(entry => entry.RelativePath).ToArray());
        }, ct);

    public Task DeletePathRecursiveAsync(string path, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var root = RequireSafeLocalDeleteRoot(path);
            if (File.Exists(root))
            {
                File.Delete(root);
                return;
            }
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException(root);
            DeleteDirectoryTree(root, ct);
        }, ct);

    public Task ChangePermissionsAsync(
        string path,
        int unixMode,
        bool recursive = false,
        CancellationToken ct = default)
        => Task.FromException(new NotSupportedException(
            "Unix permission changes require an SFTP server and are unavailable for the local test adapter."));

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException($"Path already exists: {path}");
            Directory.CreateDirectory(path);
        }, ct);

    private static string RequireSafeLocalDeleteRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var volumeRoot = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(volumeRoot) &&
            string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("A filesystem root cannot be deleted.");
        }
        return fullPath;
    }

    private static void DeleteDirectoryTree(string root, CancellationToken ct)
    {
        var directory = new DirectoryInfo(root);
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            var isDirectory = entry.Attributes.HasFlag(FileAttributes.Directory);
            var isLink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (isDirectory && !isLink)
            {
                DeleteDirectoryTree(entry.FullName, ct);
            }
            else if (isDirectory)
            {
                Directory.Delete(entry.FullName, recursive: false);
            }
            else
            {
                File.Delete(entry.FullName);
            }
        }
        ct.ThrowIfCancellationRequested();
        Directory.Delete(root, recursive: false);
    }

    private static Task CopyFileSafelyAsync(string sourcePath, string destinationPath, bool overwrite,
        IProgress<double>? progress, CancellationToken ct) => Task.Run(() =>
    {
        if (Directory.Exists(destinationPath))
            throw new IOException($"A directory already exists at the destination: {destinationPath}");
        if (!overwrite && File.Exists(destinationPath))
            throw new IOException($"File already exists: {destinationPath}");

        progress?.Report(0.0);
        var temporaryPath = destinationPath + $".sutty-{Guid.NewGuid():N}.part";
        try
        {
            using (var source = File.OpenRead(sourcePath))
            using (var destination = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var total = source.Length;
                var buffer = new byte[81920];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    destination.Write(buffer, 0, read);
                    progress?.Report(total == 0 ? 1.0 : (double)source.Position / total);
                }
                destination.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite);
            progress?.Report(1.0);
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            throw;
        }
    }, ct);

    private static async Task<SftpTransferResult> CopyPathAdvancedAsync(
        string sourcePath,
        string destinationPath,
        SftpTransferDirection direction,
        SftpTransferOptions? options,
        IProgress<SftpTransferProgress>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var normalized = (options ?? SftpTransferOptions.Default).Normalize();
        var started = Stopwatch.GetTimestamp();
        var sourceFile = new FileInfo(sourcePath);
        List<LocalTransferFile> files;

        if (sourceFile.Exists)
        {
            files = [new LocalTransferFile(sourceFile.FullName, Path.GetFullPath(destinationPath), sourceFile.Name, sourceFile.Length)];
        }
        else
        {
            var sourceDirectory = new DirectoryInfo(sourcePath);
            if (!sourceDirectory.Exists)
                throw new FileNotFoundException($"Source path does not exist: {sourcePath}", sourcePath);

            progress?.Report(new SftpTransferProgress(
                direction,
                SftpTransferPhase.Enumerating,
                sourceDirectory.Name,
                0,
                0,
                0,
                0,
                1));

            var sourceTree = EnumerateLocalSourceTree(sourceDirectory, ct);
            var destinationRoot = Path.GetFullPath(destinationPath);
            Directory.CreateDirectory(destinationRoot);
            foreach (var directory in sourceTree.Directories)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.Combine(destinationRoot, directory.RelativePath));
            }

            files = sourceTree.Files
                .Select(source =>
                {
                    return new LocalTransferFile(
                        source.Info.FullName,
                        Path.Combine(destinationRoot, source.RelativePath),
                        source.RelativePath,
                        source.Info.Length);
                })
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var totalBytes = files.Sum(file => file.Length);
        long completedBytes = 0;
        long copiedBytes = 0;
        long resumedBytes = 0;
        var filesSkipped = 0;
        string? singleFileHash = null;

        for (var index = 0; index < files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[index];
            var outcome = await CopyOneWithRetryAsync(
                file,
                direction,
                normalized,
                completedBytes,
                totalBytes,
                index,
                files.Count,
                progress,
                ct).ConfigureAwait(false);
            completedBytes += file.Length;
            copiedBytes += outcome.Bytes;
            resumedBytes += outcome.ResumedBytes;
            filesSkipped += outcome.Skipped ? 1 : 0;
            if (files.Count == 1)
                singleFileHash = outcome.Sha256;
        }

        progress?.Report(new SftpTransferProgress(
            direction,
            SftpTransferPhase.Completed,
            Path.GetFileName(sourcePath),
            totalBytes,
            totalBytes,
            files.Count,
            files.Count,
            1));

        return new SftpTransferResult(
            direction,
            Path.GetFullPath(sourcePath),
            Path.GetFullPath(destinationPath),
            files.Count - filesSkipped,
            copiedBytes,
            resumedBytes,
            singleFileHash,
            Stopwatch.GetElapsedTime(started),
            filesSkipped);
    }

    private static async Task<LocalCopyOutcome> CopyOneWithRetryAsync(
        LocalTransferFile file,
        SftpTransferDirection direction,
        SftpTransferOptions options,
        long completedBytes,
        long totalBytes,
        int completedFiles,
        int totalFiles,
        IProgress<SftpTransferProgress>? progress,
        CancellationToken ct)
    {
        var maxAttempts = options.RetryEnabled ? options.MaxRetries + 1 : 1;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await CopyOneAsync(
                    file,
                    direction,
                    options,
                    completedBytes,
                    totalBytes,
                    completedFiles,
                    totalFiles,
                    attempt,
                    progress,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException && attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                progress?.Report(new SftpTransferProgress(
                    direction,
                    SftpTransferPhase.Retrying,
                    file.RelativePath,
                    completedBytes,
                    totalBytes,
                    completedFiles,
                    totalFiles,
                    attempt + 1,
                    ex.Message));
                var multiplier = 1 << Math.Min(attempt - 1, 6);
                await Task.Delay(options.InitialRetryDelay * multiplier, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<LocalCopyOutcome> CopyOneAsync(
        LocalTransferFile file,
        SftpTransferDirection direction,
        SftpTransferOptions options,
        long completedBytes,
        long totalBytes,
        int completedFiles,
        int totalFiles,
        int attempt,
        IProgress<SftpTransferProgress>? progress,
        CancellationToken ct)
    {
        var destination = file.DestinationPath;
        var replaceExistingDestination = false;
        if (Directory.Exists(destination))
            throw new IOException($"A directory already exists: {destination}");

        if (File.Exists(destination) && options.VerifyChecksum)
        {
            var sourceHash = await ComputeSha256Async(file.SourcePath, ct).ConfigureAwait(false);
            var destinationHash = await ComputeSha256Async(destination, ct).ConfigureAwait(false);
            if (CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
            {
                progress?.Report(new SftpTransferProgress(
                    direction,
                    SftpTransferPhase.Preparing,
                    file.RelativePath,
                    completedBytes + file.Length,
                    totalBytes,
                    completedFiles,
                    totalFiles,
                    attempt));
                return new LocalCopyOutcome(file.Length, file.Length,
                    Convert.ToHexString(sourceHash).ToLowerInvariant());
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
                    progress?.Report(new SftpTransferProgress(
                        direction,
                        SftpTransferPhase.Preparing,
                        file.RelativePath,
                        completedBytes + file.Length,
                        totalBytes,
                        completedFiles,
                        totalFiles,
                        attempt));
                    return new LocalCopyOutcome(0, 0, null, Skipped: true);
                case SftpConflictPolicy.Rename:
                    destination = CreateAvailableLocalPath(destination);
                    break;
                case SftpConflictPolicy.NewerOnly:
                    if (File.GetLastWriteTimeUtc(destination) >= File.GetLastWriteTimeUtc(file.SourcePath))
                    {
                        progress?.Report(new SftpTransferProgress(
                            direction,
                            SftpTransferPhase.Preparing,
                            file.RelativePath,
                            completedBytes + file.Length,
                            totalBytes,
                            completedFiles,
                            totalFiles,
                            attempt));
                        return new LocalCopyOutcome(0, 0, null, Skipped: true);
                    }
                    replaceExistingDestination = true;
                    break;
                default:
                    throw new SftpTransferConflictException(file.SourcePath, destination);
            }
        }

        var destinationDirectory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        var partialPath = destination + ".sutty.part";
        if (!options.Resume && File.Exists(partialPath))
            File.Delete(partialPath);

        var resumed = options.Resume && File.Exists(partialPath)
            ? Math.Min(new FileInfo(partialPath).Length, file.Length)
            : 0;
        if (File.Exists(partialPath) && new FileInfo(partialPath).Length > file.Length)
        {
            File.Delete(partialPath);
            resumed = 0;
        }

        progress?.Report(new SftpTransferProgress(
            direction,
            SftpTransferPhase.Preparing,
            file.RelativePath,
            completedBytes + resumed,
            totalBytes,
            completedFiles,
            totalFiles,
            attempt));

        await using (var source = new FileStream(
            file.SourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destinationStream = new FileStream(
            partialPath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            source.Position = resumed;
            destinationStream.Position = resumed;
            destinationStream.SetLength(resumed);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await destinationStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                progress?.Report(new SftpTransferProgress(
                    direction,
                    SftpTransferPhase.Transferring,
                    file.RelativePath,
                    completedBytes + source.Position,
                    totalBytes,
                    completedFiles,
                    totalFiles,
                    attempt));
            }
            await destinationStream.FlushAsync(ct).ConfigureAwait(false);
        }

        string? hash = null;
        if (options.VerifyChecksum)
        {
            progress?.Report(new SftpTransferProgress(
                direction,
                SftpTransferPhase.Verifying,
                file.RelativePath,
                completedBytes + file.Length,
                totalBytes,
                completedFiles,
                totalFiles,
                attempt));
            var sourceHash = await ComputeSha256Async(file.SourcePath, ct).ConfigureAwait(false);
            var destinationHash = await ComputeSha256Async(partialPath, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
                throw new IOException($"Checksum mismatch: {file.RelativePath}");
            hash = Convert.ToHexString(sourceHash).ToLowerInvariant();
        }

        File.Move(partialPath, destination, replaceExistingDestination);
        return new LocalCopyOutcome(file.Length, resumed, hash);
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

    private static async Task<byte[]> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
    }

    private static LocalSourceTree EnumerateLocalSourceTree(
        DirectoryInfo root,
        CancellationToken ct)
    {
        var directories = new List<LocalSourceDirectory>();
        var files = new List<LocalSourceFile>();
        Walk(root, "");
        return new LocalSourceTree(directories, files);

        void Walk(DirectoryInfo directory, string relativeDirectory)
        {
            foreach (var child in directory.EnumerateDirectories()
                         .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                var relative = string.IsNullOrEmpty(relativeDirectory)
                    ? child.Name
                    : Path.Combine(relativeDirectory, child.Name);
                directories.Add(new LocalSourceDirectory(relative));
                Walk(child, relative);
            }

            foreach (var file in directory.EnumerateFiles()
                         .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                var relative = string.IsNullOrEmpty(relativeDirectory)
                    ? file.Name
                    : Path.Combine(relativeDirectory, file.Name);
                files.Add(new LocalSourceFile(file, relative));
            }
        }
    }

    private sealed record LocalTransferFile(
        string SourcePath,
        string DestinationPath,
        string RelativePath,
        long Length);

    private sealed record LocalCopyOutcome(
        long Bytes,
        long ResumedBytes,
        string? Sha256,
        bool Skipped = false);
    private sealed record LocalSourceDirectory(string RelativePath);
    private sealed record LocalSourceFile(FileInfo Info, string RelativePath);
    private sealed record LocalSourceTree(
        List<LocalSourceDirectory> Directories,
        List<LocalSourceFile> Files);
}
