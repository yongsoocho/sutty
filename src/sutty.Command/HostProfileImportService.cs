using Microsoft.Win32;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace sutty.Command;

public enum HostProfileImportSource
{
    OpenSsh,
    Putty,
    SecureCrt,
    SiteManagerXml,
}

public sealed record HostProfileImportBatch(
    HostProfileImportSource Source,
    IReadOnlyList<HostProfileDraft> Profiles,
    IReadOnlyList<string> Warnings);

public sealed record HostProfileImportSaveResult(int Imported, int Skipped, int Failed);

/// <summary>
/// Imports credential-free connection settings. Passwords and passphrases are never read;
/// imported profiles ask for a secret on first use unless an Agent identity is selected.
/// </summary>
public static class HostProfileImportService
{
    private static readonly HashSet<string> SupportedOpenSshOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "HostName", "User", "Port", "IdentityFile", "ProxyJump", "ProxyCommand",
        "LocalForward", "RemoteForward", "DynamicForward", "PreferredAuthentications",
    };

    public static HostProfileImportBatch ImportOpenSshFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        return ParseOpenSsh(File.ReadAllText(fullPath), Path.GetDirectoryName(fullPath));
    }

    public static HostProfileImportBatch ParseOpenSsh(string text, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var warnings = new List<string>();
        var global = new OptionSet();
        var blocks = new List<(List<string> Aliases, OptionSet Options)>();
        OptionSet? current = global;

        foreach (var logicalLine in ReadLogicalLines(text))
        {
            var tokens = Tokenize(logicalLine);
            if (tokens.Count == 0)
                continue;
            var key = tokens[0];
            var value = string.Join(' ', tokens.Skip(1));
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                var aliases = tokens.Skip(1).Where(alias => alias.Length > 0).ToList();
                current = new OptionSet();
                blocks.Add((aliases, current));
                continue;
            }

            if (key.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Conditional Match block was not imported: {value}");
                current = null;
                continue;
            }

            if (key.Equals("Include", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Include was not expanded: {value}");
                continue;
            }
            if (!SupportedOpenSshOptions.Contains(key))
                warnings.Add($"미지원 OpenSSH 옵션 / Unsupported OpenSSH option: {key}");
            current?.Add(key, value);
        }

        var profiles = new List<HostProfileDraft>();
        var concreteAliases = blocks
            .SelectMany(block => block.Aliases)
            .Where(alias => !alias.StartsWith('!') && !ContainsHostPattern(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var wildcard in blocks.SelectMany(block => block.Aliases)
                     .Where(alias => !alias.StartsWith('!') && ContainsHostPattern(alias))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            warnings.Add($"Wildcard Host was applied as a default but not imported as a profile: {wildcard}");
        }

        foreach (var alias in concreteAliases)
        {
            var effective = new OptionSet();
            effective.AddRange(global);
            foreach (var block in blocks)
            {
                if (MatchesHostBlock(alias, block.Aliases))
                    effective.AddRange(block.Options);
            }

            try
            {
                profiles.Add(BuildOpenSshProfile(alias, effective, baseDirectory, warnings));
            }
            catch (ArgumentException error)
            {
                warnings.Add($"Host {alias} was skipped: {error.Message}");
            }
        }

        return new HostProfileImportBatch(HostProfileImportSource.OpenSsh, profiles, warnings);
    }

    public static HostProfileImportBatch ImportPuttyRegistry()
    {
        if (!OperatingSystem.IsWindows())
            return new HostProfileImportBatch(HostProfileImportSource.Putty, [],
                ["The Windows saved-session registry is unavailable on this platform."]);

        return ImportPuttyRegistryWindows();
    }

    [SupportedOSPlatform("windows")]
    private static HostProfileImportBatch ImportPuttyRegistryWindows()
    {
        var profiles = new List<HostProfileDraft>();
        var warnings = new List<string>();
        using var sessions = Registry.CurrentUser.OpenSubKey(@"Software\SimonTatham\PuTTY\Sessions");
        if (sessions is null)
            return new HostProfileImportBatch(HostProfileImportSource.Putty, [],
                ["No saved Windows sessions were found."]);

        foreach (var encodedName in sessions.GetSubKeyNames())
        {
            using var session = sessions.OpenSubKey(encodedName);
            if (session is null)
                continue;
            var values = session.GetValueNames().ToDictionary(
                name => name,
                name => session.GetValue(name),
                StringComparer.OrdinalIgnoreCase);
            try
            {
                var profile = ParsePuttySession(Uri.UnescapeDataString(encodedName), values, warnings);
                if (profile is not null)
                    profiles.Add(profile);
            }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
            {
                warnings.Add($"Saved session {encodedName} was skipped: {error.Message}");
            }
        }
        return new HostProfileImportBatch(HostProfileImportSource.Putty, profiles, warnings);
    }

    public static HostProfileDraft? ParsePuttySession(
        string displayName,
        IReadOnlyDictionary<string, object?> values,
        ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var host = StringValue(values, "HostName").Trim();
        if (host.Length == 0)
            return null;
        var keyPath = StringValue(values, "PublicKeyFile").Trim();
        var proxyMethod = IntValue(values, "ProxyMethod", 0);
        var route = proxyMethod switch
        {
            1 => BuildProxyRoute("Socks4", values),
            2 => BuildProxyRoute("Socks5", values),
            3 => BuildProxyRoute("HttpConnect", values),
            5 => new HostRouteProfile
            {
                Id = "imported-proxy-command",
                Type = "ExternalProxyCommand",
                Command = StringValue(values, "ProxyTelnetCommand").Trim(),
            },
            0 => new HostRouteProfile(),
            _ => new HostRouteProfile { Type = "Unsupported", State = SavedRouteState.Unsupported, DisableDirect = true },
        };
        if (proxyMethod is < 0 or 4 or > 5)
            warnings?.Add($"{displayName}: unsupported proxy method {proxyMethod}; import is blocked to prevent direct fallback.");

        return new HostProfileDraft
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? host : displayName,
            Host = host,
            Port = NormalizePort(IntValue(values, "PortNumber", 22)),
            Username = StringValue(values, "UserName").Trim(),
            AuthMethod = keyPath.Length > 0 ? "PublicKey" : "Password",
            PrivateKeyPath = keyPath,
            GroupName = "Imported",
            Tags = ["imported"],
            Route = route,
            Tunnels = ParsePuttyForwardings(values, warnings),
        };
    }

    public static HostProfileImportBatch ImportSecureCrtDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        var profiles = new List<HostProfileDraft>();
        var warnings = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.ini", SearchOption.AllDirectories))
        {
            try
            {
                var relative = Path.GetRelativePath(root, file);
                var group = Path.GetDirectoryName(relative) ?? "Imported";
                var profile = ParseSecureCrtSession(
                    Path.GetFileNameWithoutExtension(file),
                    File.ReadAllText(file),
                    group,
                    warnings);
                if (profile is not null)
                    profiles.Add(profile);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                warnings.Add($"{file} was skipped: {error.Message}");
            }
        }
        return new HostProfileImportBatch(HostProfileImportSource.SecureCrt, profiles, warnings);
    }

    public static HostProfileDraft? ParseSecureCrtSession(
        string displayName,
        string text,
        string groupName = "Imported",
        ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var separator = line.IndexOf('=');
            var quoteStart = line.IndexOf('"');
            var quoteEnd = quoteStart < 0 ? -1 : line.IndexOf('"', quoteStart + 1);
            if (separator < 0 || quoteStart < 0 || quoteEnd < quoteStart)
                continue;
            var key = line[(quoteStart + 1)..quoteEnd];
            values[key] = line[(separator + 1)..].Trim();
        }

        var host = FirstValue(values, "Hostname", "Host Name").Trim();
        if (host.Length == 0)
            return null;
        var keyPath = FirstValue(values, "Identity Filename V2", "Identity Filename").Trim();
        var portRaw = FirstValue(values, "[SSH2] Port", "Port");
        var port = ParseSecureCrtInteger(portRaw, 22);
        if (values.Keys.Any(key => key.Contains("Port Forward", StringComparison.OrdinalIgnoreCase)))
            warnings?.Add($"{displayName}: encoded forwarding table requires manual review.");
        var firewall = FirstValue(values, "Firewall Name", "Firewall Session").Trim();
        var usesFirewall = firewall.Length > 0 && !firewall.Equals("None", StringComparison.OrdinalIgnoreCase);
        if (usesFirewall)
            warnings?.Add($"{displayName}: INI firewall/jump settings require manual configuration; direct fallback is blocked.");

        return new HostProfileDraft
        {
            DisplayName = displayName,
            Host = host,
            Port = NormalizePort(port),
            Username = FirstValue(values, "Username", "User Name").Trim(),
            AuthMethod = keyPath.Length > 0 ? "PublicKey" : "Password",
            PrivateKeyPath = keyPath,
            GroupName = groupName,
            Tags = ["imported"],
            Route = usesFirewall
                ? new HostRouteProfile { Type = "Unsupported", State = SavedRouteState.Unsupported, DisableDirect = true }
                : new HostRouteProfile(),
        };
    }

    /// <summary>
    /// Imports SFTP entries from a supported Site Manager XML file. FTP/FTPS entries are
    /// deliberately skipped: Sutty never guesses an SSH configuration from another protocol.
    /// Password values are not read, even when they are present in a legacy export.
    /// </summary>
    public static HostProfileImportBatch ImportSftpSiteManagerXml(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        using var input = File.OpenRead(fullPath);
        using var reader = XmlReader.Create(input, CreateSafeXmlReaderSettings());
        return ParseSiteManagerDocument(XDocument.Load(reader, LoadOptions.None));
    }

    /// <summary>Parses a supported SFTP Site Manager XML document without reading credentials.</summary>
    public static HostProfileImportBatch ParseSftpSiteManagerXml(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var input = new StringReader(text);
        using var reader = XmlReader.Create(input, CreateSafeXmlReaderSettings());
        return ParseSiteManagerDocument(XDocument.Load(reader, LoadOptions.None));
    }

    private static HostProfileImportBatch ParseSiteManagerDocument(XDocument document)
    {
        var profiles = new List<HostProfileDraft>();
        var warnings = new List<string>();
        var roots = document.Descendants()
            .Where(element => element.Name.LocalName.Equals("Servers", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (roots.Length == 0)
        {
            warnings.Add("No SFTP Site Manager Servers element was found in the selected XML file.");
            return new HostProfileImportBatch(HostProfileImportSource.SiteManagerXml, profiles, warnings);
        }

        foreach (var root in roots)
            ParseSiteManagerContainer(root, "SFTP Site Manager import", profiles, warnings);
        return new HostProfileImportBatch(HostProfileImportSource.SiteManagerXml, profiles, warnings);
    }

    private static void ParseSiteManagerContainer(
        XElement container,
        string groupName,
        ICollection<HostProfileDraft> profiles,
        ICollection<string> warnings)
    {
        foreach (var element in container.Elements())
        {
            if (element.Name.LocalName.Equals("Folder", StringComparison.OrdinalIgnoreCase))
            {
                var folderName = SiteManagerValue(element, "Name");
                var childGroup = string.IsNullOrWhiteSpace(folderName)
                    ? groupName
                    : $"{groupName} / {folderName.Trim()}";
                ParseSiteManagerContainer(element, childGroup, profiles, warnings);
                continue;
            }
            if (!element.Name.LocalName.Equals("Server", StringComparison.OrdinalIgnoreCase))
                continue;

            var profile = ParseSiteManagerServer(element, groupName, warnings);
            if (profile is not null)
                profiles.Add(profile);
        }
    }

    private static HostProfileDraft? ParseSiteManagerServer(
        XElement server,
        string groupName,
        ICollection<string> warnings)
    {
        var displayName = SiteManagerValue(server, "Name");
        var host = SiteManagerValue(server, "Host").Trim();
        var protocol = SiteManagerValue(server, "Protocol");
        var identifier = string.IsNullOrWhiteSpace(displayName) ? host : displayName;
        if (host.Length == 0)
        {
            warnings.Add("An SFTP Site Manager entry without a host was skipped.");
            return null;
        }
        if (!IsSiteManagerSftpProtocol(protocol))
        {
            warnings.Add($"{identifier}: non-SFTP protocol '{protocol}' was skipped.");
            return null;
        }

        var keyPath = SiteManagerValue(server, "Keyfile").Trim();
        if (keyPath.Length == 0)
            keyPath = SiteManagerValue(server, "KeyFile").Trim();
        return new HostProfileDraft
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? host : displayName.Trim(),
            Host = host,
            Port = NormalizePort(ParseInteger(SiteManagerValue(server, "Port"), 22)),
            Username = SiteManagerValue(server, "User").Trim(),
            AuthMethod = keyPath.Length > 0 ? "PublicKey" : "Password",
            PrivateKeyPath = keyPath,
            GroupName = groupName,
            Tags = ["imported", "site-manager"],
        };
    }

    private static XmlReaderSettings CreateSafeXmlReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 4 * 1024 * 1024,
    };

    private static string SiteManagerValue(XElement element, string name) =>
        element.Elements()
            .FirstOrDefault(child => child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value
            .Trim() ?? "";

    private static bool IsSiteManagerSftpProtocol(string value) =>
        value.Trim().Equals("1", StringComparison.Ordinal) ||
        value.Contains("sftp", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("ssh", StringComparison.OrdinalIgnoreCase);

    public static HostProfileImportSaveResult SaveUnique(HostProfileImportBatch batch)
    {
        var preview = DefinitionSharingService.Preview(batch);
        foreach (var row in preview.Hosts.Where(row => row.Kind == ImportChangeKind.Add))
            row.Choice = ImportChoice.Add;
        var result = DefinitionSharingService.Apply(preview);
        var invalid = preview.Hosts.Count(row => row.Kind == ImportChangeKind.Invalid);
        return new HostProfileImportSaveResult(result.Added, result.Skipped - invalid, result.Failed + invalid);
    }

    private static HostProfileDraft BuildOpenSshProfile(
        string alias,
        OptionSet options,
        string? baseDirectory,
        ICollection<string> warnings)
    {
        var host = options.Get("HostName").FirstOrDefault() ?? alias;
        var keyPath = options.Get("IdentityFile").FirstOrDefault() ?? "";
        keyPath = ExpandPath(keyPath, baseDirectory);
        var proxyJump = options.Get("ProxyJump").FirstOrDefault();
        var proxyCommand = options.Get("ProxyCommand").FirstOrDefault();
        var route = new HostRouteProfile();
        if (!string.IsNullOrWhiteSpace(proxyJump) &&
            !proxyJump.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            var firstHop = proxyJump.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
            var (jumpUser, jumpHost, jumpPort) = ParseJump(firstHop);
            route = new HostRouteProfile
            {
                Id = $"imported-jump-{alias}",
                Type = "SshJump",
                Host = jumpHost,
                Port = jumpPort,
                Username = jumpUser,
                AuthMethod = "Agent",
                ProxyDns = true,
            };
            if (proxyJump.Contains(','))
            {
                route.State = SavedRouteState.Unsupported;
                route.DisableDirect = true;
                warnings.Add($"Host {alias}: multiple ProxyJump hops require manual configuration; import is blocked.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(proxyCommand) &&
                 !proxyCommand.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            route = new HostRouteProfile
            {
                Id = $"imported-proxy-command-{alias}",
                Type = "ExternalProxyCommand",
                Command = proxyCommand,
            };
        }

        var tunnels = new List<HostTunnelProfile>();
        foreach (var value in options.Get("LocalForward").Distinct(StringComparer.Ordinal))
            if (TryParseForward("Local", value, out var tunnel)) tunnels.Add(tunnel);
            else warnings.Add($"Host {alias}: LocalForward was not understood: {value}");
        foreach (var value in options.Get("RemoteForward").Distinct(StringComparer.Ordinal))
            if (TryParseForward("Remote", value, out var tunnel)) tunnels.Add(tunnel);
            else warnings.Add($"Host {alias}: RemoteForward was not understood: {value}");
        foreach (var value in options.Get("DynamicForward").Distinct(StringComparer.Ordinal))
            if (TryParseForward("Dynamic", value, out var tunnel)) tunnels.Add(tunnel);
            else warnings.Add($"Host {alias}: DynamicForward was not understood: {value}");

        var preferredAuthentication = options.Get("PreferredAuthentications").FirstOrDefault() ?? "";
        var preferredMethods = preferredAuthentication.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var authMethod = keyPath.Length > 0
            ? "PublicKey"
            : preferredMethods.Any(value =>
                value.Equals("keyboard-interactive", StringComparison.OrdinalIgnoreCase))
                ? "KeyboardInteractive"
                : preferredMethods.Any(value =>
                    value.Equals("password", StringComparison.OrdinalIgnoreCase))
                    ? "Password"
                    : "Agent";

        return new HostProfileDraft
        {
            DisplayName = alias,
            Host = host,
            Port = NormalizePort(ParseInteger(options.Get("Port").FirstOrDefault(), 22)),
            Username = options.Get("User").FirstOrDefault() ?? "",
            AuthMethod = authMethod,
            PrivateKeyPath = keyPath,
            GroupName = "OpenSSH import",
            Tags = ["imported"],
            Route = route,
            Tunnels = tunnels,
        };
    }

    private static HostRouteProfile BuildProxyRoute(
        string type,
        IReadOnlyDictionary<string, object?> values) => new()
    {
        Id = $"imported-{type.ToLowerInvariant()}",
        Type = type,
        Host = StringValue(values, "ProxyHost").Trim(),
        Port = NormalizePort(IntValue(values, "ProxyPort", type == "HttpConnect" ? 8080 : 1080)),
        Username = StringValue(values, "ProxyUsername").Trim(),
        ProxyDns = IntValue(values, "ProxyDNS", 2) != 1,
    };

    private static List<HostTunnelProfile> ParsePuttyForwardings(
        IReadOnlyDictionary<string, object?> values,
        ICollection<string>? warnings)
    {
        if (!values.TryGetValue("PortForwardings", out var raw) || raw is null)
            return [];
        var lines = raw switch
        {
            string[] array => array,
            string value => value.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
            _ => [],
        };
        var tunnels = new List<HostTunnelProfile>();
        foreach (var line in lines)
        {
            var separator = line.IndexOf('=');
            if (separator <= 1)
                continue;
            var prefix = char.ToUpperInvariant(line[0]);
            var type = prefix switch { 'R' => "Remote", 'D' => "Dynamic", _ => "Local" };
            var bind = line[1..separator];
            var destination = line[(separator + 1)..];
            var representation = type == "Dynamic" ? bind : $"{bind} {destination}";
            if (TryParseForward(type, representation, out var tunnel))
                tunnels.Add(tunnel);
            else
                warnings?.Add($"Forwarding was not understood: {line}");
        }
        return tunnels;
    }

    private static bool TryParseForward(string type, string value, out HostTunnelProfile tunnel)
    {
        tunnel = new HostTunnelProfile();
        var tokens = Tokenize(value);
        if (tokens.Count == 0)
            return false;
        if (!TryParseBind(tokens[0], out var bindHost, out var bindPort))
            return false;
        if (type == "Dynamic")
        {
            tunnel = new HostTunnelProfile
            {
                Type = type,
                BindHost = bindHost,
                BindPort = bindPort,
                DestinationHost = "127.0.0.1",
            };
            return true;
        }
        if (tokens.Count < 2 || !TryParseEndpoint(tokens[1], out var destinationHost, out var destinationPort))
            return false;
        tunnel = new HostTunnelProfile
        {
            Type = type,
            BindHost = bindHost,
            BindPort = bindPort,
            DestinationHost = destinationHost,
            DestinationPort = destinationPort,
        };
        return true;
    }

    private static bool TryParseBind(string value, out string host, out int port)
    {
        if (int.TryParse(value, out port) && port is >= 1 and <= 65_535)
        {
            host = "127.0.0.1";
            return true;
        }
        return TryParseEndpoint(value, out host, out port);
    }

    private static bool TryParseEndpoint(string value, out string host, out int port)
    {
        host = "";
        port = 0;
        value = value.Trim();
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0 || close + 2 > value.Length || value[close + 1] != ':')
                return false;
            host = value[1..close];
            return int.TryParse(value[(close + 2)..], out port) && port is >= 1 and <= 65_535;
        }
        var separator = value.LastIndexOf(':');
        if (separator <= 0)
            return false;
        host = value[..separator];
        return host.Length > 0 && int.TryParse(value[(separator + 1)..], out port) &&
               port is >= 1 and <= 65_535;
    }

    private static (string User, string Host, int Port) ParseJump(string value)
    {
        var user = "";
        var at = value.LastIndexOf('@');
        if (at >= 0)
        {
            user = value[..at];
            value = value[(at + 1)..];
        }
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0) throw new ArgumentException("Invalid bracketed jump host.");
            var host = value[1..close];
            var port = close + 1 < value.Length && value[close + 1] == ':'
                ? ParseInteger(value[(close + 2)..], 22)
                : 22;
            return (user, host, NormalizePort(port));
        }
        var separator = value.LastIndexOf(':');
        if (separator > 0 && int.TryParse(value[(separator + 1)..], out var parsedPort))
            return (user, value[..separator], NormalizePort(parsedPort));
        return (user, value, 22);
    }

    private static IEnumerable<string> ReadLogicalLines(string text)
    {
        var pending = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.EndsWith('\\'))
            {
                pending.Append(line[..^1]).Append(' ');
                continue;
            }
            pending.Append(line);
            var logical = pending.ToString().Trim();
            pending.Clear();
            if (logical.Length > 0)
                yield return logical;
        }
        if (pending.Length > 0)
            yield return pending.ToString();
    }

    private static string StripComment(string value)
    {
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (escaped) { escaped = false; continue; }
            if (character == '\\') { escaped = true; continue; }
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                continue;
            }
            if (character is '\'' or '"') { quote = character; continue; }
            if (character == '#') return value[..index];
        }
        return value;
    }

    private static List<string> Tokenize(string value)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (escaped) { current.Append(character); escaped = false; continue; }
            if (character == '\\' && quote != '\'')
            {
                var next = index + 1 < value.Length ? value[index + 1] : '\0';
                if (next is '\\' or '"' or '\'' or '#' || char.IsWhiteSpace(next))
                {
                    escaped = true;
                    continue;
                }
                current.Append(character);
                continue;
            }
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                else current.Append(character);
                continue;
            }
            if (character is '\'' or '"') { quote = character; continue; }
            if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(character);
        }
        if (escaped) current.Append('\\');
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static string ExpandPath(string value, string? baseDirectory)
    {
        if (value.Length == 0) return "";
        if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal) ||
            value.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                value.Length == 1 ? "" : value[2..]);
        }
        if (!Path.IsPathFullyQualified(value) && !string.IsNullOrWhiteSpace(baseDirectory))
            return Path.GetFullPath(value, baseDirectory);
        return value;
    }

    private static bool ContainsHostPattern(string value) => value.IndexOfAny(['*', '?', '[']) >= 0;

    private static bool MatchesHostBlock(string alias, IReadOnlyCollection<string> patterns)
    {
        var positiveMatch = false;
        foreach (var rawPattern in patterns)
        {
            var negated = rawPattern.StartsWith('!');
            var pattern = negated ? rawPattern[1..] : rawPattern;
            if (!HostPatternMatches(alias, pattern))
                continue;
            if (negated)
                return false;
            positiveMatch = true;
        }
        return positiveMatch;
    }

    private static bool HostPatternMatches(string alias, string pattern)
    {
        var expression = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*')
            {
                expression.Append(".*");
            }
            else if (character == '?')
            {
                expression.Append('.');
            }
            else if (character == '[' &&
                     pattern.IndexOf(']', index + 1) is var close && close > index + 1)
            {
                var content = pattern[(index + 1)..close];
                if (content.StartsWith('!'))
                    content = "^" + content[1..];
                expression.Append('[')
                    .Append(content.Replace("\\", "\\\\", StringComparison.Ordinal))
                    .Append(']');
                index = close;
            }
            else
            {
                expression.Append(Regex.Escape(character.ToString()));
            }
        }
        expression.Append('$');
        return Regex.IsMatch(
            alias,
            expression.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
    }

    private static int ParseInteger(string? value, int fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback :
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;

    private static int ParseSecureCrtInteger(string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (value.Length == 8 && value.All(Uri.IsHexDigit) &&
            int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var encodedValue))
        {
            return encodedValue;
        }
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimalValue) &&
            decimalValue is >= 1 and <= 65_535)
            return decimalValue;
        return int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexValue)
            ? hexValue
            : 0;
    }

    // Keep malformed source ports invalid so preview can show them instead of guessing port 22.
    private static int NormalizePort(int value) => value is >= 1 and <= 65_535 ? value : 0;

    private static string StringValue(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";

    private static int IntValue(IReadOnlyDictionary<string, object?> values, string key, int fallback) =>
        values.TryGetValue(key, out var value) && value is not null
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : fallback;

    private static string FirstValue(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
            if (values.TryGetValue(key, out var value)) return value;
        return "";
    }

    private sealed class OptionSet
    {
        private readonly Dictionary<string, List<string>> _values =
            new(StringComparer.OrdinalIgnoreCase);

        public void Add(string key, string value)
        {
            if (!_values.TryGetValue(key, out var items))
                _values[key] = items = [];
            items.Add(value);
        }

        public void AddRange(OptionSet source)
        {
            foreach (var pair in source._values)
                foreach (var value in pair.Value)
                    Add(pair.Key, value);
        }

        public IReadOnlyList<string> Get(string key) =>
            _values.TryGetValue(key, out var values) ? values : [];
    }
}
