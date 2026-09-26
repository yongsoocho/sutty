using System.Diagnostics;
using System.Text;

namespace sutty.Core.Terminal;

/// <summary>
/// Describes the connection-oriented forms Sutty can label specially. Other
/// direct executables remain supported without treating them as shell commands.
/// </summary>
public enum LocalTerminalLaunchKind
{
    DirectExecutable,
    OpenSsh,
    MultipassConnect,
}

/// <summary>
/// Resolves a bare executable name to an existing absolute .exe path. The
/// planner independently verifies every returned path before it can be used.
/// </summary>
public interface ILocalTerminalExecutableResolver
{
    string? ResolveExecutable(string executableFileName);
}

/// <summary>
/// A validated direct-process terminal launch. The executable and its arguments
/// are distinct values, so callers never need to pass the user's text through
/// cmd.exe, PowerShell, or another command interpreter.
/// </summary>
public sealed class LocalTerminalLaunchPlan
{
    private readonly string[] _arguments;
    private readonly IReadOnlyList<string> _readOnlyArguments;

    internal LocalTerminalLaunchPlan(
        LocalTerminalLaunchKind kind,
        string executablePath,
        IEnumerable<string> arguments,
        string canonicalCommand,
        string launchTitle)
    {
        Kind = kind;
        ExecutablePath = executablePath;
        _arguments = arguments.ToArray();
        _readOnlyArguments = Array.AsReadOnly(_arguments);
        CanonicalCommand = canonicalCommand;
        LaunchTitle = launchTitle;
    }

    public LocalTerminalLaunchKind Kind { get; }
    /// <summary>
    /// Runtime-only absolute executable path. Callers must not persist this value:
    /// a future launch resolves the saved canonical command on the current PC.
    /// </summary>
    public string ExecutablePath { get; }
    public IReadOnlyList<string> Arguments => _readOnlyArguments;

    /// <summary>
    /// Normalized, re-parseable user command text with no resolved local path or
    /// shell expansion. This class does not write it to storage.
    /// </summary>
    public string CanonicalCommand { get; }

    /// <summary>Short tab label derived from the validated direct command.</summary>
    public string LaunchTitle { get; }

    /// <summary>Compatibility alias for UI display code.</summary>
    public string DisplayCommand => CanonicalCommand;

    /// <summary>Creates a no-shell ProcessStartInfo for callers outside ConPTY.</summary>
    public ProcessStartInfo CreateProcessStartInfo()
    {
        var start = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (var argument in _arguments)
            start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>
    /// Formats the already separated argument vector for the native CreateProcess
    /// call used by ConPTY. No command interpreter receives this string.
    /// </summary>
    internal string CreateNativeCommandLine()
    {
        var commandLine = new StringBuilder(QuoteWindowsArgument(ExecutablePath));
        foreach (var argument in _arguments)
            commandLine.Append(' ').Append(QuoteWindowsArgument(argument));
        return commandLine.ToString();
    }

    internal static string QuoteWindowsArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new StringBuilder("\"");
        var slashCount = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashCount++;
                continue;
            }

            if (character == '\"')
                result.Append('\\', slashCount * 2 + 1);
            else
                result.Append('\\', slashCount);
            result.Append(character);
            slashCount = 0;
        }

        return result.Append('\\', slashCount * 2).Append('\"').ToString();
    }
}

/// <summary>
/// Creates safe direct-process launch plans from one-line terminal shortcuts.
/// Commands must name a bare .exe discoverable without a shell. Input is bounded,
/// has no command chaining/redirection syntax, and is passed to CreateProcess as a
/// structured argument vector. Thus <c>ssh worker1</c> still reads the user's
/// OpenSSH configuration and .ssh key settings, while no shell evaluates it.
/// </summary>
public static class LocalTerminalLaunchPlanner
{
    /// <summary>Validates persisted syntax without finding or executing a program.</summary>
    public static (LocalTerminalLaunchKind Kind, string CanonicalCommand, string LaunchTitle) ValidateCommand(string command)
    {
        var tokens = Tokenize(command);
        var executable = NormalizeExecutableName(tokens[0]);
        var arguments = tokens.Skip(1).ToArray();
        var kind = ParseSpecialConnectionForm(executable, arguments);
        return (kind, BuildCanonicalCommand(executable, arguments), BuildLaunchTitle(kind, executable, arguments));
    }

    public const int MaximumCommandLength = 2_048;
    public const int MaximumArgumentCount = 32;
    public const int MaximumArgumentLength = 1_024;

    // '%' is intentional: it is literal when no command interpreter receives the
    // command. The rest are rejected so a copied compound shell command cannot
    // become a shortcut that looks like a connection command.
    private const string ShellSyntaxCharacters = "&|;<>`$()";
    private static readonly string[] BlockedExecutableNames =
    [
        "cmd.exe", "powershell.exe", "pwsh.exe", "bash.exe", "sh.exe",
        "zsh.exe", "fish.exe",
    ];
    private static readonly HashSet<char> SshFlagsWithoutValue =
    [
        '4', '6', 'A', 'a', 'C', 'f', 'G', 'g', 'K', 'k', 'M', 'N', 'n',
        'q', 's', 'T', 't', 'V', 'v', 'X', 'x', 'Y', 'y',
    ];
    private static readonly HashSet<char> SshFlagsWithValue =
    [
        'B', 'b', 'c', 'D', 'E', 'e', 'F', 'I', 'i', 'J', 'L', 'l', 'm',
        'O', 'o', 'p', 'Q', 'R', 'S', 'W', 'w',
    ];

