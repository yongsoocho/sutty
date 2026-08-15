using Renci.SshNet;
using sutty.Core.Models;

namespace sutty.Core.Sftp;

/// <summary>
/// SSH.NET SftpClient wrapper. SSH.NET does not promise that one SftpClient can be
/// called concurrently, so every list/transfer/mutation is serialized by one gate.
/// </summary>
public sealed partial class SshNetSftpService : ISftpService
{
    private readonly Func<SftpClient?> _clientProvider;
    private readonly Func<CancellationToken, Task<SftpClient?>>? _reconnectAsync;
    private readonly SftpTransferCheckpointStore _checkpointStore;
    private readonly string _checkpointScope;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public SshNetSftpService(
        Func<SftpClient?> clientProvider,
        string checkpointScope = "default",
        Func<CancellationToken, Task<SftpClient?>>? reconnectAsync = null,
        SftpTransferCheckpointStore? checkpointStore = null)
    {
        _clientProvider = clientProvider;
        _checkpointScope = string.IsNullOrWhiteSpace(checkpointScope) ? "default" : checkpointScope;
        _reconnectAsync = reconnectAsync;
        _checkpointStore = checkpointStore ?? SftpTransferCheckpointStore.Default;
    }

    private SftpClient Client =>
        _clientProvider() is { IsConnected: true } client
            ? client
            : throw new InvalidOperationException("SFTP 채널이 연결되어 있지 않습니다.");

    public Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(
        string path, CancellationToken ct = default) => SerializedAsync<IReadOnlyList<RemoteFileEntry>>(() =>
    {
        ct.ThrowIfCancellationRequested();
        return Client.ListDirectory(path)
            .Where(f => f.Name is not "." and not "..")
            .Select(f => new RemoteFileEntry
            {
                Name = f.Name,
                FullPath = f.FullName,
                IsDirectory = f.IsDirectory,
                IsSymbolicLink = f.IsSymbolicLink,
                IsRegularFile = f.IsRegularFile,
                Size = f.Length,
                Modified = f.LastWriteTime,
            })
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }, ct);

    public Task UploadFileAsync(string localPath, string remoteDirectory, bool overwrite = false,
        IProgress<double>? progress = null, CancellationToken ct = default) => SerializedAsync(() =>
    {
        var client = Client;
        var remotePath = RemotePath.Combine(remoteDirectory, Path.GetFileName(localPath));
        var temporaryPath = remotePath + $".sutty-{Guid.NewGuid():N}.part";
        string? backupPath = null;

        if (client.Exists(remotePath))
        {
            if (client.GetAttributes(remotePath).IsDirectory)
                throw new IOException($"A remote directory already exists: {remotePath}");
            if (!overwrite)
                throw new IOException($"Remote file already exists: {remotePath}");
        }

        progress?.Report(0.0);
        try
        {
            using (var local = File.OpenRead(localPath))
            using (var remote = client.Open(temporaryPath, FileMode.CreateNew, FileAccess.Write))
            {
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

            ct.ThrowIfCancellationRequested();
            if (client.Exists(remotePath))
            {
                if (client.GetAttributes(remotePath).IsDirectory)
                    throw new IOException($"A remote directory already exists: {remotePath}");
                if (!overwrite)
                    throw new IOException($"Remote file already exists: {remotePath}");

                // Preserve the previous remote file until the completed temp file has
                // successfully taken its name. A failed promote rolls the backup back.
                backupPath = remotePath + $".sutty-{Guid.NewGuid():N}.backup";
                client.RenameFile(remotePath, backupPath);
            }

            try
            {
                client.RenameFile(temporaryPath, remotePath);
            }
            catch
            {
                if (backupPath is not null && client.Exists(backupPath) && !client.Exists(remotePath))
                    client.RenameFile(backupPath, remotePath);
                throw;
            }

            if (backupPath is not null)
            {
                try { if (client.Exists(backupPath)) client.DeleteFile(backupPath); } catch { }
            }
            progress?.Report(1.0);
        }
        catch
        {
            try { if (client.Exists(temporaryPath)) client.DeleteFile(temporaryPath); } catch { }
            if (backupPath is not null)
            {
                try
                {
                    if (client.Exists(backupPath) && !client.Exists(remotePath))
                        client.RenameFile(backupPath, remotePath);
                }
                catch { }
            }
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
            if (client.Exists(destinationPath))
                throw new IOException($"Remote path already exists: {destinationPath}");
            client.RenameFile(sourcePath, destinationPath);
        }, ct);

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
            await Task.Run(shutdown).ConfigureAwait(false);
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
