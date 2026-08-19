namespace sutty.Core.Models;

/// <summary>
/// Immutable, credential-free snapshot of the algorithms negotiated by the primary SSH
/// transport's initial handshake. It intentionally excludes authentication data, route
/// secrets, and the raw host key; only the verified SHA-256 host-key fingerprint is retained.
/// </summary>
public sealed record SshNegotiatedConnectionInfo
{
    public SshNegotiatedConnectionInfo(
        string? serverVersion,
        string? clientVersion,
        string? keyExchangeAlgorithm,
        string? hostKeyAlgorithm,
        string? hostKeySha256Fingerprint,
        string? clientToServerCipher,
        string? serverToClientCipher,
        string? clientToServerMac,
        string? serverToClientMac,
        string? clientToServerCompression,
        string? serverToClientCompression)
    {
        ServerVersion = Normalize(serverVersion);
        ClientVersion = Normalize(clientVersion);
        KeyExchangeAlgorithm = Normalize(keyExchangeAlgorithm);
        HostKeyAlgorithm = Normalize(hostKeyAlgorithm);
        HostKeySha256Fingerprint = Normalize(hostKeySha256Fingerprint);
        ClientToServerCipher = Normalize(clientToServerCipher);
        ServerToClientCipher = Normalize(serverToClientCipher);
        ClientToServerMac = Normalize(clientToServerMac);
        ServerToClientMac = Normalize(serverToClientMac);
        ClientToServerCompression = Normalize(clientToServerCompression);
        ServerToClientCompression = Normalize(serverToClientCompression);
    }

    public string? ServerVersion { get; }
    public string? ClientVersion { get; }
    public string? KeyExchangeAlgorithm { get; }
    public string? HostKeyAlgorithm { get; }
    public string? HostKeySha256Fingerprint { get; }
    public string? ClientToServerCipher { get; }
    public string? ServerToClientCipher { get; }
    public string? ClientToServerMac { get; }
    public string? ServerToClientMac { get; }
    public string? ClientToServerCompression { get; }
    public string? ServerToClientCompression { get; }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
