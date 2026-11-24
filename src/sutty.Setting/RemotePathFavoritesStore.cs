using System.Text.Json;
using System.Text.Json.Serialization;

namespace sutty.Setting;

/// <summary>Local folder bookmarks and per-host remote bookmarks for the Files workspace.</summary>
public sealed class RemotePathFavoritesStore(string? path = null)
{
    private static readonly object Gate = new();
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sutty", "remote-path-favorites.json");

    public IReadOnlyList<string> GetPaths(string hostIdentity)
    {
        ValidateIdentity(hostIdentity);
        lock (Gate)
        {
            var document = Read();
            return document.Hosts.TryGetValue(hostIdentity, out var paths) ? paths.ToArray() : [];
        }
    }

    public void SetFavorite(string hostIdentity, string remotePath, bool favorite)
    {
        ValidateIdentity(hostIdentity);
        ValidatePath(remotePath);
        lock (Gate)
        {
            using var writer = LockDocument();
            // Read errors propagate: a damaged document must never be overwritten by defaults.
            var document = Read();
            var paths = document.Hosts.TryGetValue(hostIdentity, out var existing) ? existing : [];
            if (favorite && !paths.Contains(remotePath, StringComparer.Ordinal))
            {
                if (paths.Count >= 100) throw new InvalidOperationException("Each host can have up to 100 favorite folders.");
                paths.Add(remotePath);
                paths.Sort(StringComparer.Ordinal);
            }
            else if (!favorite) paths.Remove(remotePath);
            if (paths.Count == 0) document.Hosts.Remove(hostIdentity);
            else document.Hosts[hostIdentity] = paths;
            if (document.Hosts.Count > 2_000) throw new InvalidOperationException("The favorite host limit has been reached.");
            Write(document);
        }
    }

    public IReadOnlyList<string> GetLocalPaths()
    {
        lock (Gate) return Read().LocalFolders.ToArray();
    }

    public void SetLocalFavorite(string path, bool favorite)
    {
        path = NormalizeLocalPath(path);
        lock (Gate)
        {
            using var writer = LockDocument();
            var document = Read();
            var paths = document.LocalFolders;
            var index = paths.FindIndex(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
            if (favorite && index < 0)
            {
                if (paths.Count >= 100) throw new InvalidOperationException("Up to 100 local folders can be saved.");
                paths.Add(path);
                paths.Sort(StringComparer.OrdinalIgnoreCase);
            }
            else if (!favorite && index >= 0) paths.RemoveAt(index);
            else return;
            Write(document);
        }
    }

    private FileStream LockDocument()
    {
        var fullPath = Path.GetFullPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        // A competing window can retry. Never replace a newer snapshot with an older one.
        return new FileStream(fullPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static string NormalizeLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || path.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("An absolute local folder path is required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private RemotePathFavoritesDocument Read()
    {
        if (!File.Exists(_path)) return new();
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("The favorites file is too large.");
        var document = JsonSerializer.Deserialize(stream, RemotePathFavoritesJsonContext.Default.RemotePathFavoritesDocument)
            ?? throw new InvalidDataException("The favorites file is empty.");
        if (document.SchemaVersion != 1 || document.Hosts is null || document.Hosts.Count > 2_000)
            throw new InvalidDataException("The favorites file has an unsupported format.");
        if (document.LocalFolders is null || document.LocalFolders.Count > 100) throw new InvalidDataException("Invalid local favorites.");
        foreach (var folder in document.LocalFolders) NormalizeLocalPath(folder);
        foreach (var pair in document.Hosts)
        {
            ValidateIdentity(pair.Key);
            if (pair.Value is null || pair.Value.Count > 100) throw new InvalidDataException("Invalid favorite folders.");
            foreach (var folder in pair.Value) ValidatePath(folder);
        }
        return document;
    }

    private void Write(RemotePathFavoritesDocument document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document,
            RemotePathFavoritesJsonContext.Default.RemotePathFavoritesDocument);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("The favorites file size limit has been reached.");
        var fullPath = Path.GetFullPath(_path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".favorites-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ValidateIdentity(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl))
            throw new InvalidDataException("Invalid favorite host identity.");
    }

    private static void ValidatePath(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith('/') || value.Length > 4096 || value.Any(char.IsControl))
            throw new InvalidDataException("A favorite folder must be an absolute remote path.");
    }
}

internal sealed class RemotePathFavoritesDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> LocalFolders { get; set; } = [];
    public Dictionary<string, List<string>> Hosts { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RemotePathFavoritesDocument))]
internal partial class RemotePathFavoritesJsonContext : JsonSerializerContext;
