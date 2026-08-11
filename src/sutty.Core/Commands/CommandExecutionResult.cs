namespace sutty.Core.Commands;

/// <summary>
/// Immutable result of one non-interactive SSH exec channel.
/// Standard output and standard error stay separate so callers never lose diagnostics.
/// </summary>
public sealed record CommandExecutionResult(
    string Command,
    string StandardOutput,
    string StandardError,
    int? ExitCode,
    string? ExitSignal,
    DateTimeOffset StartedAt,
    TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0 && string.IsNullOrEmpty(ExitSignal);

    /// <summary>Compatibility display text while REPL consumers migrate to structured fields.</summary>
    public string CombinedOutput
    {
        get
        {
            if (string.IsNullOrEmpty(StandardOutput)) return StandardError;
            if (string.IsNullOrEmpty(StandardError)) return StandardOutput;
            return StandardOutput.EndsWith('\n')
                ? StandardOutput + StandardError
                : StandardOutput + Environment.NewLine + StandardError;
        }
    }
}
