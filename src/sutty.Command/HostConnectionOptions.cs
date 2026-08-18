using System.Text.Json.Serialization;

namespace sutty.Command;

/// <summary>Whether persisted route metadata can be used for a new connection.</summary>
public enum SavedRouteState
{
    Valid,
    Unsupported,
    Corrupt,
}

public static class SavedRouteErrorCodes
{
    public const string Unsupported = "SAVED_ROUTE_UNSUPPORTED";
    public const string Corrupt = "SAVED_ROUTE_CORRUPT";
}

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
    public bool DisableDirect { get; set; }

    // These values are derived while loading untrusted/legacy SQLite metadata. They are
    // intentionally not persisted; saving requires the user to select a valid route first.
    [JsonIgnore]
    public SavedRouteState State { get; set; } = SavedRouteState.Valid;

    [JsonIgnore]
    public string ErrorCode { get; set; } = "";

    [JsonIgnore]
    public string SourceType { get; set; } = "";

    [JsonIgnore]
    public bool CanConnect => State == SavedRouteState.Valid;

    // Read-only compatibility bridge for profiles written before the strict-route rename.
    // Normalized profiles never write this legacy field back to SQLite.
    [JsonPropertyName("enterpriseMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyDisableDirect { get; set; }
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
