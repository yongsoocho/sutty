namespace sutty.Core.Terminal;

/// <summary>Prepares POSIX shell input only. Never executes, reads terminal output, or probes a shell.</summary>
public static class TerminalDirectoryCommand
{
    public static string Prepare(string absoluteRemotePath)
    {
        ValidateAbsolutePath(absoluteRemotePath);
        // A leading slash cannot be a cd option. Single quotes protect spaces, shell
        // substitutions, semicolons, glob characters and Unicode names without changing them.
        return "cd '" + absoluteRemotePath.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    public static void ValidateAbsolutePath(string absoluteRemotePath)
    {
        if (string.IsNullOrEmpty(absoluteRemotePath) || absoluteRemotePath[0] != '/' ||
            absoluteRemotePath.Length > 32_768 || absoluteRemotePath.Any(char.IsControl) ||
            absoluteRemotePath.Contains('\u2028') || absoluteRemotePath.Contains('\u2029'))
            throw new ArgumentException("Enter an absolute remote path without control characters.",
                nameof(absoluteRemotePath));
    }
}
