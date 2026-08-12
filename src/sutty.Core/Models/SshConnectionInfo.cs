namespace sutty.Core.Models;

using sutty.Core.Security;
using sutty.Core.Routing;

public enum SshAuthMethod
{
    Password,
    PublicKey,
    Agent,
    KeyboardInteractive,
}

/// <summary>Connect 시 세션에 전달되는 연결 정보. UI와 Core가 공유하는 계약.</summary>
public sealed class SshConnectionInfo
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string DisplayName { get; set; } = "";
    public string Username { get; set; } = "";
    public SshAuthMethod AuthMethod { get; set; }
    public string Password { get; set; } = "";
    public string PrivateKeyPath { get; set; } = "";
    public string Passphrase { get; set; } = "";
    public string JumpHost { get; set; } = "";
    public int KeepAliveSeconds { get; set; }
    public bool Compression { get; set; }
    public bool X11Forwarding { get; set; }
    public List<string> Tags { get; set; } = [];

    /// <summary>Network route shared by this session's SSH and SFTP transports.</summary>
    public ConnectionRoute Route { get; set; } = new();

    /// <summary>Fail-closed route constraints evaluated before a socket is opened.</summary>
    public ConnectionRoutePolicy RoutePolicy { get; set; } = new();

    /// <summary>Optional id of the explicit saved-host profile that opened this connection.</summary>
    public string? SavedHostId { get; set; }

    /// <summary>When true, the UI persists or updates a non-secret saved-host profile.</summary>
    public bool SaveProfile { get; set; }

    /// <summary>
    /// Opt-in request to place the password or private-key passphrase in the local encrypted vault.
    /// Authentication values are never written to the profile or connection-history database.
    /// </summary>
    public bool RememberCredential { get; set; }

    /// <summary>Opaque reference to a local encrypted-vault record.</summary>
    public string? CredentialId { get; set; }

    public string GroupName { get; set; } = "";
    public string Environment { get; set; } = "Unclassified";
    public bool IsFavorite { get; set; }

    /// <summary>
    /// Called only when the server presents an unknown public host key. The first
    /// handshake is rejected before this callback runs; a positive decision causes
    /// the session to create a fresh SSH client and retry. Changed keys never reach
    /// this callback and are always rejected.
    /// </summary>
    public Func<HostKeyVerification, CancellationToken, Task<HostKeyDecision>>?
        HostKeyPromptAsync
    { get; set; }

    /// <summary>탭 헤더 등에 쓸 표시 이름. DisplayName이 없으면 user@host.</summary>
    public string Title =>
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName
        : string.IsNullOrWhiteSpace(Username) ? Host
        : $"{Username}@{Host}";
}
