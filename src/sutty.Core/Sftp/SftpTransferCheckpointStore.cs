using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sutty.Core.Sftp;

/// <summary>
/// Non-secret restart checkpoints for resumable transfers. The document is replaced
/// atomically and contains paths, sizes and offsets only; credentials are never stored.
/// </summary>
public sealed class SftpTransferCheckpointStore
{
    private readonly object _gate = new();
    private readonly string _path;

    public static SftpTransferCheckpointStore Default { get; } = new();

    public SftpTransferCheckpointStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "sutty",
            "sftp-transfer-checkpoints.json");
    }

    public SftpTransferCheckpoint? Load(string id)
    {
        lock (_gate)
            return ReadDocument().Items.FirstOrDefault(item => item.Id == id);
    }

    public void Save(SftpTransferCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        lock (_gate)
        {
            var document = ReadDocument();
            document.Items.RemoveAll(item => item.Id == checkpoint.Id);
            document.Items.Add(checkpoint with { UpdatedAtUtc = DateTimeOffset.UtcNow });
            WriteDocument(document);
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            var document = ReadDocument();
            if (document.Items.RemoveAll(item => item.Id == id) == 0)
                return;
            WriteDocument(document);
        }
    }

    public static string CreateId(
        string scope,
        SftpTransferDirection direction,
        string sourcePath,
        string destinationPath)
    {
        var material = string.Join('\n', scope, direction, sourcePath, destinationPath);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private SftpTransferCheckpointDocument ReadDocument()
    {
        try
        {
            if (!File.Exists(_path))
                return new SftpTransferCheckpointDocument();
            return JsonSerializer.Deserialize(
                       File.ReadAllText(_path),
                       SftpCheckpointJsonContext.Default.SftpTransferCheckpointDocument)
                   ?? new SftpTransferCheckpointDocument();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // A corrupt or inaccessible checkpoint must never block a fresh transfer.
            return new SftpTransferCheckpointDocument();
        }
    }

    private void WriteDocument(SftpTransferCheckpointDocument document)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(
                document,
                SftpCheckpointJsonContext.Default.SftpTransferCheckpointDocument);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
                File.Replace(temporaryPath, _path, null);
            else
                File.Move(temporaryPath, _path);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }
}

public sealed record SftpTransferCheckpoint
{
    public string Id { get; init; } = "";
    public string Scope { get; init; } = "";
    public SftpTransferDirection Direction { get; init; }
    public string SourcePath { get; init; } = "";
    public string DestinationPath { get; init; } = "";
    public string PartialPath { get; init; } = "";
    public long TotalBytes { get; init; }
    public long TransferredBytes { get; init; }
    public long SourceLastWriteUtcTicks { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class SftpTransferCheckpointDocument
{
    public List<SftpTransferCheckpoint> Items { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SftpTransferCheckpointDocument))]
internal sealed partial class SftpCheckpointJsonContext : JsonSerializerContext;
