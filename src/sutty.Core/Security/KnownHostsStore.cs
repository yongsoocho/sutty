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
    private const int CurrentVersion = 1;
    private const int MaximumEntries = 4096;
    private const long MaximumFileBytes = 4L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly object _gate = new();
    private Dictionary<string, KnownHostRecord>? _records;

    public KnownHostsStore(string? storePath = null)
    {
        StorePath = Path.GetFullPath(storePath ?? DefaultStorePath);
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
            EnsureLoaded();
            return _records!.TryGetValue(endpoint.Value, out var record) ? record : null;
        }
    }

    public KnownHostRecord Trust(HostEndpointIdentity endpoint, HostKeyData key)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            EnsureLoaded();

            if (_records!.TryGetValue(endpoint.Value, out var existing))
            {
                if (existing.Key.Equals(key))
                    return existing;
                throw new HostKeyChangedException(endpoint, existing.Key, key);
            }

            if (_records.Count >= MaximumEntries)
                throw new InvalidOperationException($"Known-host store cannot exceed {MaximumEntries} entries.");

            var record = new KnownHostRecord(endpoint, key.Clone(), DateTimeOffset.UtcNow);
            var next = new Dictionary<string, KnownHostRecord>(_records, StringComparer.Ordinal)
            {
                [endpoint.Value] = record,
            };

            SaveSnapshot(next.Values);
            _records = next;
            return record;
        }
    }

    public IReadOnlyList<KnownHostRecord> GetAll()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _records!.Values
                .OrderBy(record => record.Endpoint.Value, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private void EnsureLoaded()
    {
        if (_records is not null)
            return;

        var loaded = new Dictionary<string, KnownHostRecord>(StringComparer.Ordinal);
        if (!File.Exists(StorePath))
        {
            _records = loaded;
            return;
        }

        using var stream = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes)
            throw new InvalidDataException("Known-host store exceeds the maximum supported size.");

        KnownHostsDocument document;
        try
        {
            document = JsonSerializer.Deserialize<KnownHostsDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("Known-host store is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Known-host store contains invalid JSON.", ex);
        }

        if (document.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported known-host store version: {document.Version}.");
        if (document.Hosts is null || document.Hosts.Count > MaximumEntries)
            throw new InvalidDataException("Known-host store contains an invalid number of entries.");

        foreach (var item in document.Hosts)
        {
            try
            {
                var endpoint = HostEndpointIdentity.Parse(item.Identity);
                var rawKey = Convert.FromBase64String(item.RawKey);
                var key = HostKeyData.CreateVerified(item.Algorithm, rawKey, item.Sha256Fingerprint);
                var trustedAt = item.TrustedAtUtc.ToUniversalTime();
                if (trustedAt == default)
                    throw new InvalidDataException("Known-host trust time is missing.");

                if (!loaded.TryAdd(endpoint.Value, new KnownHostRecord(endpoint, key, trustedAt)))
                    throw new InvalidDataException($"Known-host store contains duplicate endpoint {endpoint.Value}.");
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException)
            {
                throw new InvalidDataException("Known-host store contains an invalid entry.", ex);
            }
        }

        _records = loaded;
    }

    private void SaveSnapshot(IEnumerable<KnownHostRecord> records)
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
                })
                .ToList(),
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
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

    private sealed class KnownHostsDocument
    {
        public int Version { get; set; }
        public List<KnownHostDocumentEntry>? Hosts { get; set; }
    }

    private sealed class KnownHostDocumentEntry
    {
        public string Identity { get; set; } = "";
        public string Algorithm { get; set; } = "";
        public string Sha256Fingerprint { get; set; } = "";
        public string RawKey { get; set; } = "";
        public DateTimeOffset TrustedAtUtc { get; set; }
    }
}
