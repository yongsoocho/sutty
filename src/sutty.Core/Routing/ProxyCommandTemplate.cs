using System.Globalization;
using System.Text;

namespace sutty.Core.Routing;

/// <summary>
/// Expands the OpenSSH-style placeholders supported by Sutty. Endpoint values are
/// always quoted and are rejected when they could alter the surrounding cmd.exe
/// command. The command template itself remains an explicitly user-authorized command.
/// </summary>
public static class ProxyCommandTemplate
{
    private const int MaximumCommandLength = 4_096;
    private const string UnsafeSubstitutionCharacters = "\"%!&|<>^()";

    public static void Validate(string? template)
    {
        var command = template?.Trim() ?? "";
        if (command.Length is < 1 or > MaximumCommandLength ||
            command.Any(character => character is '\0' or '\r' or '\n'))
        {
            throw new RoutePolicyViolationException(
                "A valid single-line ProxyCommand is required.");
        }
    }

    public static string Expand(
        string template,
        string targetHost,
        int targetPort,
        string username)
    {
        Validate(template);
        if (targetPort is < 1 or > 65_535)
            throw new RoutePolicyViolationException("The ProxyCommand target port is invalid.");

        var quotedHost = QuoteEndpointValue(targetHost, "target host", rejectWhitespace: true);
        var quotedUser = QuoteEndpointValue(username, "username", rejectWhitespace: false);
        var port = targetPort.ToString(CultureInfo.InvariantCulture);
        var command = template.Trim();
        var expanded = new StringBuilder(command.Length + quotedHost.Length + quotedUser.Length);

        for (var index = 0; index < command.Length; index++)
        {
            var current = command[index];
            if (current != '%' || index + 1 >= command.Length)
            {
                expanded.Append(current);
                continue;
            }

            var token = command[++index];
            switch (token)
            {
                case 'h':
                    expanded.Append(quotedHost);
                    break;
                case 'p':
                    expanded.Append(port);
                    break;
                case 'r':
                    expanded.Append(quotedUser);
                    break;
                case '%':
                    expanded.Append('%');
                    break;
                default:
                    // Keep unsupported percent expressions intact. ProxyCommand is an
                    // explicit shell command and may legitimately contain cmd.exe
                    // environment variables such as %PATH%.
                    expanded.Append('%').Append(token);
                    break;
            }
        }

        return expanded.ToString();
    }

    private static string QuoteEndpointValue(
        string? value,
        string fieldName,
        bool rejectWhitespace)
    {
        var normalized = value?.Trim() ?? "";
        if (normalized.Length is < 1 or > 255 ||
            normalized.Any(char.IsControl) ||
            (rejectWhitespace && normalized.Any(char.IsWhiteSpace)) ||
            normalized.IndexOfAny(UnsafeSubstitutionCharacters.ToCharArray()) >= 0)
        {
            throw new RoutePolicyViolationException(
                $"The ProxyCommand {fieldName} contains unsafe shell characters.");
        }

        return $"\"{normalized}\"";
    }
}
