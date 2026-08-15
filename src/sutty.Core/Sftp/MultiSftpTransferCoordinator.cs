namespace sutty.Core.Sftp;

using System.Security.Cryptography;
using System.Text;

/// <summary>One connected SFTP target selected in the Multi screen.</summary>
public sealed record MultiSftpTarget(
    string Id,
    string DisplayName,
    ISftpService Service,
    string RemoteDirectory)
{
    /// <summary>Stable saved-host or endpoint identity used by the durable queue.</summary>
    public string PersistenceId { get; init; } = Id;
}

public enum MultiSftpTargetState
{
    Pending,
    Transferring,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Latest independently tracked state for one target server.</summary>
public sealed record MultiSftpTargetStatus(
    MultiSftpTarget Target,
    MultiSftpTargetState State,
    SftpTransferProgress? TransferProgress = null,
    SftpTransferResult? Result = null,
    string? Error = null)
{
    public double Fraction => State == MultiSftpTargetState.Succeeded
        ? 1.0
        : TransferProgress?.Fraction ?? 0.0;
}

/// <summary>Complete result of a multi-server upload. Failures are isolated per server.</summary>
public sealed record MultiSftpBatchResult(IReadOnlyList<MultiSftpTargetStatus> Targets)
{
    public IReadOnlyList<MultiSftpTargetStatus> Failed => Targets
        .Where(target => target.State == MultiSftpTargetState.Failed)
        .ToArray();

    public bool IsSuccessful => Targets.Count > 0 && Failed.Count == 0 &&
        Targets.All(target => target.State == MultiSftpTargetState.Succeeded);
}

/// <summary>
/// Fans one local file or folder out to multiple connected SFTP sessions while keeping
/// progress, completion, and failure independent for every server.
/// </summary>
public sealed class MultiSftpTransferCoordinator
{
    public const int MaximumTargets = 16;
    private readonly int _maximumParallelism;

    public MultiSftpTransferCoordinator(int maximumParallelism = 4)
    {
        _maximumParallelism = Math.Clamp(maximumParallelism, 1, MaximumTargets);
    }

    public Task<MultiSftpBatchResult> UploadAsync(
        string localPath,
        IReadOnlyCollection<MultiSftpTarget> targets,
        SftpTransferOptions? options = null,
        IProgress<MultiSftpTargetStatus>? progress = null,
        CancellationToken ct = default)
    {
        ValidateTargets(targets);
        return UploadCoreAsync(localPath, targets, options, progress, ct);
    }

    /// <summary>
    /// Downloads the same remote file or directory from many servers into one local
    /// aggregation directory. Every server receives a deterministic child directory,
    /// so equal remote names can never overwrite another server's result.
    /// </summary>
    public Task<MultiSftpBatchResult> DownloadAsync(
        string remotePath,
        string localDirectory,
        IReadOnlyCollection<MultiSftpTarget> sources,
        SftpTransferOptions? options = null,
        IProgress<MultiSftpTargetStatus>? progress = null,
        CancellationToken ct = default)
    {
        ValidateTargets(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localDirectory);
        if (!Path.IsPathFullyQualified(localDirectory))
            throw new ArgumentException("The local aggregation directory must be absolute.", nameof(localDirectory));

        return DownloadCoreAsync(RemotePath.Normalize(remotePath), localDirectory, sources,
            options, progress, ct);
    }

    /// <summary>Retries only the failed targets from a completed batch.</summary>
    public Task<MultiSftpBatchResult> RetryFailedAsync(
        string localPath,
        MultiSftpBatchResult previous,
        SftpTransferOptions? options = null,
        IProgress<MultiSftpTargetStatus>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var failedTargets = previous.Failed.Select(status => status.Target).ToArray();
        if (failedTargets.Length == 0)
            return Task.FromResult(new MultiSftpBatchResult([]));
        return UploadCoreAsync(localPath, failedTargets, options, progress, ct);
    }

    /// <summary>Retries only failed servers from a completed N-to-one download.</summary>
    public Task<MultiSftpBatchResult> RetryFailedDownloadAsync(
        string remotePath,
        string localDirectory,
        MultiSftpBatchResult previous,
        SftpTransferOptions? options = null,
        IProgress<MultiSftpTargetStatus>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var failedTargets = previous.Failed.Select(status => status.Target).ToArray();
        if (failedTargets.Length == 0)
            return Task.FromResult(new MultiSftpBatchResult([]));
        return DownloadAsync(remotePath, localDirectory, failedTargets, options, progress, ct);
    }

