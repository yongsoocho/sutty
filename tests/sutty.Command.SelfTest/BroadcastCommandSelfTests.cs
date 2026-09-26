using sutty.Core.Commands;
using sutty.Core.Sessions;
using sutty.Core.Terminal;

internal static class BroadcastCommandSelfTests
{
    public static async Task RunAsync()
    {
        await MixedTargetsRequireFreshTerminalApprovalAsync();
        var zero = await RunResultAsync(Result(0));
        Assert(zero.Outcome == BroadcastCommandOutcome.Completed, "exit zero completes");
        var nonzero = await RunResultAsync(Result(7));
        Assert(nonzero.Outcome == BroadcastCommandOutcome.Failed && nonzero.Execution?.ExitCode == 7,
            "nonzero exit remains a failure with its exact code");
        var signal = await RunResultAsync(Result(0) with { ExitSignal = "TERM" });
        Assert(signal.Outcome == BroadcastCommandOutcome.Failed, "a signal does not report success");
        var unknown = await RunResultAsync(Result(null));
        Assert(unknown.Outcome == BroadcastCommandOutcome.CompletedWithoutExitCode &&
               unknown.Execution?.Succeeded == false, "no exit code is never success");

        var failure = await BroadcastCommandExecution.RunAsync(
            _ => Task.FromException<CommandExecutionResult>(new IOException("synthetic disconnect")),
            TimeSpan.FromSeconds(2));
        Assert(failure.Outcome == BroadcastCommandOutcome.ResponseUnknown,
            "transport failure does not claim the remote command was not run");

        var calls = 0;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var skipped = await BroadcastCommandExecution.RunAsync(_ =>
            {
                calls++;
                return Task.FromResult(Result(0));
            }, TimeSpan.FromSeconds(2), cancelled.Token);
            Assert(skipped.Outcome == BroadcastCommandOutcome.NotStarted && calls == 0,
                "cancellation before dispatch executes nothing");
        }

