namespace sutty.Core.Sftp;

/// <summary>Direction of a resumable SFTP transfer.</summary>
public enum SftpTransferDirection
{
    Upload,
    Download,
}

/// <summary>Current phase reported by the Core transfer engine.</summary>
public enum SftpTransferPhase
{
    Enumerating,
    Preparing,
    Transferring,
    Verifying,
    Retrying,
    Completed,
}

/// <summary>
/// Transfer behavior shared by single-session and multi-session SFTP operations.
/// MaxRetries is the number of retries after the initial attempt.
/// </summary>
public sealed record SftpTransferOptions
{
    public static SftpTransferOptions Default { get; } = new();

    public bool Overwrite { get; init; } = true;
    public bool Resume { get; init; } = true;
    public bool VerifyChecksum { get; init; } = true;
    public bool RetryEnabled { get; init; } = true;
    public int MaxRetries { get; init; } = 3;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    internal SftpTransferOptions Normalize() => this with
    {
        MaxRetries = Math.Clamp(MaxRetries, 0, 10),
        InitialRetryDelay = TimeSpan.FromMilliseconds(Math.Clamp(
            InitialRetryDelay.TotalMilliseconds,
            100,
            30_000)),
    };
}

/// <summary>Byte-accurate progress for one path, including recursive directory transfers.</summary>
public sealed record SftpTransferProgress(
    SftpTransferDirection Direction,
    SftpTransferPhase Phase,
    string RelativePath,
    long BytesTransferred,
    long TotalBytes,
    int FilesCompleted,
    int TotalFiles,
    int Attempt,
    string? Message = null)
{
    public double Fraction => TotalBytes <= 0
        ? Phase == SftpTransferPhase.Completed ? 1.0 : 0.0
        : Math.Clamp((double)BytesTransferred / TotalBytes, 0.0, 1.0);
}

/// <summary>Successful result of a file or recursive directory transfer.</summary>
public sealed record SftpTransferResult(
    SftpTransferDirection Direction,
    string SourcePath,
    string DestinationPath,
    int FilesTransferred,
    long BytesTransferred,
    long ResumedBytes,
    string? Sha256,
    TimeSpan Duration);

/// <summary>One flattened entry produced while enumerating a remote folder tree.</summary>
public sealed record RemoteTreeEntry(
    Models.RemoteFileEntry Entry,
    string RelativePath,
    int Depth);
