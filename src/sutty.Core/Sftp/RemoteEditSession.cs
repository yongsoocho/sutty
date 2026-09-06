using System.Security.Cryptography;
using System.Text;
using sutty.Core.Models;

namespace sutty.Core.Sftp;

/// <summary>A local working copy pinned to one server and one absolute remote file.</summary>
public sealed class RemoteEditSession
{
    public const long MaximumBytes = 8 * 1024 * 1024;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string HostIdentity { get; }
    public string RemoteFilePath { get; private set; }
    public string LocalFilePath { get; private set; } = "";
    public string WorkingDirectory { get; }
    public RemoteEditStamp? Baseline { get; private set; }
    public string? UploadedHash { get; private set; }
    public bool NeedsReview { get; private set; }
    public string? RecoveryNoteError { get; private set; }

    public RemoteEditSession(string hostIdentity, string remotePath, string? storageRoot = null)
    {
        if (string.IsNullOrWhiteSpace(hostIdentity) || hostIdentity.Any(char.IsControl))
            throw new ArgumentException("A server identity is required.", nameof(hostIdentity));
        if (string.IsNullOrEmpty(remotePath) || !remotePath.StartsWith('/') || remotePath.Any(char.IsControl) || RemotePath.Normalize(remotePath) == "/")
            throw new ArgumentException("An absolute remote file path is required.", nameof(remotePath));
        HostIdentity = hostIdentity;
        RemoteFilePath = RemotePath.Normalize(remotePath);
        WorkingDirectory = Path.Combine(Path.GetFullPath(storageRoot ?? DefaultStorageRoot), Id);
    }

    public static string DefaultStorageRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sutty", "edits");

    public string AllocateWorkingCopy()
    {
        Directory.CreateDirectory(WorkingDirectory);
        var name = RemotePath.GetName(RemoteFilePath);
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Length > 100 || IsReservedName(name))
            name = "remote-file.txt";
        LocalFilePath = Path.Combine(WorkingDirectory, $"{Guid.NewGuid():N}-{name}");
        UploadedHash = null;
        Baseline = null;
        NeedsReview = true;
        return LocalFilePath;
    }

    /// <summary>Initialize only after a successful, verified queue download and a second stat.</summary>
    public async Task AcceptDownloadAsync(RemoteEditStamp before, RemoteEditStamp? after, CancellationToken ct)
    {
        var bytes = await ReadStableTextAsync(LocalFilePath, ct);
        UploadedHash = Hash(bytes);
        Baseline = after;
        NeedsReview = !before.Matches(after) || before.Size != bytes.LongLength;
        await WriteRecoveryNoteAsync(ct);
    }

    public async Task<string> ReadLocalHashAsync(CancellationToken ct = default) =>
        Hash(await ReadStableTextAsync(LocalFilePath, ct));

    public async Task<bool> HasChangesAsync(CancellationToken ct = default)
    {
        if (UploadedHash is null) return true;
        try { return !string.Equals(UploadedHash, await ReadLocalHashAsync(ct), StringComparison.Ordinal); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>An immutable upload source prevents editor saves from changing an in-flight upload.</summary>
    public async Task<RemoteEditUpload> CreateUploadAsync(CancellationToken ct = default)
    {
        var bytes = await ReadStableTextAsync(LocalFilePath, ct);
        var path = Path.Combine(WorkingDirectory, $"upload-{Guid.NewGuid():N}.txt");
        await File.WriteAllBytesAsync(path, bytes, ct);
        return new RemoteEditUpload(path, Hash(bytes), bytes.LongLength);
    }

    public bool HasRemoteConflict(RemoteEditStamp? current) => NeedsReview || Baseline is null || !Baseline.Matches(current);

    public async Task AcceptUploadAsync(RemoteEditUpload upload, string destination, RemoteEditStamp? after, CancellationToken ct)
    {
        UploadedHash = upload.Sha256;
        RemoteFilePath = RemotePath.Normalize(destination);
        Baseline = after;
        NeedsReview = after is null || !after.CanCompare || after.Size != upload.Size;
        await WriteRecoveryNoteAsync(ct);
    }

    public void RequireReview() => NeedsReview = true;

    public static RemoteEditStamp ValidateEntry(RemoteFileEntry? entry)
    {
        if (entry is null || !entry.IsRegularFile || entry.IsDirectory || entry.IsSymbolicLink)
            throw new IOException("Choose a regular file; folders and symbolic links cannot be edited.");
        if (entry.Size is < 0 or > MaximumBytes)
            throw new IOException("Use Download for files larger than 8 MiB.");
        return new(entry.Size, entry.Modified);
    }

    public static async Task<RemoteEditStamp?> ReadRemoteStampAsync(ISftpService sftp, string remotePath, CancellationToken ct)
    {
        var entries = await sftp.ListDirectoryAsync(RemotePath.GetDirectory(remotePath), ct);
        var entry = entries.FirstOrDefault(item => string.Equals(item.FullPath, remotePath, StringComparison.Ordinal));
        return entry is null ? null : ValidateEntry(entry);
    }

    private async Task WriteRecoveryNoteAsync(CancellationToken ct)
    {
        // Human-readable recovery metadata deliberately excludes credentials and terminal history.
        var note = $"Sutty remote working copy / 원격 편집본\nServer / 서버: {HostIdentity}\nRemote / 원격: {RemoteFilePath}\nLocal / 로컬: {LocalFilePath}\n\nThis copy is kept locally. Review the current remote file before uploading after reconnect.\n이 파일은 로컬에 보관됩니다. 재연결 후 현재 원격 파일을 확인하고 업로드하세요.\n";
        try
        {
            await File.WriteAllTextAsync(Path.Combine(WorkingDirectory, "RECOVER.txt"), note, new UTF8Encoding(false), ct);
            RecoveryNoteError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A recovery-note failure cannot undo a verified download or remote promotion.
            // Expose it separately so the UI preserves the actual transfer outcome.
            RecoveryNoteError = error.GetType().Name;
        }
    }

    public static async Task<byte[]> ReadStableTextAsync(string path, CancellationToken ct)
    {
        // FileShare.Read blocks in-place writes/replacement during this short bounded snapshot.
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > MaximumBytes)
            throw new IOException("Use Download for files larger than 8 MiB.");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, ct);
        var utf16 = bytes.Length >= 2 && ((bytes[0] == 0xff && bytes[1] == 0xfe) || (bytes[0] == 0xfe && bytes[1] == 0xff));
        if (!utf16 && bytes.Contains((byte)0))
            throw new IOException("Binary files must be downloaded instead of opened for text editing.");
        return bytes;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool IsReservedName(string name)
    {
        var stem = name.Split('.')[0];
        return new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record RemoteEditStamp(long Size, DateTime? Modified)
{
    public bool CanCompare => Modified is not null;
    public bool Matches(RemoteEditStamp? other) => CanCompare && other is { CanCompare: true } && Size == other.Size && Modified == other.Modified;
}

public sealed record RemoteEditUpload(string LocalPath, string Sha256, long Size);
