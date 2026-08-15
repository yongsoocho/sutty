using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace sutty.UI.Helpers;

/// <summary>
/// Extracts output between standalone shell marker lines. Marker text appearing in the
/// echoed <c>echo marker</c> command is ignored, and UTF-8/marker splits across packets
/// are supported.
/// </summary>
public sealed class TerminalBroadcastCapture
{
    private const int MaxCapturedCharacters = 256 * 1024;
    private readonly string _beginMarker;
    private readonly string _endMarker;
    private readonly object _gate = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _buffer = new();
    private readonly TaskCompletionSource<string> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started;
    private bool _truncated;

    public TerminalBroadcastCapture(string beginMarker, string endMarker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(beginMarker);
        ArgumentException.ThrowIfNullOrWhiteSpace(endMarker);
        _beginMarker = beginMarker;
        _endMarker = endMarker;
    }

    public Task<string> Completion => _completion.Task;

    /// <summary>Returns the output received so far without waiting for the end marker.</summary>
    public string Snapshot()
    {
        lock (_gate)
        {
            if (!_started)
                return string.Empty;

            var output = CleanForPreview(_buffer.ToString());
            if (_truncated)
                output += "\n[output truncated by Sutty]";
            return output;
        }
    }

    /// <summary>Stops a pending capture when its terminal closes or fails.</summary>
    public void Fail(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_gate)
            _completion.TrySetException(error);
    }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (_completion.Task.IsCompleted || bytes.IsEmpty)
            return;

        lock (_gate)
        {
            if (_completion.Task.IsCompleted)
                return;

            var characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var count = _decoder.GetChars(bytes, characters, flush: false);
            _buffer.Append(characters, 0, count);

            if (!_started)
            {
                var start = FindStandaloneMarker(_buffer, _beginMarker);
                if (start < 0)
                {
                    TrimPrefix(_beginMarker.Length + 512);
                    return;
                }

                RemoveThroughMarkerLine(start, _beginMarker.Length);
                _started = true;
            }

            var end = FindStandaloneMarker(_buffer, _endMarker);
            if (end >= 0)
            {
                var output = CleanForPreview(_buffer.ToString(0, end));
                if (_truncated)
                    output += "\n… [output truncated by Sutty]";
                _completion.TrySetResult(output);
                return;
            }

            if (_buffer.Length > MaxCapturedCharacters + _endMarker.Length + 512)
            {
                var tailToKeep = _endMarker.Length + 512;
                _buffer.Remove(
                    MaxCapturedCharacters,
                    _buffer.Length - MaxCapturedCharacters - tailToKeep);
                _truncated = true;
            }
        }
    }

    private static int FindStandaloneMarker(StringBuilder buffer, string marker)
    {
        var text = buffer.ToString();
        var searchFrom = 0;
        while (searchFrom < text.Length)
        {
            var index = text.IndexOf(marker, searchFrom, StringComparison.Ordinal);
            if (index < 0)
                return -1;

            var afterIndex = index + marker.Length;
            var lineStart = index;
            while (lineStart > 0 && text[lineStart - 1] is not ('\r' or '\n'))
                lineStart--;
            var lineEnd = afterIndex;
            while (lineEnd < text.Length && text[lineEnd] is not ('\r' or '\n'))
                lineEnd++;

            // PSReadLine and themed shells may wrap marker output in SGR sequences.
            // Compare the complete logical line after removing terminal controls so an
            // echoed command ("PS> echo marker") still cannot impersonate marker output.
            var logicalLine = StripTerminalControlSequences(
                text[lineStart..lineEnd]).Trim();
            if (string.Equals(logicalLine, marker, StringComparison.Ordinal))
                return index;

            searchFrom = index + marker.Length;
        }

        return -1;
    }

    private void RemoveThroughMarkerLine(int markerStart, int markerLength)
    {
        var removeThrough = markerStart + markerLength;
        while (removeThrough < _buffer.Length && _buffer[removeThrough] is '\r' or '\n')
            removeThrough++;
        _buffer.Remove(0, removeThrough);
    }

    private void TrimPrefix(int keep)
    {
        if (_buffer.Length > keep)
            _buffer.Remove(0, _buffer.Length - keep);
    }

    private string CleanForPreview(string value)
    {
        var normalized = StripTerminalControlSequences(value)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return string.Join('\n', normalized
            .Split('\n')
            .Where(line => !line.Contains($"echo {_endMarker}", StringComparison.OrdinalIgnoreCase)))
            .Trim('\n');
    }

    private static string StripTerminalControlSequences(string value)
    {
        var result = new StringBuilder(value.Length);
        var state = EscapeState.Ground;
        foreach (var character in value)
        {
            switch (state)
            {
                case EscapeState.Ground:
                    if (character == '\x1b')
                        state = EscapeState.Escape;
                    else if (character is '\r' or '\n' or '\t' || !char.IsControl(character))
                        result.Append(character);
                    break;
                case EscapeState.Escape:
                    state = character switch
                    {
                        '[' => EscapeState.Csi,
                        ']' => EscapeState.Osc,
                        '(' or ')' or '*' or '+' or '%' => EscapeState.EscapeIntermediate,
                        _ => EscapeState.Ground,
                    };
                    break;
                case EscapeState.Csi:
                    if (character is >= '@' and <= '~')
                        state = EscapeState.Ground;
                    break;
                case EscapeState.Osc:
                    if (character == '\a')
                        state = EscapeState.Ground;
                    else if (character == '\x1b')
                        state = EscapeState.OscEscape;
                    break;
                case EscapeState.OscEscape:
                    state = character == '\\' ? EscapeState.Ground : EscapeState.Osc;
                    break;
                case EscapeState.EscapeIntermediate:
                    state = EscapeState.Ground;
                    break;
            }
        }

        return result.ToString();
    }

    private enum EscapeState
    {
        Ground,
        Escape,
        Csi,
        Osc,
        OscEscape,
        EscapeIntermediate,
    }
}
