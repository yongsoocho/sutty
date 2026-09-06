using System.Diagnostics;
using System.Text;

namespace sutty.Core.Sftp;

public static class ExternalEditorCommand
{
    public static ProcessStartInfo Create(string executable, string arguments, string localFile)
    {
        if (string.IsNullOrWhiteSpace(executable))
            executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!Path.IsPathFullyQualified(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new ArgumentException("Choose an existing editor .exe using an absolute path.");
        arguments = string.IsNullOrWhiteSpace(arguments) ? "{file}" : arguments;
        if (arguments.Length > 4096 || arguments.Any(char.IsControl) || !arguments.Contains("{file}", StringComparison.Ordinal))
            throw new ArgumentException("Editor arguments must contain {file} and no control characters.");
        // {file} includes its own Windows quoting; also tolerate the common quoted placeholder.
        arguments = arguments.Replace("\"{file}\"", "{file}", StringComparison.Ordinal)
            .Replace("{file}", QuoteArgument(Path.GetFullPath(localFile)), StringComparison.Ordinal);
        return new ProcessStartInfo(executable)
        {
            Arguments = arguments,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(localFile))!,
        };
    }

    public static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1);
            else result.Append('\\', slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
