using System.Globalization;
using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Core.Security;
using sutty.Core.Sessions;
using sutty.Core.Sftp;

internal static class LoopbackTransferTests
{
    // The fixture starts a fresh process for each attempt. Only the actual files and the
    // application's on-disk checkpoint are shared between an interruption and its resume.
    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 6 || args[0] is not ("upload" or "download") ||
            args[1] is not ("direct" or "jump") || args[2] is not ("interrupt" or "resume"))
            throw new ArgumentException("Expected direction, route, action, local file, length and saved offset.");
        var upload = args[0] == "upload";
        var viaJump = args[1] == "jump";
        var local = args[3];
        var length = long.Parse(args[4], CultureInfo.InvariantCulture);
        var savedOffset = long.Parse(args[5], CultureInfo.InvariantCulture);
        var remote = "/" + Path.GetFileName(local);
        var session = CreateSession(viaJump);
        try
        {
            await session.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(45));
            Require(session.State == SessionState.Connected && session.SftpState == SftpConnectionState.Ready,
                $"SSH/SFTP connection failed ({session.LastError}, {session.LastSftpError}).");
            var command = await session.ExecuteCommandAsync("sutty-test-endpoint");
            Require(command.Succeeded && command.StandardOutput.Trim() == "destination",
                "SSH did not reach the destination.");
            var options = new SftpTransferOptions
            {
                ConflictPolicy = SftpConflictPolicy.Overwrite, VerifyChecksum = true, Resume = true, RetryEnabled = false,
            };
            Task<SftpTransferResult> Transfer(SftpTransferOptions settings,
                IProgress<SftpTransferProgress>? progress = null, CancellationToken token = default) => upload
                ? session.Sftp.UploadPathAsync(local, remote, settings, progress, token)
                : session.Sftp.DownloadPathAsync(remote, local, settings, progress, token);

            if (args[2] == "interrupt")
            {
                using var cancel = new CancellationTokenSource();
                using var release = new ManualResetEventSlim();
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var progress = new InlineProgress<SftpTransferProgress>(item =>
                {
                    if (item.Phase == SftpTransferPhase.Transferring && item.BytesTransferred >= 4 * 1024 * 1024 &&
                        started.TrySetResult() && !release.Wait(TimeSpan.FromSeconds(45)))
                        throw new TimeoutException("The browse operation did not release the transfer.");
                });
                var transfer = Task.Run(() => Transfer(options, progress, cancel.Token));
                try
                {
                    var first = await Task.WhenAny(started.Task, transfer).WaitAsync(TimeSpan.FromSeconds(45));
                    if (first == transfer)
                    {
                        await transfer;
                        throw new InvalidOperationException("Transfer finished before reaching the pause point.");
                    }
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var entries = await session.Sftp.ListDirectoryAsync("/browse-check", timeout.Token);
                    Require(entries.Any(entry => entry.Name == "확인.txt") && !transfer.IsCompleted,
                        "Listing another directory must finish while the transfer owns its SFTP connection.");
                    Console.WriteLine($"Listed an independent folder during {args[0]} via {args[1]}.");
                }
                finally
                {
                    cancel.Cancel();
                    release.Set();
                    try { await transfer; }
                    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
                }
                return;
            }

            var result = await Transfer(options);
            Require(savedOffset > 0 && savedOffset < length && result.ResumedBytes == savedOffset,
                $"Expected resume offset {savedOffset}, got {result.ResumedBytes}.");
            Require(result.FilesTransferred == 1 && result.FilesSkipped == 0 && result.BytesTransferred == length &&
                string.Equals(result.Sha256, Setting("EXPECTED_HASH"), StringComparison.OrdinalIgnoreCase),
                "Final size, checksum, or transferred file count did not match.");
            var skipped = await Transfer(options with { ConflictPolicy = SftpConflictPolicy.Skip });
            Require(skipped.FilesSkipped == 1 && skipped.FilesTransferred == 0 && skipped.BytesTransferred == 0,
                "An existing destination was not reported as skipped.");
            var newer = await Transfer(options with { ConflictPolicy = SftpConflictPolicy.NewerOnly });
            Require(newer.FilesSkipped == 1, "Newer-only must skip files in the same SFTP timestamp second.");
            Console.WriteLine($"{args[0]} via {args[1]} resumed at {savedOffset:N0} bytes, verified {length:N0} bytes.");
        }
        finally { await session.DisconnectAsync(); }
    }

    internal static SshNetSession CreateSession(bool viaJump, string username = "target",
        SshAuthMethod authentication = SshAuthMethod.Password)
    {
        var targetPort = int.Parse(Setting("TARGET_PORT"), CultureInfo.InvariantCulture);
        var gatewayPort = int.Parse(Setting("GATEWAY_PORT"), CultureInfo.InvariantCulture);
        var trusted = new Dictionary<int, string>
        {
            [targetPort] = Setting("TARGET_KEY"),
            [gatewayPort] = Setting("GATEWAY_KEY"),
        };
        return new SshNetSession(new SshConnectionInfo
        {
            Host = "127.0.0.1", Port = targetPort, Username = username,
            AuthMethod = authentication, Password = authentication == SshAuthMethod.Password ? Setting("TARGET_PASSWORD") : "",
            PrivateKeyPath = authentication == SshAuthMethod.PublicKey ? Setting("CLIENT_KEY_FILE") : "",
            KeyboardInteractivePromptAsync = (challenge, _) =>
            {
                Require(challenge.Prompts is [{ IsEchoed: false, Request: "Verification code: " }], "Unexpected authentication prompt.");
                return Task.FromResult<IReadOnlyList<string>?>([Setting("TARGET_CODE")]);
            },
            Route = viaJump ? new ConnectionRoute
            {
                Type = ConnectionRouteType.SshJump, Id = "fixture-gateway",
                Host = "127.0.0.1", Port = gatewayPort, Username = "gateway",
                AuthMethod = SshAuthMethod.Password, Password = Setting("GATEWAY_PASSWORD"),
            } : new ConnectionRoute(),
            HostKeyPromptAsync = (key, _) => Task.FromResult(
                key.State == HostKeyTrustState.Unknown &&
                trusted.TryGetValue(key.Endpoint.Port, out var expected) &&
                key.PresentedKey.Sha256Fingerprint == expected
                    ? HostKeyDecision.TrustOnce : HostKeyDecision.Cancel),
        });
    }

    internal static string Setting(string name) => Environment.GetEnvironmentVariable("SUTTY_LOOPBACK_" + name)
        ?? throw new InvalidOperationException($"Missing loopback fixture setting {name}.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
