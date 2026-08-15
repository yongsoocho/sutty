using System.Text.Json.Serialization;

namespace sutty.Command;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(HostRouteProfile))]
[JsonSerializable(typeof(List<HostTunnelProfile>))]
internal sealed partial class CommandJsonContext : JsonSerializerContext;
