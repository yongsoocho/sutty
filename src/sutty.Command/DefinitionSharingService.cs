using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sutty.Command;

public enum ImportChangeKind { Add, Change, Duplicate, Invalid }
public enum ImportChoice { Skip, Copy, Update, Add }

public sealed class HostImportPreviewItem
{
    public required HostProfileDraft Draft { get; init; }
    public string? SourceId { get; init; }
    public HostProfile? Existing { get; init; }
    public ImportChangeKind Kind { get; init; }
    public ImportChoice Choice { get; set; } = ImportChoice.Skip;
    public string Detail { get; init; } = "";
    internal bool Shared { get; init; }
}

public sealed class CommandImportPreviewItem
{
    public required string Name { get; init; }
    public required string CommandText { get; init; }
    public CommandTemplate? Existing { get; init; }
    public ImportChangeKind Kind { get; init; }
    public ImportChoice Choice { get; set; } = ImportChoice.Skip;
}

public sealed record DefinitionImportPreview(
    IReadOnlyList<HostImportPreviewItem> Hosts,
    IReadOnlyList<CommandImportPreviewItem> Commands,
    IReadOnlyList<string> Warnings);

public sealed record HostImportApplyResult(int Added, int Updated, int Skipped, int Failed);

// These deliberately contain no vault identifiers, local paths, histories, trust keys,
// process arguments, or credential values. Never serialize HostProfile directly for sharing.
public sealed class SharedDefinitions
{
    public int SchemaVersion { get; set; } = 1;
    public List<SharedHost> Hosts { get; set; } = [];
    public List<SharedCommand> Commands { get; set; } = [];
}

public sealed class SharedHost
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string AuthMethod { get; set; } = "Password";
    public string AuthenticationAlias { get; set; } = "";
    public string GroupName { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string Environment { get; set; } = HostEnvironments.Unclassified;
    public bool IsFavorite { get; set; }
    public SharedRoute Route { get; set; } = new();
    public List<HostTunnelProfile> Tunnels { get; set; } = [];
}

public sealed class SharedRoute
{
    public string Type { get; set; } = "Direct";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string AuthMethod { get; set; } = "Password";
    public bool ProxyDns { get; set; } = true;
    public bool DisableDirect { get; set; }
}

public sealed class SharedCommand
{
    public string Name { get; set; } = "";
    public string CommandText { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SharedDefinitions))]
[JsonSerializable(typeof(SharedHost))]
internal sealed partial class SharedDefinitionsJsonContext : JsonSerializerContext;

/// <summary>Bounded local JSON sharing and explicit, per-row import application. Never connects or runs commands.</summary>
public static class DefinitionSharingService
{
    public const int SchemaVersion = 1;
    public const int MaxFileBytes = 4 * 1024 * 1024;
    private const int MaxItems = 1_000;
    private static readonly HashSet<string> AuthMethods = ["Password", "PublicKey", "Agent", "KeyboardInteractive"];
    private static readonly HashSet<string> RouteTypes = ["Direct", "SshJump", "Socks4", "Socks5", "HttpConnect", "ExternalProxyCommand"];

    public static string Export(IEnumerable<HostProfile> selectedHosts, IEnumerable<CommandTemplate> selectedCommands)
    {
        ArgumentNullException.ThrowIfNull(selectedHosts);
        ArgumentNullException.ThrowIfNull(selectedCommands);
        var hosts = selectedHosts.Take(MaxItems + 1).ToList();
        var commands = selectedCommands.Take(MaxItems + 1).ToList();
        if (hosts.Count + commands.Count > MaxItems)
            throw new ArgumentException("Choose at most 1,000 definitions per file.");
        var pack = new SharedDefinitions
        {
            Hosts = hosts.Select(ToShared).ToList(),
            Commands = commands.Select(command => new SharedCommand { Name = command.Name, CommandText = command.CommandText }).ToList(),
        };
        if (pack.Commands.Any(command => !ValidCommand(command.Name, command.CommandText)))
            throw new ArgumentException("A command template is too long or invalid.");
        var json = JsonSerializer.Serialize(pack, SharedDefinitionsJsonContext.Default.SharedDefinitions);
        if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
            throw new ArgumentException("The sharing file exceeds 4 MiB. Select fewer definitions.");
        return json;
    }

