using System.Security.Cryptography;
using System.Text;

namespace sutty.Core.Sftp;

/// <summary>A verified destination and account. A gateway is transport, not file ownership.</summary>
public static class SftpEndpointIdentity
{
    public static string Create(string host, int port, string username, string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) throw new InvalidOperationException("The SFTP host key is unavailable.");
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(host.Trim().ToLowerInvariant());
            writer.Write(port);
            writer.Write(username);
            writer.Write(fingerprint);
        }
        return "sftp:" + Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }
}
