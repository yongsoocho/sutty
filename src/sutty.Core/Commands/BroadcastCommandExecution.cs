using sutty.Core.Sessions;
using sutty.Core.Terminal;

namespace sutty.Core.Commands;

public enum BroadcastCommandMode
{
    SshExec,
    TerminalInput,
}

public enum BroadcastCommandOutcome
{
    NotStarted,
    Completed,
    CompletedWithoutExitCode,
    Failed,
    ResponseUnknown,
    CancellationRequested,
}

public sealed record BroadcastCommandResult(
    BroadcastCommandOutcome Outcome,
    CommandExecutionResult? Execution = null,
    string? Error = null);

/// <summary>
/// Bounds the user's wait independently of transport cooperation. An interrupted wait
/// never proves that the server stopped executing, and this runner never retries.
/// </summary>
public static class BroadcastCommandExecution
{
    /// <summary>Selection expresses intent; terminal-input dispatch still needs separate approval.</summary>
    public static bool CanSelectTarget(SessionState? sshState, TerminalState? terminalState) =>
        sshState is { } state ? state == SessionState.Connected : terminalState == TerminalState.Open;

    /// <summary>Terminal input requires a fresh explicit approval in addition to target selection.</summary>
    public static Task<BroadcastCommandResult> RunApprovedAsync(
        Func<CancellationToken, Task<CommandExecutionResult>> execute,
        TimeSpan timeout,
        BroadcastCommandMode mode,
        bool terminalInputApproved,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == BroadcastCommandMode.TerminalInput && !terminalInputApproved)
            return Task.FromResult(new BroadcastCommandResult(BroadcastCommandOutcome.NotStarted));
        return RunAsync(execute, timeout, cancellationToken);
    }

    public static async Task<BroadcastCommandResult> RunAsync(
        Func<CancellationToken, Task<CommandExecutionResult>> execute,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execute);
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (cancellationToken.IsCancellationRequested)
            return new(BroadcastCommandOutcome.NotStarted);

        // Transport callbacks may block while sending an SSH signal. Never link them
        // to the token/timer that completes the user's wait.
        var transportCancellation = new CancellationTokenSource();
        var executionToken = transportCancellation.Token;
        Task cancellationCallbacks = Task.CompletedTask;
        // 0 = queued, 1 = dispatch claimed, 2 = cancelled before dispatch.
        var dispatchState = 0;
        Task<CommandExecutionResult>? pending = null;
        try
        {
            // SSH channel creation can block before its async method returns a Task.
            // Keep that synchronous prefix off the caller/UI thread as well.
            pending = Task.Run(async () =>
            {
                if (Interlocked.CompareExchange(ref dispatchState, 1, 0) != 0)
                    throw new OperationCanceledException(executionToken);
                executionToken.ThrowIfCancellationRequested();
                return await execute(executionToken).ConfigureAwait(false);
            }, executionToken);
            var result = await pending.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (string.Equals(result.ExitSignal, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                return new(cancellationToken.IsCancellationRequested
                    ? BroadcastCommandOutcome.CancellationRequested
                    : BroadcastCommandOutcome.ResponseUnknown, result);
            return new(result.Succeeded
                ? BroadcastCommandOutcome.Completed
                : result.ExitCode.HasValue || !string.IsNullOrEmpty(result.ExitSignal)
                    ? BroadcastCommandOutcome.Failed
                    : BroadcastCommandOutcome.CompletedWithoutExitCode, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Atomically prevent a queued worker from dispatching after NotStarted
            // has been shown. Once dispatch is claimed, termination is uncertain.
            var notStarted = Interlocked.CompareExchange(ref dispatchState, 2, 0) == 0;
            cancellationCallbacks = transportCancellation.CancelAsync();
            return new(notStarted ? BroadcastCommandOutcome.NotStarted
                : BroadcastCommandOutcome.CancellationRequested);
        }
        catch (TimeoutException)
        {
            var notStarted = Interlocked.CompareExchange(ref dispatchState, 2, 0) == 0;
            cancellationCallbacks = transportCancellation.CancelAsync();
            return new(notStarted ? BroadcastCommandOutcome.NotStarted
                : BroadcastCommandOutcome.ResponseUnknown);
        }
        catch (Exception error)
        {
            // A broken channel may have delivered the command before it failed.
            return new(BroadcastCommandOutcome.ResponseUnknown, Error: error.Message);
        }
        finally
        {
            _ = ObserveCompletionAndDisposeAsync(pending, cancellationCallbacks, transportCancellation);
        }
    }

    private static async Task ObserveCompletionAndDisposeAsync(
        Task? pending, Task cancellationCallbacks, CancellationTokenSource transportCancellation)
    {
        // Observe both independently, even if one never finishes. Dispose only after
        // neither the transport nor its cancellation callbacks can still use the CTS.
        await Task.WhenAll(ObserveAsync(pending ?? Task.CompletedTask), ObserveAsync(cancellationCallbacks))
            .ConfigureAwait(false);
        transportCancellation.Dispose();
    }

    private static async Task ObserveAsync(Task pending)
    {
        try { await pending.ConfigureAwait(false); }
        catch { /* The owning result already says response unknown; never replay or update it. */ }
    }
}
