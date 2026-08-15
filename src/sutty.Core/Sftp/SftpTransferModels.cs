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
/// Conflict behavior retained with a durable transfer job. <see cref="Ask"/> is a
/// UI-facing policy: callers must resolve it before starting an unattended transfer.
/// </summary>
public enum SftpConflictPolicy
{
    Ask,
    Overwrite,
    Skip,
    Rename,
    NewerOnly,
}

/// <summary>
/// Transfer behavior shared by single-session and multi-session SFTP operations.
/// MaxRetries is the number of retries after the initial attempt.
/// </summary>
public sealed record SftpTransferOptions
{
    public static SftpTransferOptions Default { get; } = new();

    public bool Overwrite { get; init; } = true;
    /// <summary>
    /// A durable per-job conflict policy. Null preserves the legacy <see cref="Overwrite"/>
    /// flag when reading queue files created by earlier Sutty versions.
    /// </summary>
    public SftpConflictPolicy? ConflictPolicy { get; init; }
    public bool Resume { get; init; } = true;
    public bool VerifyChecksum { get; init; } = true;
    public bool RetryEnabled { get; init; } = true;
    public int MaxRetries { get; init; } = 3;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public static SftpConflictPolicy ParseConflictPolicy(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "overwrite" => SftpConflictPolicy.Overwrite,
            "skip" => SftpConflictPolicy.Skip,
            "rename" => SftpConflictPolicy.Rename,
            "neweronly" => SftpConflictPolicy.NewerOnly,
            _ => SftpConflictPolicy.Ask,
        };

    internal SftpConflictPolicy EffectiveConflictPolicy =>
        ConflictPolicy ?? (Overwrite ? SftpConflictPolicy.Overwrite : SftpConflictPolicy.Ask);

    internal SftpTransferOptions Normalize()
    {
        SftpConflictPolicy? conflictPolicy = ConflictPolicy is { } policy && Enum.IsDefined(policy)
            ? policy
            : null;
        return this with
        {
            ConflictPolicy = conflictPolicy,
            MaxRetries = Math.Clamp(MaxRetries, 0, 10),
            InitialRetryDelay = TimeSpan.FromMilliseconds(Math.Clamp(
                InitialRetryDelay.TotalMilliseconds,
                100,
                30_000)),
        };
    }
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
    TimeSpan Duration,
    int FilesSkipped = 0);

/// <summary>
/// A bounded, non-destructive preview shown before a remote directory is removed.
/// Symbolic links are counted as leaf entries and are never traversed.
/// </summary>
public sealed record SftpDeletePreview(
    string RootPath,
    int FileCount,
    int DirectoryCount,
    long TotalBytes,
    IReadOnlyList<string> PreviewPaths);

/// <summary>One flattened entry produced while enumerating a remote folder tree.</summary>
public sealed record RemoteTreeEntry(
    Models.RemoteFileEntry Entry,
    string RelativePath,
    int Depth);