    public static LocalTerminalLaunchPlan Create(
        string command,
        ILocalTerminalExecutableResolver? resolver = null)
    {
        var tokens = Tokenize(command);
        var executableName = NormalizeExecutableName(tokens[0]);
        var launchArguments = tokens.Skip(1).ToArray();
        var kind = ParseSpecialConnectionForm(executableName, launchArguments);
        var executable = ResolveAndValidateExecutable(
            (resolver ?? SystemLocalTerminalExecutableResolver.Instance).ResolveExecutable(executableName),
            executableName);
        var canonicalCommand = BuildCanonicalCommand(executableName, launchArguments);
        return new LocalTerminalLaunchPlan(
            kind,
            executable,
            launchArguments,
            canonicalCommand,
            BuildLaunchTitle(kind, executableName, launchArguments));
    }

    public static bool TryCreate(
        string command,
        out LocalTerminalLaunchPlan? plan,
        out string? error,
        ILocalTerminalExecutableResolver? resolver = null)
    {
        try
        {
            plan = Create(command, resolver);
            error = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            plan = null;
            error = exception.Message;
            return false;
        }
        catch (NotSupportedException exception)
        {
            plan = null;
            error = exception.Message;
            return false;
        }
        catch (IOException exception)
        {
            plan = null;
            error = exception.Message;
            return false;
        }
    }

