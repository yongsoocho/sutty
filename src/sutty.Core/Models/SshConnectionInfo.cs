namespace sutty.Core.Models;

using sutty.Core.Security;

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

    /// <summary>
    /// Called only when the server presents an unknown public host key. The first
    /// handshake is rejected before this callback runs; a positive decision causes
    /// the session to create a fresh SSH client and retry. Changed keys never reach
    /// this callback and are always rejected.
    /// </summary>
    public Func<HostKeyVerification, CancellationToken, Task<HostKeyDecision>>?
        HostKeyPromptAsync { get; set; }

    /// <summary>탭 헤더 등에 쓸 표시 이름. DisplayName이 없으면 user@host.</summary>
    public string Title =>
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName
        : string.IsNullOrWhiteSpace(Username) ? Host
        : $"{Username}@{Host}";
}
