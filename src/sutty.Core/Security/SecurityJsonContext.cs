using System.Text.Json.Serialization;

namespace sutty.Core.Security;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(KnownHostsDocument))]
[JsonSerializable(typeof(CredentialSecret))]
[JsonSerializable(typeof(CredentialVaultDocument))]
internal sealed partial class SecurityJsonContext : JsonSerializerContext;

internal sealed class KnownHostsDocument
{
    public int Version { get; set; }
    public List<KnownHostDocumentEntry>? Hosts { get; set; }
    public List<KnownHostActivityDocumentEntry>? Activity { get; set; }
}

internal sealed class KnownHostDocumentEntry
{
    public string Identity { get; set; } = "";
    public string Algorithm { get; set; } = "";
    public string Sha256Fingerprint { get; set; } = "";
    public string RawKey { get; set; } = "";
    public DateTimeOffset TrustedAtUtc { get; set; }
    public DateTimeOffset LastUsedAtUtc { get; set; }
}

internal sealed class KnownHostActivityDocumentEntry
{
    public DateTimeOffset TimestampUtc { get; set; }
    public string Type { get; set; } = "";
    public string Identity { get; set; } = "";
    public string? PreviousAlgorithm { get; set; }
    public string? PreviousSha256Fingerprint { get; set; }
    public string? CurrentAlgorithm { get; set; }
    public string? CurrentSha256Fingerprint { get; set; }
    public string Reason { get; set; } = "";
}

internal sealed class CredentialVaultDocument
{
    public int Version { get; set; }
    public List<CredentialVaultEntry> Entries { get; set; } = [];
}

internal sealed class CredentialVaultEntry
{
    public string Id { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Ciphertext { get; set; } = "";
    public string Tag { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
