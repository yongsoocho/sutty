using System.Text.Json.Serialization;

namespace sutty.Command;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class CommandJsonContext : JsonSerializerContext;
