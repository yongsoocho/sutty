using Renci.SshNet;
using sutty.Core.Models;

namespace sutty.Core.Sftp;

/// <summary>
/// Each SSH.NET client has one owner at a time. Interactive listings can use a
/// separately verified read-only connection while a transfer owns the main client.
/// </summary>
public sealed partial class SshNetSftpService : ISftpService
{
    private readonly Func<SftpClient?> _clientProvider;
    private readonly Func<CancellationToken, Task<SftpClient?>>? _reconnectAsync;
    private readonly SftpTransferCheckpointStore _checkpointStore;
    private string _checkpointScope;
    internal void BindVerifiedEndpoint(string identity) => _checkpointScope = identity;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _browseGate = new(1, 1);
    private readonly Func<CancellationToken, Task<SftpClient>>? _openBrowseClient;
    private CancellationTokenSource _browseLifetime = new();
    private SftpClient? _browseClient;

    public SshNetSftpService(
        Func<SftpClient?> clientProvider,
        string checkpointScope = "default",
        Func<CancellationToken, Task<SftpClient?>>? reconnectAsync = null,
        SftpTransferCheckpointStore? checkpointStore = null,
        Func<CancellationToken, Task<SftpClient>>? openBrowseClient = null)
    {
        _clientProvider = clientProvider;
        _openBrowseClient = openBrowseClient;
        _checkpointScope = string.IsNullOrWhiteSpace(checkpointScope) ? "default" : checkpointScope;
        _reconnectAsync = reconnectAsync;
        _checkpointStore = checkpointStore ?? SftpTransferCheckpointStore.Default;
    }

    private SftpClient Client =>
        _clientProvider() is { IsConnected: true } client
            ? client
            : throw new InvalidOperationException("SFTP 채널이 연결되어 있지 않습니다.");

