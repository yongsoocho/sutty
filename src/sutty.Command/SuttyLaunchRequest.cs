namespace sutty.Command;

public enum SuttyLaunchAction
{
    Default,
    OpenSavedHost,
    ShowHelp,
    Invalid,
}

/// <summary>A credential-free request parsed from Sutty's process arguments.</summary>
public sealed record SuttyLaunchRequest(
    SuttyLaunchAction Action,
    string SavedHostReference = "",
    string ErrorCode = "")
{
    public static SuttyLaunchRequest Default { get; } = new(SuttyLaunchAction.Default);
}

/// <summary>
/// Parses the small supported GUI command line. Authentication values and raw host
/// endpoints are deliberately not accepted; --host resolves an existing Saved Host.
/// </summary>
public static class SuttyLaunchRequestParser
{
    public static SuttyLaunchRequest Parse(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return SuttyLaunchRequest.Default;

        if (!TryTokenize(arguments, out var tokens) || tokens.Count == 0)
            return new SuttyLaunchRequest(SuttyLaunchAction.Invalid, ErrorCode: "INVALID_QUOTES");

        if (tokens.Count == 1 && tokens[0] is "--help" or "-h" or "/?")
            return new SuttyLaunchRequest(SuttyLaunchAction.ShowHelp);

        string reference;
        if (tokens.Count == 2 && string.Equals(tokens[0], "--host", StringComparison.OrdinalIgnoreCase))
        {
            reference = tokens[1];
        }
        else if (tokens.Count == 1 && tokens[0].StartsWith("--host=", StringComparison.OrdinalIgnoreCase))
        {
            reference = tokens[0]["--host=".Length..];
        }
        else
        {
            return new SuttyLaunchRequest(SuttyLaunchAction.Invalid, ErrorCode: "UNSUPPORTED_ARGUMENTS");
        }

        reference = reference.Trim();
        if (reference.Length is < 1 or > 128 || reference.Any(char.IsControl))
            return new SuttyLaunchRequest(SuttyLaunchAction.Invalid, ErrorCode: "INVALID_HOST_REFERENCE");
        return new SuttyLaunchRequest(SuttyLaunchAction.OpenSavedHost, reference);
    }

    private static bool TryTokenize(string arguments, out List<string> tokens)
    {
        tokens = [];
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            var character = arguments[index];
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(character);
        }

        if (quoted)
            return false;
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return true;
    }
}
