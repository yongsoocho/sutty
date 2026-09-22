using System.Text;

namespace sutty.Core.Terminal;

/// <summary>
/// Reads only the private OSC emitted by this child shell's prompt. The per-host
/// nonce prevents normal OSC 7/title output and other sessions from changing the
/// local file browser. It is not a security boundary against the child itself.
/// </summary>
internal sealed class LocalWorkingDirectoryTracker
{
    private const int MaxPathCharacters = 32767;
    private const string Base64Prefix = "base64;";
    // One UTF-16 code unit needs at most three UTF-8 bytes, or four base64 bytes.
    private static readonly int MaxPayloadBytes = Base64Prefix.Length + 4 * MaxPathCharacters;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly byte[] _prefix;
    private readonly Action<string> _report;
    private readonly List<byte> _payload = [];
    private State _state;
    private int _prefixOffset;
    private bool _ignored;

    public LocalWorkingDirectoryTracker(string nonce, Action<string> report)
    {
        _prefix = Encoding.ASCII.GetBytes($"777;sutty-cwd;{nonce};");
        _report = report;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            switch (_state)
            {
                case State.Text:
                    if (value == 0x1b) _state = State.Escape;
                    break;
                case State.Escape:
                    if (value == (byte)']')
                    {
                        _state = State.Osc;
                        _prefixOffset = 0;
                        _payload.Clear();
                        _ignored = false;
                    }
                    else if (value != 0x1b)
                        _state = State.Text;
                    break;
                case State.Osc:
                    if (value == 0x07) Complete();
                    else if (value == 0x1b) _state = State.OscEscape;
                    else if (!_ignored)
                    {
                        if (_prefixOffset < _prefix.Length)
                            _ignored = value != _prefix[_prefixOffset++];
                        else if (_payload.Count < MaxPayloadBytes)
                            _payload.Add(value);
                        else
                            _ignored = true;
                    }
                    break;
                case State.OscEscape:
                    if (value == (byte)'\\') Complete();
                    else
                    {
                        // An ESC inside a path is invalid. Consume the remainder
                        // of the OSC without interpreting it as a second report.
                        _ignored = true;
                        _state = value == 0x1b ? State.OscEscape : State.Osc;
                        if (value == 0x07) Complete();
                    }
                    break;
            }
        }
    }

    private void Complete()
    {
        _state = State.Text;
        if (_ignored || _prefixOffset != _prefix.Length)
            return;

        string path;
        try
        {
            path = StrictUtf8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_payload));
            if (path.StartsWith(Base64Prefix, StringComparison.Ordinal))
            {
                // Console.Write in Windows PowerShell uses its current legacy
                // output encoding. ASCII base64 protects the path without
                // changing that encoding for the user's commands. CMD writes
                // its Unicode $P directly and retains the raw-path protocol.
                var encoded = path[Base64Prefix.Length..];
                if (encoded.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not '+' and not '/' and not '='))
                    return;
                path = StrictUtf8.GetString(Convert.FromBase64String(encoded));
            }
        }
        catch (Exception error) when (error is DecoderFallbackException or FormatException)
        {
            return;
        }

        // An empty report explicitly means a non-filesystem PowerShell provider.
        // Never trim or unescape: spaces, semicolons, and Unicode belong to paths.
        if (path.Length > MaxPathCharacters || path.Any(char.IsControl) ||
            (path.Length != 0 && !Path.IsPathFullyQualified(path)))
            return;
        _report(path);
    }

    private enum State { Text, Escape, Osc, OscEscape }
}