        // The transport deliberately ignores cancellation: waiting must still be bounded.
        var late = new TaskCompletionSource<CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken transportToken = default;
        var timedOut = await BroadcastCommandExecution.RunAsync(token =>
        {
            calls++;
            transportToken = token;
            return late.Task;
        }, TimeSpan.FromMilliseconds(200)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert(timedOut.Outcome == BroadcastCommandOutcome.ResponseUnknown && transportToken.IsCancellationRequested,
            "timeout ends waiting and requests cancellation without claiming remote termination");
        late.SetResult(Result(0));
        Assert(timedOut.Outcome == BroadcastCommandOutcome.ResponseUnknown && calls == 1,
            "late success does not rewrite uncertainty or replay the command");

        using (var stop = new CancellationTokenSource())
        {
            var uncooperative = new TaskCompletionSource<CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiting = BroadcastCommandExecution.RunAsync(_ =>
            {
                entered.SetResult();
                return uncooperative.Task;
            },
                TimeSpan.FromSeconds(20), stop.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            var stopped = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(stopped.Outcome == BroadcastCommandOutcome.CancellationRequested,
                "manual cancellation stops waiting even if the transport ignores it");
            uncooperative.SetException(new IOException("synthetic late disconnect"));
        }

        await SynchronousDispatchCannotBlockCallerOrOtherTargetsAsync();
        await BlockingTransportCancellationCannotDelayWaitAsync(manualCancellation: false);
        await BlockingTransportCancellationCannotDelayWaitAsync(manualCancellation: true);

        var outputs = await Task.WhenAll(
            RunResultAsync(Result(0)),
            BroadcastCommandExecution.RunAsync(_ => Task.FromException<CommandExecutionResult>(
                new IOException("one host failed")), TimeSpan.FromSeconds(2)));
        Assert(outputs[0].Outcome == BroadcastCommandOutcome.Completed &&
               outputs[1].Outcome == BroadcastCommandOutcome.ResponseUnknown,
            "one host failure does not hide other host results");
        Console.WriteLine("Broadcast command safety self-tests passed.");
    }

    private static async Task MixedTargetsRequireFreshTerminalApprovalAsync()
    {
        Assert(BroadcastCommandExecution.CanSelectTarget(SessionState.Connected, null) &&
               BroadcastCommandExecution.CanSelectTarget(null, TerminalState.Open),
            "connected SSH and running local/external terminals are both selectable");
        Assert(!BroadcastCommandExecution.CanSelectTarget(null, null) &&
               !BroadcastCommandExecution.CanSelectTarget(SessionState.Connecting, null) &&
               !BroadcastCommandExecution.CanSelectTarget(null, TerminalState.Opening) &&
               !BroadcastCommandExecution.CanSelectTarget(null, TerminalState.Closed),
            "placeholders, connecting SSH and stopped/starting terminals cannot be selected");

        var inputCount = 0;
        Task<CommandExecutionResult> SendInput(CancellationToken _) {
            Interlocked.Increment(ref inputCount);
            return Task.FromResult(Result(null));
        }
        var denied = await BroadcastCommandExecution.RunApprovedAsync(SendInput,
            TimeSpan.FromSeconds(2), BroadcastCommandMode.TerminalInput, terminalInputApproved: false);
        Assert(denied.Outcome == BroadcastCommandOutcome.NotStarted && inputCount == 0,
            "selecting a running terminal alone must not transmit even one input byte");

        var mixed = await Task.WhenAll(
            BroadcastCommandExecution.RunApprovedAsync(_ => Task.FromResult(Result(0)),
                TimeSpan.FromSeconds(2), BroadcastCommandMode.SshExec, terminalInputApproved: true),
            BroadcastCommandExecution.RunApprovedAsync(SendInput,
                TimeSpan.FromSeconds(2), BroadcastCommandMode.TerminalInput, terminalInputApproved: true));
        Assert(inputCount == 1 && mixed[0].Outcome == BroadcastCommandOutcome.Completed &&
               mixed[1].Outcome == BroadcastCommandOutcome.CompletedWithoutExitCode,
            "an approved mixed batch dispatches terminal input once and retains its unknown exit code");

        var nextAttempt = await BroadcastCommandExecution.RunApprovedAsync(SendInput,
            TimeSpan.FromSeconds(2), BroadcastCommandMode.TerminalInput, terminalInputApproved: false);
        Assert(inputCount == 1 && nextAttempt.Outcome == BroadcastCommandOutcome.NotStarted,
            "a previous batch approval cannot authorize the next terminal-input attempt");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var stopped = await BroadcastCommandExecution.RunApprovedAsync(SendInput,
            TimeSpan.FromSeconds(2), BroadcastCommandMode.TerminalInput, true, cancelled.Token);
        Assert(inputCount == 1 && stopped.Outcome == BroadcastCommandOutcome.NotStarted,
            "approved terminal input cancelled before dispatch is never sent later");
    }

    private static async Task SynchronousDispatchCannotBlockCallerOrOtherTargetsAsync()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var blocked = BroadcastCommandExecution.RunAsync(_ =>
            {
                entered.SetResult();
                // Models channel.Open/SendExecRequest stalling before returning a Task.
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Synthetic dispatch was not released.");
                returned.SetResult();
                return Task.FromResult(Result(0));
            }, TimeSpan.FromMilliseconds(200));
            Assert(watch.Elapsed < TimeSpan.FromSeconds(1),
                "a synchronous transport prefix must not block the caller/UI thread");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var other = await RunResultAsync(Result(0)).WaitAsync(TimeSpan.FromSeconds(5));
            var expired = await blocked.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(other.Outcome == BroadcastCommandOutcome.Completed &&
                   expired.Outcome == BroadcastCommandOutcome.ResponseUnknown && !returned.Task.IsCompleted,
                "other targets and the deadline finish while synchronous dispatch remains blocked");
            release.Set();
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(expired.Outcome == BroadcastCommandOutcome.ResponseUnknown,
                "a late synchronous-dispatch completion cannot replace the uncertain result");
        }
        finally
        {
            release.Set();
        }
    }

    private static async Task BlockingTransportCancellationCannotDelayWaitAsync(bool manualCancellation)
    {
        using var releaseCallback = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new TaskCompletionSource<CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        try
        {
            var waiting = BroadcastCommandExecution.RunAsync(token =>
            {
                registration = token.Register(() =>
                {
                    callbackEntered.TrySetResult();
                    // Models SSH.NET waiting for a reply or blocking in a signal write.
                    releaseCallback.Wait(TimeSpan.FromSeconds(5));
                    callbackExited.TrySetResult();
                    throw new IOException("Synthetic cancellation callback error must be observed.");
                });
                registered.SetResult();
                return transport.Task;
            }, manualCancellation ? TimeSpan.FromSeconds(20) : TimeSpan.FromMilliseconds(200), stop.Token);
            await registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (manualCancellation) await stop.CancelAsync().WaitAsync(TimeSpan.FromSeconds(1));
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var outcome = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
            Assert(!callbackExited.Task.IsCompleted && outcome.Outcome == (manualCancellation
                    ? BroadcastCommandOutcome.CancellationRequested
                    : BroadcastCommandOutcome.ResponseUnknown),
                "timeout/manual wait ends while a transport cancellation callback remains blocked");
        }
        finally
        {
            releaseCallback.Set();
            transport.TrySetResult(Result(0));
            if (callbackEntered.Task.IsCompleted)
                await callbackExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            registration.Dispose();
        }
    }

    private static Task<BroadcastCommandResult> RunResultAsync(CommandExecutionResult result) =>
        BroadcastCommandExecution.RunAsync(_ => Task.FromResult(result), TimeSpan.FromSeconds(2));

    private static CommandExecutionResult Result(int? exitCode) =>
        new("synthetic command", "stdout", "stderr", exitCode, null,
            DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(1));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