    public async Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(
        string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Ordinary navigation needs no additional SSH authentication. On contention,
        // only listing uses the auxiliary connection; it never writes or retries a job.
        if (_openBrowseClient is null || _operationGate.Wait(0, ct))
        {
            if (_openBrowseClient is null) await _operationGate.WaitAsync(ct).ConfigureAwait(false);
            try { return await ReadDirectoryAsync(Client, path, ct).ConfigureAwait(false); }
            finally { _operationGate.Release(); }
        }

        await _browseGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _browseLifetime.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (_browseClient is not { IsConnected: true })
            {
                _browseClient?.Dispose();
                _browseClient = null;
                _browseClient = await _openBrowseClient(linked.Token).ConfigureAwait(false);
            }
            return await ReadDirectoryAsync(_browseClient, path, linked.Token).ConfigureAwait(false);
        }
        finally { _browseGate.Release(); }
    }

    private static async Task<IReadOnlyList<RemoteFileEntry>> ReadDirectoryAsync(
        SftpClient client, string path, CancellationToken ct)
    {
        var entries = new List<RemoteFileEntry>();
        await foreach (var file in client.ListDirectoryAsync(path, ct).ConfigureAwait(false))
        {
            if (file.Name is "." or "..") continue;
            DirectoryListingLimit.CheckNext(entries.Count);
            entries.Add(new RemoteFileEntry
            {
                Name = file.Name, FullPath = file.FullName, IsDirectory = file.IsDirectory,
                IsSymbolicLink = file.IsSymbolicLink, IsRegularFile = file.IsRegularFile,
                Size = file.Length, Modified = file.LastWriteTimeUtc,
            });
        }
        return entries.OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
    }

    public Task UploadFileAsync(string localPath, string remoteDirectory, bool overwrite = false,
        IProgress<double>? progress = null, CancellationToken ct = default) => SerializedAsync(() =>
    {
        var client = Client;
        var remotePath = RemotePath.Combine(remoteDirectory, Path.GetFileName(localPath));
        var temporaryPath = remotePath + $".sutty-{Guid.NewGuid():N}.part";

        var existing = client.Exists(remotePath) ? client.GetAttributes(remotePath) : null;
        if (existing is not null)
        {
            if (existing.IsDirectory)
                throw new IOException($"A remote directory already exists: {remotePath}");
            if (!overwrite)
                throw new IOException($"Remote file already exists: {remotePath}");
        }

        progress?.Report(0.0);
        try
        {
            DateTime sourceTime;
            using (var local = File.OpenRead(localPath))
            using (var remote = client.Open(temporaryPath, FileMode.CreateNew, FileAccess.Write))
            {
                SftpFileMetadata.ProtectStagedUpload(client, temporaryPath, existing);
                sourceTime = File.GetLastWriteTimeUtc(local.SafeFileHandle);
                var total = local.Length;
                var buffer = new byte[81920];
                int read;
                while ((read = local.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    remote.Write(buffer, 0, read);
                    progress?.Report(total == 0 ? 1.0 : (double)local.Position / total);
                }
            }

            PromoteRemoteFile(client, temporaryPath, remotePath, overwrite, sourceTime, ct);
            progress?.Report(1.0);
        }
        catch
        {
            DeleteRemoteIfExists(client, temporaryPath);
            throw;
        }
    }, ct);

    public Task DownloadFileAsync(string remotePath, string localPath, bool overwrite = false,
        IProgress<double>? progress = null, CancellationToken ct = default) => SerializedAsync(() =>
    {
        if (!overwrite && File.Exists(localPath))
            throw new IOException($"Local file already exists: {localPath}");

        progress?.Report(0.0);
        var temporaryPath = localPath + $".sutty-{Guid.NewGuid():N}.part";
        try
        {
            var sourceTime = Client.GetAttributes(remotePath).LastWriteTimeUtc;
            using (var remote = Client.OpenRead(remotePath))
            using (var local = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var total = remote.Length;
                var transferred = 0L;
                var buffer = new byte[81920];
                int read;
                while ((read = remote.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    local.Write(buffer, 0, read);
                    transferred += read;
                    progress?.Report(total == 0 ? 1.0 : (double)transferred / total);
                }
                local.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            SftpFileMetadata.SetLocalWriteTime(temporaryPath, sourceTime);
            ct.ThrowIfCancellationRequested();
            File.Move(temporaryPath, localPath, overwrite);
            progress?.Report(1.0);
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            throw;
        }
    }, ct);

    public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
        => SerializedAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            var client = Client;
            var source = NormalizeRemoteMovePath(sourcePath);
            var destination = NormalizeRemoteMovePath(destinationPath);
            if (source == "/")
                throw new IOException("The remote root cannot be moved.");
            if (source == destination)
                throw new IOException("The source and destination paths are the same.");
            if (!client.Exists(source))
                throw new FileNotFoundException($"Remote path does not exist: {source}", source);
            if (client.Exists(destination))
                throw new IOException($"Remote path already exists: {destination}");

            var destinationParent = RemotePath.GetDirectory(destination);
            if (!client.Exists(destinationParent) ||
                !client.GetAttributes(destinationParent).IsDirectory)
            {
                throw new DirectoryNotFoundException(
                    $"Remote destination directory does not exist: {destinationParent}");
            }
            if (IsDirectoryNotLink(client, source) &&
                destination.StartsWith(source + "/", StringComparison.Ordinal))
            {
                throw new IOException("A remote directory cannot be moved inside itself.");
            }

            client.RenameFile(source, destination);
        }, ct);

    private static string NormalizeRemoteMovePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = RemotePath.Normalize(path);
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
            ValidateRemotePathSegment(segment);
        return normalized;
    }

    public Task DeleteFileAsync(string path, CancellationToken ct = default)
        => SerializedAsync(() => { ct.ThrowIfCancellationRequested(); Client.DeleteFile(path); }, ct);

    public Task DeleteDirectoryAsync(string path, CancellationToken ct = default)
        => SerializedAsync(() => { ct.ThrowIfCancellationRequested(); Client.DeleteDirectory(path); }, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        => SerializedAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            var client = Client;
            if (client.Exists(path))
                throw new IOException($"Remote path already exists: {path}");
            client.CreateDirectory(path);
        }, ct);

    /// <summary>
    /// Waits for the current SFTP operation to leave the shared client, then runs the
    /// supplied transport shutdown while holding the same exclusivity gate. Operations
    /// queued behind shutdown will observe the cleared/disconnected client afterwards.
    /// </summary>
    public async Task ShutdownAsync(Action shutdown)
    {
        ArgumentNullException.ThrowIfNull(shutdown);
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _browseLifetime.CancelAsync().ConfigureAwait(false);
            await _browseGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    try { _browseClient?.Dispose(); }
                    finally { _browseClient = null; shutdown(); }
                }).ConfigureAwait(false);
            }
            finally
            {
                _browseLifetime.Dispose();
                _browseLifetime = new();
                _browseGate.Release();
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task SerializedAsync(Action operation, CancellationToken ct)
    {
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(operation, ct).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<T> SerializedAsync<T>(Func<T> operation, CancellationToken ct)
    {
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(operation, ct).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }
}