    private async Task<MultiSftpBatchResult> UploadCoreAsync(
        string localPath,
        IReadOnlyCollection<MultiSftpTarget> targets,
        SftpTransferOptions? options,
        IProgress<MultiSftpTargetStatus>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        if (!File.Exists(localPath) && !Directory.Exists(localPath))
            throw new FileNotFoundException($"Local upload path does not exist: {localPath}", localPath);

        var fileName = File.Exists(localPath)
            ? Path.GetFileName(localPath)
            : new DirectoryInfo(localPath).Name;
        var results = new MultiSftpTargetStatus[targets.Count];
        using var concurrency = new SemaphoreSlim(_maximumParallelism, _maximumParallelism);
        var indexedTargets = targets.Select((target, index) => (target, index)).ToArray();

        foreach (var (target, index) in indexedTargets)
        {
            results[index] = new MultiSftpTargetStatus(target, MultiSftpTargetState.Pending);
            progress?.Report(results[index]);
        }

        await Task.WhenAll(indexedTargets.Select(async indexed =>
        {
            var (target, index) = indexed;
            try
            {
                await concurrency.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                results[index] = new MultiSftpTargetStatus(target, MultiSftpTargetState.Cancelled);
                progress?.Report(results[index]);
                return;
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                results[index] = new MultiSftpTargetStatus(target, MultiSftpTargetState.Transferring);
                progress?.Report(results[index]);

                var targetProgress = new CallbackProgress<SftpTransferProgress>(item =>
                {
                    results[index] = new MultiSftpTargetStatus(
                        target,
                        MultiSftpTargetState.Transferring,
                        item);
                    progress?.Report(results[index]);
                });
                var destination = RemotePath.Combine(target.RemoteDirectory, fileName);
                var result = await target.Service.UploadPathAsync(
                    localPath,
                    destination,
                    options,
                    targetProgress,
                    ct).ConfigureAwait(false);
                results[index] = new MultiSftpTargetStatus(
                    target,
                    MultiSftpTargetState.Succeeded,
                    Result: result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                results[index] = new MultiSftpTargetStatus(target, MultiSftpTargetState.Cancelled);
            }
            catch (Exception error)
            {
                results[index] = new MultiSftpTargetStatus(
                    target,
                    MultiSftpTargetState.Failed,
                    Error: error.Message);
            }
            finally
            {
                concurrency.Release();
                progress?.Report(results[index]);
            }
        })).ConfigureAwait(false);

        return new MultiSftpBatchResult(results);
    }

    private async Task<MultiSftpBatchResult> DownloadCoreAsync(
        string remotePath,
        string localDirectory,
        IReadOnlyCollection<MultiSftpTarget> sources,
        SftpTransferOptions? options,
        IProgress<MultiSftpTargetStatus>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(localDirectory);
        var remoteName = RemotePath.GetName(remotePath);
        if (string.IsNullOrWhiteSpace(remoteName))
            remoteName = "root";

        var results = new MultiSftpTargetStatus[sources.Count];
        using var concurrency = new SemaphoreSlim(_maximumParallelism, _maximumParallelism);
        var indexedSources = sources.Select((target, index) => (target, index)).ToArray();

        foreach (var (target, index) in indexedSources)
        {
            results[index] = new MultiSftpTargetStatus(target, MultiSftpTargetState.Pending);
            progress?.Report(results[index]);
        }

        await Task.WhenAll(indexedSources.Select(async indexed =>
        {
            var (target, index) = indexed;
            try
            {
                await concurrency.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                results[index] = new MultiSftpTargetStatus(target, MultiSftpTargetState.Cancelled);
                progress?.Report(results[index]);
                return;
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                results[index] = new MultiSftpTargetStatus(target, MultiSftpTargetState.Transferring);
                progress?.Report(results[index]);

                var targetProgress = new CallbackProgress<SftpTransferProgress>(item =>
                {
                    results[index] = new MultiSftpTargetStatus(
                        target,
                        MultiSftpTargetState.Transferring,
                        item);
                    progress?.Report(results[index]);
                });
                var serverDirectory = Path.Combine(
                    localDirectory,
                    CreateServerDirectoryName(target));
                Directory.CreateDirectory(serverDirectory);
                var destination = Path.Combine(serverDirectory, MakeSafeLocalName(remoteName));
                var result = await target.Service.DownloadPathAsync(
                    remotePath,
                    destination,
                    options,
                    targetProgress,
                    ct).ConfigureAwait(false);
                results[index] = new MultiSftpTargetStatus(
                    target,
                    MultiSftpTargetState.Succeeded,
                    Result: result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                results[index] = new MultiSftpTargetStatus(target, MultiSftpTargetState.Cancelled);
            }
            catch (Exception error)
            {
                results[index] = new MultiSftpTargetStatus(
                    target,
                    MultiSftpTargetState.Failed,
                    Error: error.Message);
            }
            finally
            {
                concurrency.Release();
                progress?.Report(results[index]);
            }
        })).ConfigureAwait(false);

        return new MultiSftpBatchResult(results);
    }

    private static string CreateServerDirectoryName(MultiSftpTarget target)
    {
        var display = MakeSafeLocalName(target.DisplayName);
        var stableIdentity = string.IsNullOrWhiteSpace(target.PersistenceId)
            ? target.Id
            : target.PersistenceId;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableIdentity)))
            .ToLowerInvariant()[..10];
        return $"{display}-{digest}";
    }

    private static string MakeSafeLocalName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Trim().Select(character =>
            invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray())
            .TrimEnd(' ', '.');
        if (safe is "" or "." or "..")
            safe = "server";
        return safe.Length <= 96 ? safe : safe[..96];
    }

    private static void ValidateTargets(IReadOnlyCollection<MultiSftpTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
            throw new ArgumentException("At least one SFTP target is required.", nameof(targets));
        if (targets.Count > MaximumTargets)
            throw new ArgumentOutOfRangeException(
                nameof(targets),
                $"At most {MaximumTargets} SFTP targets are supported.");
        if (targets.Any(target => target.Service is null))
            throw new ArgumentException("Every SFTP target must have a service.", nameof(targets));
        if (targets.GroupBy(target => target.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("SFTP target IDs must be unique.", nameof(targets));
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