    private static IReadOnlyList<string> Tokenize(string? command)
    {
        var source = command ?? "";
        if (source.Length is < 1 or > MaximumCommandLength ||
            source.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
            throw new ArgumentException("A single-line command up to 2048 characters is required.", nameof(command));
        var input = source.Trim();
        if (input.Length == 0)
            throw new ArgumentException("A single-line command up to 2048 characters is required.", nameof(command));

        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var tokenStarted = false;
        for (var index = 0; index < input.Length;)
        {
            if (!inQuotes && char.IsWhiteSpace(input[index]))
            {
                AddToken(tokens, current, tokenStarted, command);
                tokenStarted = false;
                while (index < input.Length && char.IsWhiteSpace(input[index]))
                    index++;
                continue;
            }

            tokenStarted = true;
            if (input[index] == '\\')
            {
                var slashes = 0;
                while (index < input.Length && input[index] == '\\')
                {
                    slashes++;
                    index++;
                }

                if (index < input.Length && input[index] == '\"')
                {
                    current.Append('\\', slashes / 2);
                    if ((slashes & 1) == 0)
                        inQuotes = !inQuotes;
                    else
                        current.Append('\"');
                    index++;
                }
                else
                {
                    current.Append('\\', slashes);
                }
                continue;
            }

            if (input[index] == '\"')
            {
                inQuotes = !inQuotes;
                index++;
                continue;
            }

            current.Append(input[index++]);
        }

        if (inQuotes)
            throw new ArgumentException("The command contains an unmatched quote.", nameof(command));

        AddToken(tokens, current, tokenStarted, command);
        if (tokens.Count is < 1 or > MaximumArgumentCount)
            throw new ArgumentException($"Use at most {MaximumArgumentCount} command arguments.", nameof(command));
        return tokens;
    }

    private static void AddToken(
        List<string> tokens,
        StringBuilder current,
        bool tokenStarted,
        string? parameterName)
    {
        if (!tokenStarted)
            return;
        if (current.Length == 0)
            throw new ArgumentException("Empty command arguments are not supported.", parameterName);
        if (current.Length > MaximumArgumentLength ||
            current.ToString().IndexOfAny(ShellSyntaxCharacters.ToCharArray()) >= 0)
        {
            throw new ArgumentException(
                "Command arguments must be bounded and cannot contain shell syntax.",
                parameterName);
        }

        tokens.Add(current.ToString());
        current.Clear();
    }

    private static string NormalizeExecutableName(string command)
    {
        if (command.Length is < 1 or > 255 ||
            command.IndexOfAny(['\\', '/', ':']) >= 0 ||
            !command.Equals(Path.GetFileName(command), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Use a bare executable name from PATH, not a path or shell alias.");
        }

        var executable = command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? command
            : command + ".exe";
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            BlockedExecutableNames.Contains(executable, StringComparer.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "Command interpreters are not supported. Use a direct .exe command such as ssh, multipass, or wsl.");
        }

        return executable;
    }

    private static LocalTerminalLaunchKind ParseSpecialConnectionForm(
        string executableName,
        string[] arguments)
    {
        if (executableName.Equals("ssh.exe", StringComparison.OrdinalIgnoreCase))
        {
            ParseSshConnectionArguments(arguments);
            return LocalTerminalLaunchKind.OpenSsh;
        }

        if (executableName.Equals("multipass.exe", StringComparison.OrdinalIgnoreCase) &&
            arguments.Length > 0 &&
            arguments[0].Equals("connect", StringComparison.OrdinalIgnoreCase))
        {
            ParseMultipassConnectionArguments(arguments);
            return LocalTerminalLaunchKind.MultipassConnect;
        }

        return LocalTerminalLaunchKind.DirectExecutable;
    }

    private static void ParseSshConnectionArguments(string[] arguments)
    {
        if (arguments.Length == 0)
            throw new ArgumentException("An SSH host or OpenSSH configuration alias is required.");

        var destinationSeen = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (destinationSeen)
            {
                throw new ArgumentException(
                    "This launcher opens an interactive SSH connection and does not accept a remote command.");
            }

            if (!argument.StartsWith("-", StringComparison.Ordinal) || argument == "-")
            {
                ValidateSshDestination(argument);
                destinationSeen = true;
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Long SSH options are not supported in connection shortcuts.");

            var option = argument[1];
            if (SshFlagsWithoutValue.Contains(option))
            {
                for (var charIndex = 1; charIndex < argument.Length; charIndex++)
                {
                    if (!SshFlagsWithoutValue.Contains(argument[charIndex]))
                        throw new ArgumentException($"SSH option '{argument}' is not a supported connection option.");
                }
                continue;
            }

            if (!SshFlagsWithValue.Contains(option))
                throw new ArgumentException($"SSH option '{argument}' is not a supported connection option.");

            // OpenSSH permits -p2222 and -iC:\\key as well as separated values.
            if (argument.Length > 2)
                continue;
            if (++index >= arguments.Length || arguments[index].StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException($"SSH option '-{option}' needs a value.");
        }

        if (!destinationSeen)
            throw new ArgumentException("An SSH host or OpenSSH configuration alias is required.");
    }

    private static void ParseMultipassConnectionArguments(string[] arguments)
    {
        if (arguments.Length != 2)
        {
            throw new ArgumentException(
                "Use Multipass connection shortcuts in the form 'multipass connect <instance>'.");
        }

        var instance = arguments[1];
        if (instance.Length is < 1 or > 63 ||
            !char.IsLetterOrDigit(instance[0]) ||
            instance.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("The Multipass instance name is invalid.");
        }
    }

    private static void ValidateSshDestination(string destination)
    {
        if (destination.Length is < 1 or > 255 ||
            destination.Any(char.IsWhiteSpace) ||
            destination.StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException("The SSH host or configuration alias is invalid.");
        }
    }

    private static string ResolveAndValidateExecutable(string? resolvedPath, string expectedFileName)
    {
        if (string.IsNullOrWhiteSpace(resolvedPath) || !Path.IsPathFullyQualified(resolvedPath))
            throw new IOException($"{expectedFileName} was not found. Install it or add its folder to PATH.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(resolvedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new IOException($"The resolved {expectedFileName} path is invalid.", exception);
        }

        if (!Path.IsPathFullyQualified(fullPath) ||
            !Path.GetFileName(fullPath).Equals(expectedFileName, StringComparison.OrdinalIgnoreCase) ||
            !fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath))
        {
            throw new IOException($"An existing absolute {expectedFileName} executable is required.");
        }

        return fullPath;
    }

    private static string BuildCanonicalCommand(string executableName, IReadOnlyList<string> arguments)
    {
        var command = new StringBuilder(executableName[..^4]);
        foreach (var argument in arguments)
        {
            command.Append(' ').Append(
                argument.Any(char.IsWhiteSpace) || argument.Contains('"')
                    ? LocalTerminalLaunchPlan.QuoteWindowsArgument(argument)
                    : argument);
        }
        return command.ToString();
    }

    private static string BuildLaunchTitle(
        LocalTerminalLaunchKind kind,
        string executableName,
        IReadOnlyList<string> arguments) => kind switch
    {
        LocalTerminalLaunchKind.OpenSsh => $"SSH · {arguments[^1]}",
        LocalTerminalLaunchKind.MultipassConnect => $"Multipass · {arguments[1]}",
        _ => Path.GetFileNameWithoutExtension(executableName),
    };

    private sealed class SystemLocalTerminalExecutableResolver : ILocalTerminalExecutableResolver
    {
        public static SystemLocalTerminalExecutableResolver Instance { get; } = new();

        public string? ResolveExecutable(string executableFileName)
        {
            if (executableFileName.Equals("ssh.exe", StringComparison.OrdinalIgnoreCase))
            {
                var systemOpenSsh = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "OpenSSH",
                    executableFileName);
                if (File.Exists(systemOpenSsh))
                    return systemOpenSsh;
            }

            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            var scanned = 0;
            foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (++scanned > 256 || entry.Length > 4_096)
                    break;

                var directory = entry.Trim().Trim('\"');
                if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))
                    continue;
                try
                {
                    var candidate = Path.Combine(directory, executableFileName);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Ignore malformed PATH entries; an existing safe candidate is
                    // still required before the planner will create a launch plan.
                }
            }

            return null;
        }
    }
}
