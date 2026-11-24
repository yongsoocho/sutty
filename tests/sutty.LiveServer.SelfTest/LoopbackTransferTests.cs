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
        var targetPort = int.Parse(Setting("TARGET_PORT"), CultureInfo.InvariantCulture);
        var gatewayPort = int.Parse(Setting("GATEWAY_PORT"), CultureInfo.InvariantCulture);
        var trusted = new Dictionary<int, string>
        {
            [targetPort] = Setting("TARGET_KEY"),
            [gatewayPort] = Setting("GATEWAY_KEY"),
        };
        var session = new SshNetSession(new SshConnectionInfo
        {
            Host = "127.0.0.1", Port = targetPort, Username = "target",
            AuthMethod = SshAuthMethod.Password, Password = Setting("TARGET_PASSWORD"),
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
                var progress = new InlineProgress<SftpTransferProgress>(item =>
                {
                    if (item.Phase == SftpTransferPhase.Transferring && item.BytesTransferred >= 4 * 1024 * 1024)
                        cancel.Cancel();
                });
                try { await Transfer(options, progress, cancel.Token); }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
                throw new InvalidOperationException("The first attempt finished before the requested interruption.");
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
            Console.WriteLine($"{args[0]} via {args[1]} resumed at {savedOffset:N0} bytes, verified {length:N0} bytes.");
        }
        finally { await session.DisconnectAsync(); }
    }

    private static string Setting(string name) => Environment.GetEnvironmentVariable("SUTTY_LOOPBACK_" + name)
        ?? throw new InvalidOperationException($"Missing loopback fixture setting {name}.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