    public static DefinitionImportPreview Preview(HostProfileImportBatch batch) => BuildPreview(
        batch.Profiles.Select(draft => (Draft: draft, Id: (string?)null)), [], batch.Warnings, false);

    public static DefinitionImportPreview PreviewFile(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaxFileBytes) throw new InvalidDataException("The sharing file exceeds 4 MiB.");
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = file.Read(buffer)) > 0)
        {
            if (bounded.Length + count > MaxFileBytes) throw new InvalidDataException("The sharing file exceeds 4 MiB.");
            bounded.Write(buffer, 0, count);
        }
        bounded.Position = 0;
        using var reader = new StreamReader(bounded, new UTF8Encoding(false, true), true);
        return PreviewJson(reader.ReadToEnd());
    }

    /// <summary>Save exactly the reviewed JSON, keeping an existing export intact if writing fails or is cancelled.</summary>
    public static async Task SaveFileAsync(string path, string reviewedJson, CancellationToken cancellationToken = default)
    {
        if (Encoding.UTF8.GetByteCount(reviewedJson) > MaxFileBytes)
            throw new InvalidDataException("The sharing file exceeds 4 MiB.");
        var destination = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".sutty-share-{Guid.NewGuid():N}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(reviewedJson), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public static DefinitionImportPreview PreviewJson(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
            throw new InvalidDataException("The sharing file exceeds 4 MiB.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var number) || number != SchemaVersion)
            throw new InvalidDataException("Unsupported sharing schema. This app reads schemaVersion 1 only.");
        RejectDuplicateProperties(root);
        if (!root.TryGetProperty("hosts", out var hostElements) || hostElements.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("commands", out var commandElements) || commandElements.ValueKind != JsonValueKind.Array ||
            hostElements.GetArrayLength() + commandElements.GetArrayLength() > MaxItems)
            throw new InvalidDataException("The file must contain hosts and commands arrays with at most 1,000 definitions.");
        var warnings = new List<string>();
        FindUnsupportedFields(root, ["schemaVersion", "hosts", "commands"], warnings);
        var drafts = new List<(HostProfileDraft Draft, string? Id)>();
        foreach (var element in hostElements.EnumerateArray())
        {
            FindUnsupportedFields(element, ["id", "displayName", "host", "port", "username", "authMethod", "authenticationAlias", "groupName", "tags", "environment", "isFavorite", "route", "tunnels"], warnings);
            var host = element.Deserialize(SharedDefinitionsJsonContext.Default.SharedHost)
                ?? throw new InvalidDataException("A shared host is invalid.");
            // A missing/null/malformed route is never interpreted as a direct connection.
            var hasRoute = element.TryGetProperty("route", out var routeElement) &&
                routeElement.ValueKind == JsonValueKind.Object && routeElement.TryGetProperty("type", out var routeType) &&
                routeType.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(routeType.GetString());
            if (hasRoute)
                FindUnsupportedFields(routeElement, ["type", "host", "port", "username", "authMethod", "proxyDns", "disableDirect"], warnings);
            if (element.TryGetProperty("tunnels", out var tunnelElements) && tunnelElements.ValueKind == JsonValueKind.Array)
                foreach (var tunnelElement in tunnelElements.EnumerateArray())
                    FindUnsupportedFields(tunnelElement, ["type", "bindHost", "bindPort", "destinationHost", "destinationPort"], warnings);
            var route = hasRoute && host.Route is not null
                ? new HostRouteProfile
                {
                    Type = host.Route.Type, Host = host.Route.Host, Port = host.Route.Port,
                    Username = host.Route.Username, AuthMethod = host.Route.AuthMethod,
                    ProxyDns = host.Route.ProxyDns, DisableDirect = host.Route.DisableDirect,
                }
                : new HostRouteProfile { State = SavedRouteState.Corrupt, DisableDirect = true };
            drafts.Add((new HostProfileDraft
            {
                DisplayName = host.DisplayName, Host = host.Host, Port = host.Port, Username = host.Username,
                AuthMethod = host.AuthMethod, AuthenticationAlias = host.AuthenticationAlias,
                GroupName = host.GroupName, Tags = host.Tags, Environment = host.Environment,
                IsFavorite = host.IsFavorite, Route = route, Tunnels = host.Tunnels,
            }, host.Id));
        }
        var commands = new List<SharedCommand>();
        foreach (var element in commandElements.EnumerateArray())
        {
            FindUnsupportedFields(element, ["name", "commandText"], warnings);
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !element.TryGetProperty("commandText", out var body) || body.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("A command definition is invalid.");
            commands.Add(new SharedCommand { Name = name.GetString()!, CommandText = body.GetString()! });
        }
        return BuildPreview(drafts, commands, warnings, true);
    }

    private static DefinitionImportPreview BuildPreview(
        IEnumerable<(HostProfileDraft Draft, string? Id)> incoming, IEnumerable<SharedCommand> commands,
        IReadOnlyList<string> warnings, bool shared)
    {
        var local = HostProfileStore.GetAll(limit: 10_000);
        var hosts = new List<HostImportPreviewItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (draft, id) in incoming.Take(MaxItems + 1))
        {
            if (hosts.Count == MaxItems) throw new InvalidDataException("Import at most 1,000 hosts at once.");
            var error = ValidateImport(draft);
            if (shared && !ValidId(id)) error = "잘못된 프로필 ID / Invalid profile id";
            var identity = Identity(draft.Host, draft.Port, draft.Username);
            var existing = local.FirstOrDefault(profile => shared && profile.Id == id) ??
                local.FirstOrDefault(profile => Identity(profile.Host, profile.Port, profile.Username) == identity);
            var repeated = !seen.Add(identity) || (shared && !seenIds.Add(id ?? ""));
            var kind = error.Length > 0 ? ImportChangeKind.Invalid : existing is null
                ? repeated ? ImportChangeKind.Duplicate : ImportChangeKind.Add
                : Equivalent(existing, draft) ? ImportChangeKind.Duplicate : ImportChangeKind.Change;
            hosts.Add(new HostImportPreviewItem
            {
                Draft = draft, SourceId = id, Existing = existing, Kind = kind,
                Choice = ImportChoice.Skip, Shared = shared,
                Detail = error.Length > 0 ? error : repeated && existing is null
                    ? "파일 내 중복 · 복제로 별도 저장 가능 / Duplicate within file; choose Copy to keep another profile"
                    : existing is not null && !Compatible(existing, draft)
                        ? "연결 정보/인증 변경 · 로컬 인증 연결 해제 / Connection or authentication changes; local credential binding will be cleared"
                        : "",
            });
        }
        var localCommands = CommandStore.GetAll();
        var commandRows = new List<CommandImportPreviewItem>();
        var seenCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands)
        {
            var existing = localCommands.FirstOrDefault(item => string.Equals(item.Name, command.Name, StringComparison.OrdinalIgnoreCase));
            var kind = !ValidCommand(command.Name, command.CommandText) ? ImportChangeKind.Invalid :
                existing is null ? seenCommands.Add(command.Name) ? ImportChangeKind.Add : ImportChangeKind.Duplicate :
                existing.CommandText == command.CommandText ? ImportChangeKind.Duplicate : ImportChangeKind.Change;
            commandRows.Add(new CommandImportPreviewItem { Name = command.Name, CommandText = command.CommandText, Existing = existing, Kind = kind });
        }
        return new DefinitionImportPreview(hosts, commandRows, warnings.Distinct().ToList());
    }

    public static HostImportApplyResult Apply(DefinitionImportPreview preview, CancellationToken cancellationToken = default)
    {
        var added = 0; var updated = 0; var skipped = 0; var failed = 0;
        foreach (var row in preview.Hosts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Choice == ImportChoice.Skip) { skipped++; continue; }
            try
            {
                if (row.Kind == ImportChangeKind.Invalid || ValidateImport(row.Draft).Length > 0)
                    throw new ArgumentException("Invalid host definitions cannot be imported.");
                var draft = HostProfileStore.ValidateDraft(row.Draft);
                draft.CredentialId = null;
                if (row.Shared) { draft.PrivateKeyPath = ""; draft.Route.PrivateKeyPath = ""; }
                string? saveId = null;
                if (row.Choice == ImportChoice.Update)
                {
                    var previous = row.Existing ?? throw new ArgumentException("There is no existing host to update.");
                    var current = HostProfileStore.GetById(previous.Id);
                    if (current is null || current.UpdatedAtUtc != previous.UpdatedAtUtc)
                        throw new InvalidOperationException("The host changed after preview. Preview again.");
                    saveId = current.Id;
                    if (Compatible(current, draft))
                    {
                        draft.CredentialId = current.CredentialId;
                        draft.PrivateKeyPath = current.PrivateKeyPath;
                    }
                    if (RouteCompatible(current.Route, draft.Route)) draft.Route.PrivateKeyPath = current.Route.PrivateKeyPath;
                    if (draft.AuthenticationAlias is null) draft.AuthenticationAlias = current.AuthenticationAlias;
                }
                else if (row.Choice == ImportChoice.Copy)
                    draft.DisplayName = CopyName(draft.DisplayName);
                else if (row.Choice == ImportChoice.Add)
                {
                    if (row.Kind != ImportChangeKind.Add || HostProfileStore.GetAll(limit: 10_000).Any(profile =>
                            Identity(profile.Host, profile.Port, profile.Username) == Identity(draft.Host, draft.Port, draft.Username)))
                        throw new InvalidOperationException("A matching host now exists. Preview again.");
                    if (row.Shared)
                    {
                        if (!ValidId(row.SourceId) || HostProfileStore.GetById(row.SourceId!) is not null)
                            throw new InvalidOperationException("A profile id is no longer available. Preview again.");
                        saveId = row.SourceId;
                    }
                }
                else throw new ArgumentException("Invalid import choice.");
                HostProfileStore.Save(draft, saveId);
                if (row.Choice == ImportChoice.Update) updated++; else added++;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            { failed++; }
        }
        foreach (var row in preview.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Choice == ImportChoice.Skip) { skipped++; continue; }
            try
            {
                if (row.Kind == ImportChangeKind.Invalid || !ValidCommand(row.Name, row.CommandText))
                    throw new ArgumentException("Invalid command definition.");
                if (row.Choice == ImportChoice.Update)
                {
                    var old = row.Existing ?? throw new ArgumentException("There is no existing command to update.");
                    var current = CommandStore.GetAll().FirstOrDefault(command => command.Id == old.Id);
                    if (current is null || current.Name != old.Name || current.CommandText != old.CommandText)
                        throw new InvalidOperationException("The command changed after preview.");
                    CommandStore.Update(old.Id, row.Name, row.CommandText);
                    updated++;
                }
                else if (row.Choice == ImportChoice.Copy || (row.Choice == ImportChoice.Add && row.Kind == ImportChangeKind.Add))
                {
                    if (row.Choice == ImportChoice.Add && CommandStore.GetAll().Any(command => string.Equals(command.Name, row.Name, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("A matching command now exists.");
                    CommandStore.Add(row.Choice == ImportChoice.Copy ? CopyName(row.Name) : row.Name, row.CommandText);
                    added++;
                }
                else throw new ArgumentException("Invalid import choice.");
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            { failed++; }
        }
        return new HostImportApplyResult(added, updated, skipped, failed);
    }

    private static SharedHost ToShared(HostProfile profile) => new()
    {
        Id = profile.Id, DisplayName = profile.DisplayName, Host = profile.Host, Port = profile.Port,
        Username = profile.Username, AuthMethod = profile.AuthMethod, AuthenticationAlias = profile.AuthenticationAlias,
        GroupName = profile.GroupName, Tags = [.. profile.Tags], Environment = profile.Environment, IsFavorite = profile.IsFavorite,
        Route = new SharedRoute
        {
            Type = profile.Route.CanConnect ? profile.Route.Type : "Unsupported",
            Host = profile.Route.Host, Port = profile.Route.Port, Username = profile.Route.Username,
            AuthMethod = profile.Route.AuthMethod, ProxyDns = profile.Route.ProxyDns, DisableDirect = profile.Route.DisableDirect,
        },
        Tunnels = profile.Tunnels.Select(tunnel => tunnel with { }).ToList(),
    };

    private static string ValidateImport(HostProfileDraft draft)
    {
        if (draft.Route is null || !draft.Route.CanConnect || !RouteTypes.Contains(draft.Route.Type ?? ""))
            return "경로 미지원/손상 · 직접 연결로 우회하지 않음 / Unsupported or invalid route; direct fallback is blocked";
        if (draft.Route.Type == "ExternalProxyCommand")
            return "ProxyCommand 차단 · 검토 후 이 PC에서 호스트를 직접 구성하세요 / ProxyCommand blocked; review and configure this host locally";
        if (draft.Port is < 1 or > 65535 || !AuthMethods.Contains(draft.AuthMethod ?? "") ||
            (draft.Route.Type == "SshJump" && !AuthMethods.Contains(draft.Route.AuthMethod ?? "")))
            return "포트 또는 인증 방법 오류 / Invalid port or authentication method";
        if ((draft.Tags?.Any(tag => tag is null || tag.Length > 32 || tag.Any(char.IsControl)) ?? false) ||
            (draft.Tags?.Count() ?? 0) > 16 || (draft.Tunnels?.Count() ?? 0) > 32 ||
            (draft.Tunnels?.Any(tunnel => tunnel is null || tunnel.Type is not ("Local" or "Remote" or "Dynamic")) ?? false))
            return "태그 또는 터널 정의 오류 / Invalid tags or tunnel definitions";
        try { HostProfileStore.ValidateDraft(draft); return ""; }
        catch (ArgumentException) { return "호스트 필드 또는 경로 오류 / Invalid host fields or route"; }
    }

    private static bool Compatible(HostProfile existing, HostProfileDraft incoming) =>
        Identity(existing.Host, existing.Port, existing.Username) == Identity(incoming.Host, incoming.Port, incoming.Username) &&
        existing.AuthMethod == incoming.AuthMethod &&
        // One vault record also holds proxy/jump passwords. Never send them to a changed route.
        RouteCompatible(existing.Route, incoming.Route) &&
        (string.IsNullOrWhiteSpace(incoming.AuthenticationAlias) || existing.AuthenticationAlias == incoming.AuthenticationAlias);

    private static bool RouteCompatible(HostRouteProfile existing, HostRouteProfile incoming) =>
        existing.Type == incoming.Type && existing.Host.Equals(incoming.Host, StringComparison.OrdinalIgnoreCase) &&
        existing.Port == incoming.Port && existing.Username == incoming.Username && existing.AuthMethod == incoming.AuthMethod;

    private static bool Equivalent(HostProfile existing, HostProfileDraft incoming)
    {
        try
        {
            var normalized = HostProfileStore.ValidateDraft(incoming);
            var incomingHost = new HostProfile
            {
                Id = existing.Id, DisplayName = normalized.DisplayName, Host = normalized.Host, Port = normalized.Port,
                Username = normalized.Username, AuthMethod = normalized.AuthMethod, Tags = normalized.Tags!.ToList(),
                GroupName = normalized.GroupName, Environment = normalized.Environment, IsFavorite = normalized.IsFavorite,
                AuthenticationAlias = normalized.AuthenticationAlias ?? "", Route = normalized.Route, Tunnels = normalized.Tunnels!.ToList(),
            };
            return JsonSerializer.Serialize(ToShared(existing), SharedDefinitionsJsonContext.Default.SharedHost) ==
                JsonSerializer.Serialize(ToShared(incomingHost), SharedDefinitionsJsonContext.Default.SharedHost);
        }
        catch (ArgumentException) { return false; }
    }

    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static string Identity(string? host, int port, string? username) => $"{host?.Trim().ToLowerInvariant()}\u001f{port}\u001f{username?.Trim()}";
    private static string CopyName(string name) => $"{name[..Math.Min(name.Length, 120)]} (copy)";
    private static bool ValidCommand(string? name, string? text) => !string.IsNullOrWhiteSpace(name) && name.Length <= 128 &&
        !name.Any(char.IsControl) && !string.IsNullOrWhiteSpace(text) && text.Length <= 65_536 && !text.Contains('\0');

    private static void FindUnsupportedFields(JsonElement element, string[] allowed, ICollection<string> warnings)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid definition object.");
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                warnings.Add("미지원 필드를 제외했습니다. 자격증명·실행 설정은 활성화되지 않습니다. / Unsupported fields were omitted; credentials and execution settings are not activated.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON fields are not supported.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
}
