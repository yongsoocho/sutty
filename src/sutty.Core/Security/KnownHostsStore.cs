using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace sutty.Core.Security;

/// <summary>
/// Thread-safe JSON known-host store. The default file is
/// %LOCALAPPDATA%\sutty\known-hosts.json and contains public host keys only.
/// </summary>
public sealed class KnownHostsStore : IKnownHostsStore
{
    private const int CurrentVersion = 2;
    private const int PreviousVersion = 1;
    private const int MaximumEntries = 4096;
    private const int MaximumActivityEntries = 2048;
    private const long MaximumFileBytes = 4L * 1024 * 1024;
    private static readonly TimeSpan LastUsedWriteInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StoreLockTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly string _storeLockPath;
    private Dictionary<string, KnownHostRecord>? _records;
    private List<KnownHostActivityRecord>? _activity;

    public KnownHostsStore(string? storePath = null)
    {
        StorePath = Path.GetFullPath(storePath ?? DefaultStorePath);
        _storeLockPath = StorePath + ".lock";
    }

    public static KnownHostsStore Default { get; } = new();

    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "sutty",
        "known-hosts.json");

    public string StorePath { get; }

    public KnownHostRecord? Find(HostEndpointIdentity endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            ReloadFromDisk();
            return _records!.TryGetValue(endpoint.Value, out var record) ? record : null;
        }
    }

    public KnownHostRecord Trust(HostEndpointIdentity endpoint, HostKeyData key)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            ReloadFromDisk();

            if (_records!.TryGetValue(endpoint.Value, out var existing))
            {
                if (existing.Key.Equals(key))
                    return existing;
                throw new HostKeyChangedException(endpoint, existing.Key, key);
            }

            if (_records.Count >= MaximumEntries)
                throw new InvalidOperationException($"Known-host store cannot exceed {MaximumEntries} entries.");

            var now = DateTimeOffset.UtcNow;
            var record = new KnownHostRecord(endpoint, key.Clone(), now, now);
            var next = new Dictionary<string, KnownHostRecord>(_records, StringComparer.Ordinal)
            {
                [endpoint.Value] = record,
            };
            var nextActivity = AppendActivity(
                _activity!,
                new KnownHostActivityRecord(
                    now,
                    KnownHostActivityType.Trusted,
                    endpoint,
                    null,
                    null,
                    key.Algorithm,
                    key.Sha256Fingerprint,
                    "Initial explicit trust"));

            SaveSnapshot(next.Values, nextActivity);
            _records = next;
            _activity = nextActivity;
            return record;
        }
    }

    public KnownHostRecord MarkUsed(
        HostEndpointIdentity endpoint,
        HostKeyData key,
        DateTimeOffset? usedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            ReloadFromDisk();
            if (!_records!.TryGetValue(endpoint.Value, out var existing))
                throw new KeyNotFoundException($"No persisted host key exists for {endpoint.Value}.");
            if (!existing.Key.Equals(key))
                throw new HostKeyChangedException(endpoint, existing.Key, key);

            var usedAt = (usedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
            if (usedAt == default)
                throw new ArgumentOutOfRangeException(nameof(usedAtUtc));
            if (usedAt <= existing.LastUsedAtUtc ||
                usedAt - existing.LastUsedAtUtc < LastUsedWriteInterval)
            {
                return existing;
            }

            var updated = existing with { LastUsedAtUtc = usedAt };
            var next = new Dictionary<string, KnownHostRecord>(_records, StringComparer.Ordinal)
            {
                [endpoint.Value] = updated,
            };
            SaveSnapshot(next.Values, _activity!);
            _records = next;
            return updated;
        }
    }

    public KnownHostRecord Rotate(
        HostEndpointIdentity endpoint,
        HostKeyData expectedTrustedKey,
        HostKeyData replacementKey,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(expectedTrustedKey);
        ArgumentNullException.ThrowIfNull(replacementKey);
        var normalizedReason = HostKeyRotationReason.Normalize(reason);

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            ReloadFromDisk();
            if (!_records!.TryGetValue(endpoint.Value, out var existing))
                throw new KeyNotFoundException($"No persisted host key exists for {endpoint.Value}.");
            if (!existing.Key.Equals(expectedTrustedKey))
                throw new HostKeyChangedException(endpoint, existing.Key, expectedTrustedKey);
            if (existing.Key.Equals(replacementKey))
                throw new InvalidOperationException("The replacement host key is already trusted.");

            var now = DateTimeOffset.UtcNow;
            var updated = new KnownHostRecord(
                endpoint,
                replacementKey.Clone(),
                existing.TrustedAtUtc,
                existing.LastUsedAtUtc);
            var next = new Dictionary<string, KnownHostRecord>(_records, StringComparer.Ordinal)
            {
                [endpoint.Value] = updated,
            };
            var nextActivity = AppendActivity(
                _activity!,
                new KnownHostActivityRecord(
                    now,
                    KnownHostActivityType.Rotated,
                    endpoint,
                    existing.Key.Algorithm,
                    existing.Key.Sha256Fingerprint,
                    replacementKey.Algorithm,
                    replacementKey.Sha256Fingerprint,
                    normalizedReason));

            SaveSnapshot(next.Values, nextActivity);
            _records = next;
            _activity = nextActivity;
            return updated;
        }
    }

    public bool Remove(HostEndpointIdentity endpoint, HostKeyData expectedTrustedKey)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(expectedTrustedKey);

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            ReloadFromDisk();
            if (!_records!.TryGetValue(endpoint.Value, out var existing))
                return false;
            if (!existing.Key.Equals(expectedTrustedKey))
                throw new HostKeyChangedException(endpoint, existing.Key, expectedTrustedKey);

            var next = new Dictionary<string, KnownHostRecord>(_records, StringComparer.Ordinal);
            next.Remove(endpoint.Value);
            var nextActivity = AppendActivity(
                _activity!,
                new KnownHostActivityRecord(
                    DateTimeOffset.UtcNow,
                    KnownHostActivityType.Removed,
                    endpoint,
                    existing.Key.Algorithm,
                    existing.Key.Sha256Fingerprint,
                    null,
                    null,
                    "User removed trust"));

            SaveSnapshot(next.Values, nextActivity);
            _records = next;
            _activity = nextActivity;
            return true;
        }
    }

    public KnownHostsSnapshot GetSnapshot(int activityLimit = 100)
    {
        if (activityLimit is < 1 or > MaximumActivityEntries)
            throw new ArgumentOutOfRangeException(nameof(activityLimit));

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            ReloadFromDisk();
            var hosts = _records!.Values
                .OrderBy(record => record.Endpoint.Value, StringComparer.Ordinal)
                .ToArray();
            var activity = _activity!
                .TakeLast(activityLimit)
                .Reverse()
                .ToArray();
            return new KnownHostsSnapshot(hosts, activity);
        }
    }

    public IReadOnlyList<KnownHostRecord> GetAll() => GetSnapshot().Hosts;

    public IReadOnlyList<KnownHostActivityRecord> GetActivity(int limit = 100) =>
        GetSnapshot(limit).Activity;

    private void EnsureLoaded()
    {
        if (_records is not null)
            return;

        var loaded = new Dictionary<string, KnownHostRecord>(StringComparer.Ordinal);
        if (!File.Exists(StorePath))
        {
            _records = loaded;
            _activity = [];
            return;
        }

        using var stream = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes)
            throw new InvalidDataException("Known-host store exceeds the maximum supported size.");

        KnownHostsDocument document;
        try
        {
            document = JsonSerializer.Deserialize(
                stream,
                SecurityJsonContext.Default.KnownHostsDocument)
                ?? throw new InvalidDataException("Known-host store is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Known-host store contains invalid JSON.", ex);
        }

        if (document.Version is not CurrentVersion and not PreviousVersion)
            throw new InvalidDataException($"Unsupported known-host store version: {document.Version}.");
        if (document.Hosts is null || document.Hosts.Count > MaximumEntries)
            throw new InvalidDataException("Known-host store contains an invalid number of entries.");
        if (document.Version == CurrentVersion && document.Activity is null)
            throw new InvalidDataException("Known-host v2 store is missing its activity collection.");
        if (document.Version == PreviousVersion && document.Activity is { Count: > 0 })
            throw new InvalidDataException("Known-host v1 store contains unsupported activity data.");
        if (document.Activity?.Count > MaximumActivityEntries)
            throw new InvalidDataException("Known-host store contains too many activity entries.");

        foreach (var item in document.Hosts)
        {
            try
            {
                if (item is null)
                    throw new InvalidDataException("Known-host store contains a null host entry.");
                var endpoint = HostEndpointIdentity.Parse(item.Identity);
                var rawKey = Convert.FromBase64String(item.RawKey);
                var key = HostKeyData.CreateVerified(item.Algorithm, rawKey, item.Sha256Fingerprint);
                var trustedAt = item.TrustedAtUtc.ToUniversalTime();
                if (trustedAt == default)
                    throw new InvalidDataException("Known-host trust time is missing.");
                var lastUsedAt = item.LastUsedAtUtc.ToUniversalTime();
                if (lastUsedAt == default)
                {
                    if (document.Version != PreviousVersion)
                        throw new InvalidDataException("Known-host last-used time is missing.");
                    lastUsedAt = trustedAt;
                }
                if (lastUsedAt < trustedAt)
                    throw new InvalidDataException("Known-host last-used time precedes its trust time.");

                if (!loaded.TryAdd(
                    endpoint.Value,
                    new KnownHostRecord(endpoint, key, trustedAt, lastUsedAt)))
                {
                    throw new InvalidDataException($"Known-host store contains duplicate endpoint {endpoint.Value}.");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException)
            {
                throw new InvalidDataException("Known-host store contains an invalid entry.", ex);
            }
        }

        var loadedActivity = new List<KnownHostActivityRecord>();
        foreach (var item in document.Activity ?? [])
        {
            try
            {
                if (item is null)
                    throw new InvalidDataException("Known-host store contains a null activity entry.");
                var timestamp = item.TimestampUtc.ToUniversalTime();
                if (timestamp == default ||
                    !Enum.TryParse<KnownHostActivityType>(item.Type, ignoreCase: false, out var type) ||
                    !Enum.IsDefined(type))
                {
                    throw new InvalidDataException("Known-host activity is invalid.");
                }

                var previousAlgorithm = NormalizeOptionalAlgorithm(item.PreviousAlgorithm);
                var previousFingerprint = NormalizeOptionalFingerprint(item.PreviousSha256Fingerprint);
                var currentAlgorithm = NormalizeOptionalAlgorithm(item.CurrentAlgorithm);
                var currentFingerprint = NormalizeOptionalFingerprint(item.CurrentSha256Fingerprint);
                ValidateActivityShape(
                    type,
                    previousAlgorithm,
                    previousFingerprint,
                    currentAlgorithm,
                    currentFingerprint);

                loadedActivity.Add(new KnownHostActivityRecord(
                    timestamp,
                    type,
                    HostEndpointIdentity.Parse(item.Identity),
                    previousAlgorithm,
                    previousFingerprint,
                    currentAlgorithm,
                    currentFingerprint,
                    HostKeyRotationReason.Normalize(item.Reason)));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                throw new InvalidDataException("Known-host store contains an invalid activity entry.", ex);
            }
        }

        _records = loaded;
        _activity = loadedActivity;
    }

    private void ReloadFromDisk()
    {
        _records = null;
        _activity = null;
        EnsureLoaded();
    }

    private StoreFileLockLease AcquireStoreLock()
    {
        // A same-directory sharing lock is enforced by Windows across logon sessions.
        // Keeping the zero-byte file avoids a delete/recreate race and inherits the
        // data directory's ACL, without exposing a squattable Global\\ kernel object.
        var directory = Path.GetDirectoryName(_storeLockPath)
            ?? throw new InvalidOperationException("Known-host store path has no parent directory.");
        Directory.CreateDirectory(directory);

        var timer = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var stream = new FileStream(
                    _storeLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
                return new StoreFileLockLease(stream);
            }
            catch (IOException error) when (IsStoreLockContention(error))
            {
                if (timer.Elapsed >= StoreLockTimeout)
                {
                    throw new IOException(
                        "Timed out waiting for exclusive access to the known-host store.",
                        error);
                }

                Thread.Sleep(25);
            }
        }
    }

    private static bool IsStoreLockContention(IOException error)
    {
        // Windows sharing/lock violations are 32/33. EAGAIN (11) covers the
        // FileShare.None implementation used by .NET on Unix test hosts.
        var nativeCode = error.HResult & 0xffff;
        return nativeCode is 11 or 32 or 33;
    }

    private void SaveSnapshot(
        IEnumerable<KnownHostRecord> records,
        IReadOnlyList<KnownHostActivityRecord> activity)
    {
        var directory = Path.GetDirectoryName(StorePath)
            ?? throw new InvalidOperationException("Known-host store path has no parent directory.");
        Directory.CreateDirectory(directory);

        var document = new KnownHostsDocument
        {
            Version = CurrentVersion,
            Hosts = records
                .OrderBy(record => record.Endpoint.Value, StringComparer.Ordinal)
                .Select(record => new KnownHostDocumentEntry
                {
                    Identity = record.Endpoint.Value,
                    Algorithm = record.Key.Algorithm,
                    Sha256Fingerprint = record.Key.Sha256Fingerprint,
                    RawKey = record.Key.ToBase64(),
                    TrustedAtUtc = record.TrustedAtUtc.ToUniversalTime(),
                    LastUsedAtUtc = record.LastUsedAtUtc.ToUniversalTime(),
                })
                .ToList(),
            Activity = activity
                .TakeLast(MaximumActivityEntries)
                .Select(item => new KnownHostActivityDocumentEntry
                {
                    TimestampUtc = item.TimestampUtc.ToUniversalTime(),
                    Type = item.Type.ToString(),
                    Identity = item.Endpoint.Value,
                    PreviousAlgorithm = item.PreviousAlgorithm,
                    PreviousSha256Fingerprint = item.PreviousSha256Fingerprint,
                    CurrentAlgorithm = item.CurrentAlgorithm,
                    CurrentSha256Fingerprint = item.CurrentSha256Fingerprint,
                    Reason = item.Reason,
                })
                .ToList(),
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(
            document,
            SecurityJsonContext.Default.KnownHostsDocument);
        if (json.LongLength > MaximumFileBytes)
            throw new InvalidOperationException("Known-host store exceeds the maximum supported size.");

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(StorePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(StorePath))
                File.Replace(tempPath, StorePath, destinationBackupFileName: null);
            else
                File.Move(tempPath, StorePath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preserve the original persistence error. A unique public-data temp file is harmless.
        }
    }

    private static List<KnownHostActivityRecord> AppendActivity(
        IReadOnlyList<KnownHostActivityRecord> activity,
        KnownHostActivityRecord item)
    {
        var skip = Math.Max(0, activity.Count - MaximumActivityEntries + 1);
        return activity.Skip(skip).Append(item).ToList();
    }

    private static string? NormalizeOptionalPublicValue(string? value)
    {
        if (value is null)
            return null;
        var normalized = value.Trim();
        if (normalized.Length is 0 or > 256 || normalized.Any(char.IsControl))
            throw new FormatException("Known-host activity metadata is invalid.");
        return normalized;
    }

    private static string? NormalizeOptionalAlgorithm(string? value)
    {
        var normalized = NormalizeOptionalPublicValue(value);
        if (normalized is not null &&
            (normalized.Length > 128 || normalized.Any(char.IsWhiteSpace)))
        {
            throw new FormatException("Known-host activity algorithm is invalid.");
        }
        return normalized;
    }

    private static string? NormalizeOptionalFingerprint(string? value)
    {
        var normalized = NormalizeOptionalPublicValue(value);
        if (normalized is null)
            return null;
        if (!normalized.StartsWith("SHA256:", StringComparison.Ordinal) ||
            normalized.Length != 50)
        {
            throw new FormatException("Known-host activity fingerprint is invalid.");
        }

        var decoded = Convert.FromBase64String(normalized[7..] + "=");
        if (decoded.Length != 32)
            throw new FormatException("Known-host activity fingerprint is invalid.");
        return normalized;
    }

    private static void ValidateActivityShape(
        KnownHostActivityType type,
        string? previousAlgorithm,
        string? previousFingerprint,
        string? currentAlgorithm,
        string? currentFingerprint)
    {
        var hasPrevious = previousAlgorithm is not null && previousFingerprint is not null;
        var hasCurrent = currentAlgorithm is not null && currentFingerprint is not null;
        var hasPartialPair = (previousAlgorithm is null) != (previousFingerprint is null) ||
                             (currentAlgorithm is null) != (currentFingerprint is null);
        var valid = !hasPartialPair && (type switch
        {
            KnownHostActivityType.Trusted => !hasPrevious && hasCurrent,
            KnownHostActivityType.Rotated => hasPrevious && hasCurrent,
            KnownHostActivityType.Removed => hasPrevious && !hasCurrent,
            _ => false,
        });
        if (!valid)
            throw new FormatException("Known-host activity key metadata is inconsistent.");
    }

    private sealed class StoreFileLockLease(FileStream stream) : IDisposable
    {
        private FileStream? _stream = stream;

        public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
    }

}
