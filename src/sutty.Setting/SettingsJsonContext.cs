using System.Text.Json.Serialization;

namespace sutty.Setting;

[JsonSourceGenerationOptions(
    WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(WorkspaceSnapshot))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
