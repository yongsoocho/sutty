namespace sutty.Command;

/// <summary>Credential-free connection route persisted with a saved host.</summary>
public sealed record HostRouteProfile
{
    public string Id { get; set; } = "direct";
    public string Type { get; set; } = "Direct";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string AuthMethod { get; set; } = "Password";
    public string PrivateKeyPath { get; set; } = "";
    public string Command { get; set; } = "";
    public bool ProxyDns { get; set; } = true;
    public bool EnterpriseMode { get; set; }
}

/// <summary>One session-scoped forwarding rule persisted with a saved host.</summary>
public sealed record HostTunnelProfile
{
    public string Type { get; set; } = "Local";
    public string BindHost { get; set; } = "127.0.0.1";
    public int BindPort { get; set; }
    public string DestinationHost { get; set; } = "127.0.0.1";
    public int DestinationPort { get; set; }
}
