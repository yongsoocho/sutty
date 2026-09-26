namespace sutty.Core.Sftp;

public sealed partial class SshNetSftpService
{
    public Task<byte[]> ReadFileBytesAsync(string remotePath, int maximumBytes, CancellationToken ct = default) =>
        SerializedAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            var client = Client;
            var entry = client.Get(remotePath);
            if (!entry.IsRegularFile || entry.IsDirectory || entry.IsSymbolicLink)
                throw new IOException("Only regular files can be verified for editing.");
            using var remote = client.OpenRead(remotePath);
            return SftpBoundedRead.Read(remote, maximumBytes, ct);
        }, ct);
}
