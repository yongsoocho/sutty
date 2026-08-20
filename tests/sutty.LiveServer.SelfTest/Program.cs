using sutty.Core.Models;
using sutty.Core.Security;
using sutty.Core.Sessions;
using sutty.Core.Sftp;
using sutty.Core.Terminal;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length == 1 && string.Equals(args[0], "--self-test-evidence", StringComparison.Ordinal))
{
    try
    {
        await RunEvidenceSelfTestsAsync();
        Console.WriteLine("Live evidence self-tests passed.");
        return 0;
    }
    catch
    {
        Console.Error.WriteLine("Live evidence self-tests failed; details suppressed.");
        return 1;
    }
}

var startedAtUtc = DateTimeOffset.UtcNow;
var elapsed = Stopwatch.StartNew();
var phase = "configuration";
LiveConfiguration? configuration = null;
LiveEvidenceConfiguration? evidenceConfiguration = null;
try
{
    configuration = LiveConfiguration.Load();
    evidenceConfiguration = LiveEvidenceConfiguration.Load(configuration);

    if (evidenceConfiguration is not null)
    {
        Console.WriteLine(
            $"Live acceptance metadata: server_family={evidenceConfiguration.ServerFamily}; " +
            $"server_version={evidenceConfiguration.ServerVersion}; route=Direct; " +
            $"authentication={configuration.AuthMethod}.");
    }
    else
    {
        Console.WriteLine("Live acceptance target configured; connection identifiers are not logged.");
    }
    Console.WriteLine($"Modes: {string.Join(", ", configuration.Modes)}");

    await RunSelectedModesAsync(
        configuration,
        evidenceConfiguration,
        new LiveModeOperations(
            RunConnectionInfoAsync,
            RunDirectPasswordGateAsync,
            RunSmokeAsync,
            RunFaultInjectionAsync,
            RunScaleAsync,
            RunSoakAsync),
        value => phase = value);

    phase = "evidence";
    if (evidenceConfiguration is not null)
    {
        await LiveEvidenceWriter.WriteAsync(
            evidenceConfiguration,
            configuration,
            startedAtUtc,
            elapsed.Elapsed,
            CompletedHarnessEvidenceResult(configuration),
            "complete");
        Console.WriteLine(CompletedHarnessEvidenceResult(configuration) == LiveEvidenceResult.Pass
            ? "Privacy-safe SSH-LIVE-001 full-gate evidence bundle written as Pass."
            : "Privacy-safe partial evidence bundle written as Blocked; manual gate checks remain.");
    }

    Console.WriteLine(configuration.Modes.SetEquals(new[] { "direct-password-gate" })
        ? "SSH-LIVE-001 Direct Password full-gate checks passed."
        : "Selected live harness checks passed; this is not full gate acceptance.");
    return 0;
}
catch (Exception error)
{
    var failureCategory = ClassifyFailure(error);
    if (evidenceConfiguration is not null && configuration is not null)
    {
        try
        {
            await LiveEvidenceWriter.WriteAsync(
                evidenceConfiguration,
                configuration,
                startedAtUtc,
                elapsed.Elapsed,
                LiveEvidenceResult.Fail,
                phase,
                failureCategory);
            Console.Error.WriteLine("Privacy-safe failure evidence bundle written.");
        }
        catch
        {
            Console.Error.WriteLine("Evidence bundle could not be written; details suppressed.");
        }
    }

    Console.Error.WriteLine(
        $"Live acceptance failed during {LiveEvidenceWriter.SafePhase(phase)} " +
        $"({failureCategory}); details suppressed.");
    return 1;
}

static LiveEvidenceResult CompletedHarnessEvidenceResult(LiveConfiguration configuration) =>
    configuration.Modes.SetEquals(new[] { "direct-password-gate" })
        ? LiveEvidenceResult.Pass
        : LiveEvidenceResult.Blocked;

static async Task RunSelectedModesAsync(
    LiveConfiguration configuration,
    LiveEvidenceConfiguration? evidenceConfiguration,
    LiveModeOperations operations,
    Action<string> phaseChanged)
{
    if (configuration.Modes.Contains("connection-info"))
    {
        phaseChanged("connection-info");
        await operations.ConnectionInfo(configuration);
    }
    if (configuration.Modes.Contains("direct-password-gate"))
    {
        phaseChanged("direct-password-gate");
        await operations.DirectPasswordGate(configuration, evidenceConfiguration);
    }
    if (configuration.Modes.Contains("smoke"))
    {
        phaseChanged("smoke");
        await operations.Smoke(configuration);
    }
    if (configuration.Modes.Contains("fault"))
    {
        phaseChanged("fault");
        await operations.Fault(configuration);
    }
    if (configuration.Modes.Contains("scale"))
    {
        phaseChanged("scale");
        await operations.Scale(configuration);
    }
    if (configuration.Modes.Contains("soak"))
    {
        phaseChanged("soak");
        await operations.Soak(configuration);
    }
}

static async Task RunConnectionInfoAsync(LiveConfiguration configuration)
{
    Console.WriteLine("[connection-info] validating connect, disconnect, and reconnect snapshots");
    var session = CreateSession(configuration);
    try
    {
        await ConnectRequiredAsync(session);
        VerifyNegotiatedConnectionInfo(session, configuration);
        var firstSnapshot = session.NegotiatedInfo ??
            throw new InvalidOperationException("The first negotiated snapshot is unavailable.");

        await session.DisconnectAsync();
        Assert(session.NegotiatedInfo is null,
            "Negotiated connection information was retained after disconnect.");

        await ConnectRequiredAsync(session);
        VerifyNegotiatedConnectionInfo(session, configuration);
        var secondSnapshot = session.NegotiatedInfo ??
            throw new InvalidOperationException("The reconnect negotiated snapshot is unavailable.");
        Assert(!ReferenceEquals(firstSnapshot, secondSnapshot),
            "Reconnect reused the previous negotiated connection snapshot.");
    }
    finally
    {
        await session.DisconnectAsync();
        Assert(session.NegotiatedInfo is null,
            "Negotiated connection information was retained after final disconnect.");
    }
}

static async Task RunDirectPasswordGateAsync(
    LiveConfiguration configuration,
    LiveEvidenceConfiguration? evidence)
{
    if (evidence is null || evidence.GateId != "SSH-LIVE-001")
        throw new InvalidOperationException(
            "The Direct Password full gate requires explicitly activated SSH-LIVE-001 evidence.");

    Console.WriteLine("[direct-password-gate] verifying exact candidate package provenance");
    await VerifyCandidatePackageAsync(
        configuration.PackagePath,
        evidence.PackageSha256,
        evidence.Commit);

    Console.WriteLine("[direct-password-gate] validating successful SSH, command, PTY, SFTP, reconnect, and cleanup");
    var session = CreateSession(configuration);
    var localRoot = CreateScratch("direct-password-gate");
    var remoteName = $"sutty-direct-password-{Guid.NewGuid():N}";
    var remoteRoot = RemotePath.Combine(configuration.RemoteRoot, remoteName);
    var remoteCreated = false;
    SshNetSession? reconnectSession = null;
    try
    {
        await ConnectRequiredAsync(session);
        VerifyNegotiatedConnectionInfo(session, configuration);
        var firstSnapshot = session.NegotiatedInfo ??
            throw new InvalidOperationException("The initial negotiated snapshot is unavailable.");
        Assert(session.SftpState == SftpConnectionState.Ready,
            "SFTP is not ready for the Direct Password gate.");

        var command = await session.ExecuteCommandAsync("printf 'sutty-direct-password-command'");
        Assert(command.Succeeded && command.StandardOutput == "sutty-direct-password-command",
            "The Direct Password command check failed; command output is suppressed.");
        foreach (var tool in new[] { "vim", "tmux", "htop" })
        {
            var probe = await session.ExecuteCommandAsync($"command -v {tool}");
            Assert(probe.Succeeded, $"Required terminal tool is unavailable: {tool}");
        }
        await VerifyInteractiveTerminalAsync(session);
        Console.WriteLine("[direct-password-gate] command and PTY checks passed");

        var source = Path.Combine(localRoot, "source.bin");
        await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(64 * 1024));
        await session.Sftp.CreateDirectoryAsync(remoteRoot);
        remoteCreated = true;
        await session.Sftp.UploadFileAsync(source, remoteRoot, overwrite: false);
        var remoteFile = RemotePath.Combine(remoteRoot, Path.GetFileName(source));
        var listing = await session.Sftp.ListDirectoryAsync(remoteRoot);
        Assert(listing.Count(entry => !entry.IsDirectory) == 1 &&
               listing.Any(entry => string.Equals(
                   entry.Name,
                   Path.GetFileName(source),
                   StringComparison.Ordinal)),
            "The Direct Password SFTP upload/list check failed.");
        Console.WriteLine("[direct-password-gate] SFTP upload and listing checks passed");

        var destination = Path.Combine(localRoot, "downloaded.bin");
        await session.Sftp.DownloadFileAsync(remoteFile, destination, overwrite: false);
        Assert(await HashFileAsync(source) == await HashFileAsync(destination),
            "The Direct Password SFTP download checksum does not match.");
        Console.WriteLine("[direct-password-gate] SFTP download check passed");

        await DeleteRemoteTreeRequiredAsync(session, configuration.RemoteRoot, remoteRoot, remoteName);
        remoteCreated = false;
        Console.WriteLine("[direct-password-gate] remote cleanup check passed");

        await session.DisconnectAsync();
        AssertDisconnectedClean(session, "first successful connection");

        // Disconnect deliberately erases transient credentials. Reconnect therefore uses a
        // fresh session built from the same runtime-only configuration, never retained secrets.
        reconnectSession = CreateSession(configuration);
        await ConnectRequiredAsync(reconnectSession);
        VerifyNegotiatedConnectionInfo(reconnectSession, configuration);
        Assert(reconnectSession.SftpState == SftpConnectionState.Ready,
            "SFTP was not ready after the Direct Password reconnect.");
        var secondSnapshot = reconnectSession.NegotiatedInfo ??
            throw new InvalidOperationException("The reconnect negotiated snapshot is unavailable.");
        Assert(!ReferenceEquals(firstSnapshot, secondSnapshot),
            "Reconnect reused the previous negotiated connection snapshot.");
        await VerifyServerAuditAsync(reconnectSession, configuration.ServerAuditCommand);
        Console.WriteLine("[direct-password-gate] reconnect and server audit checks passed");
    }
    finally
    {
        if (remoteCreated)
            await DeleteRemoteTreeRequiredAsync(session, configuration.RemoteRoot, remoteRoot, remoteName);
        await session.DisconnectAsync();
        AssertDisconnectedClean(session, "successful gate connection");
        if (reconnectSession is not null)
        {
            await reconnectSession.DisconnectAsync();
            AssertDisconnectedClean(reconnectSession, "successful reconnect");
        }
        if (Directory.Exists(localRoot))
            Directory.Delete(localRoot, recursive: true);
        Assert(!Directory.Exists(localRoot),
            "The Direct Password local scratch directory remained after cleanup.");
    }

    Console.WriteLine("[direct-password-gate] validating wrong-password rejection");
    var wrongPassword = $"sutty-invalid-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
    if (string.Equals(wrongPassword, configuration.Password, StringComparison.Ordinal))
        wrongPassword += "-different";
    await VerifyRejectedConnectionAsync(
        configuration with { Password = wrongPassword },
        LiveFailureCategory.AuthenticationFailed,
        "wrong-password rejection");

    Console.WriteLine("[direct-password-gate] validating expected host-key mismatch rejection");
    await VerifyRejectedConnectionAsync(
        configuration with
        {
            ExpectedHostKeySha256 = DifferentFingerprint(configuration.ExpectedHostKeySha256),
            TrustNewHost = false,
        },
        LiveFailureCategory.HostKeyRejected,
        "host-key mismatch rejection");

    Console.WriteLine("[direct-password-gate] validating connection cancellation against a blackhole transport");
    var blackholeConfiguration = configuration with
    {
        Host = configuration.BlackholeHost,
        Port = configuration.BlackholePort,
        ExpectedHostKeySha256 = "",
        TrustNewHost = true,
    };
    var cancellationSession = CreateSession(blackholeConfiguration);
    try
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var canceled = false;
        try
        {
            await cancellationSession.ConnectAsync(cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            canceled = true;
        }
        Assert(canceled, "The blackhole connection did not honor cancellation.");
    }
    finally
    {
        await cancellationSession.DisconnectAsync();
        AssertDisconnectedClean(cancellationSession, "canceled connection");
    }

    Console.WriteLine("[direct-password-gate] validating bounded transport timeout against a blackhole transport");
    var timeoutSession = CreateSession(blackholeConfiguration);
    var timeoutWatch = Stopwatch.StartNew();
    try
    {
        await timeoutSession.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert(timeoutSession.State == SessionState.Failed,
            "The blackhole transport did not fail after the configured connection timeout.");
        Assert(timeoutWatch.Elapsed >= TimeSpan.FromSeconds(12) &&
               timeoutWatch.Elapsed < TimeSpan.FromSeconds(30),
            "The blackhole transport did not observe the bounded SSH timeout window.");
        Assert(timeoutSession.NegotiatedInfo is null,
            "A timed-out connection retained negotiated connection information.");
    }
    finally
    {
        await timeoutSession.DisconnectAsync();
        AssertDisconnectedClean(timeoutSession, "timed-out connection");
    }
}

static async Task VerifyCandidatePackageAsync(
    string packagePath,
    string expectedSha256,
    string expectedCommit)
{
    var packageAttributes = File.GetAttributes(packagePath);
    Assert((packageAttributes & FileAttributes.ReparsePoint) == 0,
        "The candidate package must be a physical file, not a reparse point.");
    await using (var packageStream = new FileStream(
                     packagePath,
                     FileMode.Open,
                     FileAccess.Read,
                     FileShare.Read,
                     128 * 1024,
                     FileOptions.Asynchronous | FileOptions.SequentialScan))
    {
        var packageSha256 = Convert.ToHexString(await SHA256.HashDataAsync(packageStream))
            .ToLowerInvariant();
        Assert(string.Equals(packageSha256, expectedSha256, StringComparison.Ordinal),
            "The exact candidate package SHA-256 does not match the evidence manifest input.");
    }

    await using var archiveStream = new FileStream(
        packagePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        128 * 1024,
        FileOptions.Asynchronous | FileOptions.RandomAccess);
    using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
    Assert(archive.Entries.Count is > 0 and <= 10_000,
        "The candidate package ZIP entry count is outside the review boundary.");

    var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    ZipArchiveEntry? packagedCore = null;
    ZipArchiveEntry? buildInfo = null;
    foreach (var entry in archive.Entries)
    {
        var name = entry.FullName;
        Assert(IsSafeZipEntryName(name),
            "The candidate package contains an unsafe ZIP entry name.");
        Assert(entryNames.Add(name),
            "The candidate package contains duplicate case-insensitive ZIP entries.");
        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        var dosAttributes = entry.ExternalAttributes & 0xFFFF;
        Assert(unixFileType != 0xA000 &&
               (dosAttributes & (int)FileAttributes.ReparsePoint) == 0,
            "The candidate package contains a symbolic-link or reparse-point entry.");

        if (string.Equals(name, "sutty.Core.dll", StringComparison.OrdinalIgnoreCase))
        {
            Assert(!name.EndsWith("/", StringComparison.Ordinal) && packagedCore is null,
                "The candidate package must contain exactly one root sutty.Core.dll file.");
            packagedCore = entry;
        }
        if (string.Equals(name, "BUILDINFO.txt", StringComparison.OrdinalIgnoreCase))
        {
            Assert(!name.EndsWith("/", StringComparison.Ordinal) && buildInfo is null,
                "The candidate package must contain exactly one root BUILDINFO.txt file.");
            buildInfo = entry;
        }
    }

    Assert(packagedCore is not null && packagedCore.Length > 0,
        "The candidate package is missing the root sutty.Core.dll file.");
    Assert(buildInfo is not null && buildInfo.Length is > 0 and <= 4096,
        "The candidate package is missing a bounded root BUILDINFO.txt file.");
    await using (var buildInfoStream = buildInfo!.Open())
    using (var reader = new StreamReader(
               buildInfoStream,
               new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
               detectEncodingFromByteOrderMarks: false,
               bufferSize: 4096,
               leaveOpen: false))
    {
        var buildInfoText = await reader.ReadToEndAsync();
        var buildInfoLines = buildInfoText.Split(
            new[] { "\r\n", "\n" },
            StringSplitOptions.RemoveEmptyEntries);
        Assert(buildInfoLines.Contains($"Commit: {expectedCommit}", StringComparer.Ordinal) &&
               buildInfoLines.Contains("Architecture: x64", StringComparer.Ordinal),
            "The candidate package BUILDINFO identity does not match the gate commit and architecture.");
    }
    var loadedCorePath = typeof(SshNetSession).Assembly.Location;
    Assert(!string.IsNullOrWhiteSpace(loadedCorePath) && File.Exists(loadedCorePath),
        "The loaded Sutty Core assembly is unavailable for package identity verification.");
    await using var loadedCoreStream = File.OpenRead(loadedCorePath);
    await using var packagedCoreStream = packagedCore!.Open();
    var loadedCoreSha256 = await SHA256.HashDataAsync(loadedCoreStream);
    var packagedCoreSha256 = await SHA256.HashDataAsync(packagedCoreStream);
    Assert(CryptographicOperations.FixedTimeEquals(loadedCoreSha256, packagedCoreSha256),
        "The running Sutty Core assembly is not byte-identical to the exact candidate package entry.");
}

static bool IsSafeZipEntryName(string name)
{
    if (string.IsNullOrWhiteSpace(name) || name.Length > 512 ||
        name.StartsWith("/", StringComparison.Ordinal) ||
        name.Contains("\\", StringComparison.Ordinal) ||
        name.Contains(":", StringComparison.Ordinal) ||
        name.Any(character => char.IsControl(character)))
        return false;

    var path = name.EndsWith("/", StringComparison.Ordinal) ? name[..^1] : name;
    if (path.Length == 0)
        return false;
    var segments = path.Split('/');
    return segments.All(segment => segment.Length is > 0 and <= 128 &&
                                   segment is not "." and not "..");
}

static async Task VerifyRejectedConnectionAsync(
    LiveConfiguration configuration,
    LiveFailureCategory expectedCategory,
    string checkName)
{
    var session = CreateSession(configuration);
    try
    {
        await session.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(45));
        Assert(session.State == SessionState.Failed,
            $"The {checkName} attempt did not enter the failed state.");
        Assert(ClassifyConnectionFailure(session.LastError) == expectedCategory,
            $"The {checkName} attempt did not retain the expected stable failure category.");
        Assert(session.NegotiatedInfo is null,
            $"The {checkName} attempt retained negotiated connection information.");
    }
    finally
    {
        await session.DisconnectAsync();
        AssertDisconnectedClean(session, checkName);
    }
}

static async Task VerifyServerAuditAsync(SshNetSession session, string auditCommand)
{
    var audit = await session.ExecuteCommandAsync(auditCommand);
    Assert(audit.Succeeded, "The server-side session-category audit was unavailable.");
    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var line in audit.StandardOutput.Split(
                 new[] { '\r', '\n' },
                 StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var match = Regex.Match(
            line,
            "^(exec|shell|sftp|other)=([0-9]{1,6})$",
            RegexOptions.CultureInvariant);
        Assert(match.Success && counts.TryAdd(
                match.Groups[1].Value,
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)),
            "The server-side session-category audit output is not canonical.");
    }
    Assert(counts.Count == 4 &&
           counts.GetValueOrDefault("exec") == 4 &&
           counts.GetValueOrDefault("shell") == 1 &&
           counts.GetValueOrDefault("sftp") == 2 &&
           counts.GetValueOrDefault("other") == 0,
        "The server-side audit detected an unexpected or missing session request.");
}

static async Task DeleteRemoteTreeRequiredAsync(
    SshNetSession session,
    string remoteParent,
    string remoteRoot,
    string remoteName)
{
    Assert(session.State == SessionState.Connected &&
           session.SftpState == SftpConnectionState.Ready,
        "The session was unavailable for required remote cleanup.");
    var preview = await session.Sftp.PreviewDeleteAsync(remoteRoot);
    Assert(preview.FileCount == 1 && preview.DirectoryCount == 1,
        "The required remote cleanup preview did not match the gate fixture.");
    await session.Sftp.DeletePathRecursiveAsync(remoteRoot);
    var remaining = await session.Sftp.ListDirectoryAsync(remoteParent);
    Assert(!remaining.Any(entry => string.Equals(
            entry.Name,
            remoteName,
            StringComparison.Ordinal)),
        "The Direct Password remote fixture remained after cleanup.");
}

static void AssertDisconnectedClean(SshNetSession session, string checkName)
{
    Assert(session.State == SessionState.Disconnected,
        $"The {checkName} session did not reach Disconnected.");
    Assert(session.NegotiatedInfo is null,
        $"The {checkName} session retained negotiated connection information.");
    Assert(session.SftpState == SftpConnectionState.NotConnected,
        $"The {checkName} session retained an SFTP connection.");
    Assert(session.TerminalState == TerminalState.Closed,
        $"The {checkName} session retained an interactive terminal.");
}

static string DifferentFingerprint(string expected)
{
    var normalized = expected.Trim().TrimEnd('=');
    Assert(normalized.Length > "SHA256:".Length,
        "The independently configured host fingerprint is invalid.");
    var replacement = normalized[^1] == 'A' ? 'B' : 'A';
    return normalized[..^1] + replacement;
}

static async Task RunSmokeAsync(LiveConfiguration configuration)
{
    Console.WriteLine("[smoke] connecting through Sutty session path");
    var session = CreateSession(configuration);
    var localRoot = CreateScratch("smoke");
    var remoteRoot = RemotePath.Combine(configuration.RemoteRoot, $"sutty-smoke-{Guid.NewGuid():N}");
    try
    {
        await ConnectRequiredAsync(session);
        VerifyNegotiatedConnectionInfo(session, configuration);
        Assert(session.SftpState == SftpConnectionState.Ready,
            "SFTP is not ready; subsystem details are suppressed.");

        var command = await session.ExecuteCommandAsync("printf 'sutty-live-command'");
        Assert(command.Succeeded && command.StandardOutput == "sutty-live-command",
            "SSH exec failed; command output is suppressed.");

        foreach (var tool in new[] { "vim", "tmux", "htop" })
        {
            var probe = await session.ExecuteCommandAsync($"command -v {tool}");
            Assert(probe.Succeeded, $"Required terminal tool is unavailable: {tool}");
        }

        await VerifyInteractiveTerminalAsync(session);

        var source = Path.Combine(localRoot, "source");
        Directory.CreateDirectory(Path.Combine(source, "nested", "empty"));
        await File.WriteAllTextAsync(Path.Combine(source, "root.json"), "{\"live\":true}\n");
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "한글.yaml"), "상태: 정상\n");
        await session.Sftp.CreateDirectoryAsync(remoteRoot);
        var remoteTree = RemotePath.Combine(remoteRoot, "tree");
        var uploaded = await session.Sftp.UploadPathAsync(
            source,
            remoteTree,
            new SftpTransferOptions { Resume = true, VerifyChecksum = true, MaxRetries = 3 });
        Assert(uploaded.FilesTransferred == 2, "Recursive live upload file count is incorrect.");

        var enumerated = await session.Sftp.EnumerateTreeAsync(remoteTree);
        Assert(enumerated.Count >= 4 && enumerated.Any(item => item.RelativePath.Contains("한글.yaml")),
            "Remote tree enumeration did not preserve the complete UTF-8 tree.");

        var destination = Path.Combine(localRoot, "downloaded");
        var downloaded = await session.Sftp.DownloadPathAsync(
            remoteTree,
            destination,
            new SftpTransferOptions { Resume = true, VerifyChecksum = true, MaxRetries = 3 });
        Assert(downloaded.FilesTransferred == 2, "Recursive live download file count is incorrect.");
        Assert(await HashFileAsync(Path.Combine(source, "nested", "한글.yaml")) ==
               await HashFileAsync(Path.Combine(destination, "nested", "한글.yaml")),
            "Downloaded UTF-8 file checksum does not match.");
    }
    finally
    {
        await TryDeleteRemoteTreeAsync(session, remoteRoot);
        await session.DisconnectAsync();
        Directory.Delete(localRoot, recursive: true);
    }
}

static async Task RunFaultInjectionAsync(LiveConfiguration configuration)
{
    Console.WriteLine("[fault] disconnecting transport during upload, then resuming");
    var session = CreateSession(configuration);
    var localRoot = CreateScratch("fault");
    var remoteRoot = RemotePath.Combine(configuration.RemoteRoot, $"sutty-fault-{Guid.NewGuid():N}");
    try
    {
        await ConnectRequiredAsync(session);
        Assert(session.SftpState == SftpConnectionState.Ready, "SFTP is required for fault injection.");
        await session.Sftp.CreateDirectoryAsync(remoteRoot);
        var source = Path.Combine(localRoot, "fault.bin");
        await CreateSparseFileAsync(source, configuration.FaultMegabytes * 1024L * 1024L);
        var destination = RemotePath.Combine(remoteRoot, "fault.bin");
        var crossedThreshold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new InlineProgress<SftpTransferProgress>(item =>
        {
            if (item.BytesTransferred >= 4L * 1024 * 1024)
                crossedThreshold.TrySetResult();
        });

        var firstAttempt = session.Sftp.UploadPathAsync(
            source,
            destination,
            new SftpTransferOptions { Resume = true, VerifyChecksum = true, RetryEnabled = false },
            progress);
        await crossedThreshold.Task.WaitAsync(TimeSpan.FromMinutes(2));
        await session.DisconnectAsync();
        try { await firstAttempt; } catch { }

        await ConnectRequiredAsync(session);
        Assert(session.SftpState == SftpConnectionState.Ready,
            "SFTP did not recover after injected disconnect.");
        var resumed = await session.Sftp.UploadPathAsync(
            source,
            destination,
            new SftpTransferOptions { Resume = true, VerifyChecksum = true, MaxRetries = 3 });
        Assert(resumed.ResumedBytes > 0,
            "The interrupted upload restarted from zero instead of its checkpoint.");
        Assert(resumed.BytesTransferred == new FileInfo(source).Length,
            "Resumed upload final size does not match the source.");
    }
    finally
    {
        await TryDeleteRemoteTreeAsync(session, remoteRoot);
        await session.DisconnectAsync();
        Directory.Delete(localRoot, recursive: true);
    }
}

static async Task RunScaleAsync(LiveConfiguration configuration)
{
    Console.WriteLine($"[scale] {configuration.LargeGigabytes}GB and {configuration.FileCount:N0} files");
    var session = CreateSession(configuration);
    var localRoot = CreateScratch("scale");
    var remoteRoot = RemotePath.Combine(configuration.RemoteRoot, $"sutty-scale-{Guid.NewGuid():N}");
    try
    {
        await ConnectRequiredAsync(session);
        Assert(session.SftpState == SftpConnectionState.Ready, "SFTP is required for scale tests.");
        await session.Sftp.CreateDirectoryAsync(remoteRoot);

        var largeFile = Path.Combine(localRoot, $"large-{configuration.LargeGigabytes}gb.bin");
        await CreateSparseFileAsync(largeFile, configuration.LargeGigabytes * 1024L * 1024 * 1024);
        var largeResult = await session.Sftp.UploadPathAsync(
            largeFile,
            RemotePath.Combine(remoteRoot, Path.GetFileName(largeFile)),
            new SftpTransferOptions { Resume = true, VerifyChecksum = true, MaxRetries = 3 });
        Assert(largeResult.BytesTransferred == new FileInfo(largeFile).Length &&
               largeResult.Sha256 is { Length: 64 },
            "Large-file size/checksum acceptance failed.");

        var manyFiles = Path.Combine(localRoot, "many-files");
        Directory.CreateDirectory(manyFiles);
        for (var index = 0; index < configuration.FileCount; index++)
        {
            var shard = Path.Combine(manyFiles, $"{index / 1000:D3}");
            Directory.CreateDirectory(shard);
            await File.WriteAllTextAsync(Path.Combine(shard, $"{index:D6}.txt"), index.ToString());
        }
        var manyResult = await session.Sftp.UploadPathAsync(
            manyFiles,
            RemotePath.Combine(remoteRoot, "many-files"),
            new SftpTransferOptions { Resume = true, VerifyChecksum = true, MaxRetries = 3 });
        Assert(manyResult.FilesTransferred == configuration.FileCount,
            "Large directory file count does not match.");
        var tree = await session.Sftp.EnumerateTreeAsync(RemotePath.Combine(remoteRoot, "many-files"));
        Assert(tree.Count(item => !item.Entry.IsDirectory) == configuration.FileCount,
            "100k-file remote enumeration count does not match.");
    }
    finally
    {
        await TryDeleteRemoteTreeAsync(session, remoteRoot);
        await session.DisconnectAsync();
        Directory.Delete(localRoot, recursive: true);
    }
}

static async Task RunSoakAsync(LiveConfiguration configuration)
{
    Console.WriteLine($"[soak] {configuration.SessionCount} sessions for {configuration.SoakMinutes} minute(s)");
    var sessions = Enumerable.Range(0, configuration.SessionCount)
        .Select(_ => CreateSession(configuration))
        .ToArray();
    try
    {
        await Task.WhenAll(sessions.Select(ConnectRequiredAsync));
        Assert(sessions.All(session => session.State == SessionState.Connected),
            "Not every soak session connected.");
        var deadline = DateTimeOffset.UtcNow.AddMinutes(configuration.SoakMinutes);
        var iteration = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var results = await Task.WhenAll(sessions.Select(session =>
                session.ExecuteCommandAsync($"printf 'soak-{iteration}'")));
            Assert(results.All(result => result.Succeeded),
                $"A soak command failed at iteration {iteration}.");
            iteration++;
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }
    finally
    {
        await Task.WhenAll(sessions.Select(session => session.DisconnectAsync()));
    }
}

static SshNetSession CreateSession(LiveConfiguration configuration)
{
    var promptAnswers = new ConcurrentQueue<string>(configuration.KeyboardInteractiveAnswers);
    var info = new SshConnectionInfo
    {
        Host = configuration.Host,
        Port = configuration.Port,
        Username = configuration.Username,
        AuthMethod = configuration.AuthMethod,
        Password = configuration.Password,
        PrivateKeyPath = configuration.PrivateKeyPath,
        Passphrase = configuration.PrivateKeyPassphrase,
        KeepAliveSeconds = 15,
        HostKeyPromptAsync = (verification, cancellationToken) => Task.FromResult(
            verification.State == HostKeyTrustState.Changed
                ? HostKeyDecision.Cancel
                : configuration.CanTrust(verification.PresentedKey.Sha256Fingerprint)
                    ? HostKeyDecision.TrustOnce
                    : HostKeyDecision.Cancel),
        KeyboardInteractivePromptAsync = (challenge, cancellationToken) =>
        {
            var answers = new List<string>();
            foreach (var prompt in challenge.Prompts)
                answers.Add(promptAnswers.TryDequeue(out var value) ? value : configuration.Password);
            return Task.FromResult<IReadOnlyList<string>?>(answers);
        },
    };
    return new SshNetSession(info);
}

static async Task ConnectRequiredAsync(SshNetSession session)
{
    await session.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(45));
    if (session.State != SessionState.Connected)
        throw new LiveAcceptanceFailureException(
            ClassifyConnectionFailure(session.LastError));
}

static void VerifyNegotiatedConnectionInfo(
    SshNetSession session,
    LiveConfiguration configuration)
{
    var negotiated = session.NegotiatedInfo ??
        throw new InvalidOperationException(
            "The connected SSH session did not publish negotiated connection information.");

    Assert(!string.IsNullOrWhiteSpace(negotiated.ServerVersion),
        "The SSH server identification was not reported.");
    Assert(!string.IsNullOrWhiteSpace(negotiated.ClientVersion),
        "The SSH client identification was not reported.");
    Assert(!string.IsNullOrWhiteSpace(negotiated.KeyExchangeAlgorithm),
        "The negotiated SSH key-exchange algorithm was not reported.");
    Assert(!string.IsNullOrWhiteSpace(negotiated.HostKeyAlgorithm),
        "The negotiated SSH host-key algorithm was not reported.");
    var hostKeyFingerprint = negotiated.HostKeySha256Fingerprint ?? "";
    Assert(!string.IsNullOrWhiteSpace(hostKeyFingerprint),
        "The verified SSH host-key fingerprint was not reported.");
    Assert(!string.IsNullOrWhiteSpace(negotiated.ClientToServerCipher) &&
           !string.IsNullOrWhiteSpace(negotiated.ServerToClientCipher),
        "Both negotiated SSH cipher directions must be reported.");
    Assert(!string.IsNullOrWhiteSpace(negotiated.ClientToServerMac) &&
           !string.IsNullOrWhiteSpace(negotiated.ServerToClientMac),
        "Both negotiated SSH MAC directions must be reported.");
    Assert(!string.IsNullOrWhiteSpace(negotiated.ClientToServerCompression) &&
           !string.IsNullOrWhiteSpace(negotiated.ServerToClientCompression),
        "Both negotiated SSH compression directions must be reported.");

    if (!string.IsNullOrWhiteSpace(configuration.ExpectedHostKeySha256))
    {
        Assert(configuration.MatchesExpectedFingerprint(hostKeyFingerprint),
            "The negotiated-information fingerprint does not match the independently configured expected host key.");
    }
}

static async Task VerifyInteractiveTerminalAsync(SshNetSession session)
{
    var output = new MemoryStream();
    var gate = new object();
    void OnData(object? _, TerminalDataReceivedEventArgs args)
    {
        lock (gate) output.Write(args.Data.Span);
    }
    session.TerminalDataReceived += OnData;
    try
    {
        await session.OpenTerminalAsync(new TerminalSize(120, 35));
        var marker = $"sutty-pty-{Guid.NewGuid():N}";
        await session.SendTerminalInputAsync(Encoding.UTF8.GetBytes($"printf '{marker} 한글\\n'\n"));
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            string text;
            lock (gate) text = Encoding.UTF8.GetString(output.ToArray());
            if (text.Contains(marker, StringComparison.Ordinal) &&
                text.Contains("한글", StringComparison.Ordinal))
                return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Interactive PTY UTF-8 output did not arrive.");
    }
    finally
    {
        await session.CloseTerminalAsync();
        session.TerminalDataReceived -= OnData;
        output.Dispose();
    }
}

static async Task TryDeleteRemoteTreeAsync(SshNetSession session, string root)
{
    if (session.State != SessionState.Connected || session.SftpState != SftpConnectionState.Ready)
        return;
    try
    {
        var entries = await session.Sftp.EnumerateTreeAsync(root);
        foreach (var entry in entries.Where(item => !item.Entry.IsDirectory)
                     .OrderByDescending(item => item.Depth))
            await session.Sftp.DeleteFileAsync(entry.Entry.FullPath);
        foreach (var entry in entries.Where(item => item.Entry.IsDirectory)
                     .OrderByDescending(item => item.Depth))
            await session.Sftp.DeleteDirectoryAsync(entry.Entry.FullPath);
        await session.Sftp.DeleteDirectoryAsync(root);
    }
    catch
    {
        Console.Error.WriteLine("Remote cleanup warning: cleanup did not complete; details suppressed.");
    }
}

static async Task<string> HashFileAsync(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexString(await SHA256.HashDataAsync(stream));
}

static async Task CreateSparseFileAsync(string path, long length)
{
    await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    stream.SetLength(length);
    await stream.FlushAsync();
}

static async Task RunEvidenceSelfTestsAsync()
{
    var temporaryBase = Path.GetFullPath(Path.GetTempPath());
    var scratch = Path.Combine(
        temporaryBase,
        $"sutty-live-evidence-self-test-{Guid.NewGuid():N}");
    var environment = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["SUTTY_TEST_SSH_HOST"] = "sensitive-host.invalid",
        ["SUTTY_TEST_SSH_USER"] = "sensitive-operator",
        ["SUTTY_TEST_SSH_PASSWORD"] = "sensitive-password",
        ["SUTTY_TEST_SSH_KEY_PATH"] = @"C:\sensitive\private-key",
        ["SUTTY_TEST_SSH_KEY_PASSPHRASE"] = "sensitive-passphrase",
        ["SUTTY_TEST_SSH_AUTH"] = "Password",
        ["SUTTY_TEST_HOST_KEY_SHA256"] = "SHA256:sensitive-fingerprint",
        ["SUTTY_TEST_REMOTE_ROOT"] = "/sensitive/remote/root",
        ["SUTTY_TEST_KBI_ANSWERS"] = "sensitive-otp|sensitive-answer",
    };
    string? ReadEnvironment(string name) =>
        environment.TryGetValue(name, out var value) ? value : null;

    try
    {
        Directory.CreateDirectory(scratch);

        var defaultConfiguration = LiveConfiguration.Load(ReadEnvironment);
        Assert(defaultConfiguration.Modes.SetEquals(new[] { "smoke" }),
            "The credentialed harness default mode changed.");
        Assert(defaultConfiguration.MatchesExpectedFingerprint("SHA256:sensitive-fingerprint") &&
               !defaultConfiguration.MatchesExpectedFingerprint("SHA256:different-fingerprint"),
            "Independent expected-fingerprint matching changed.");
        Assert(LiveEvidenceConfiguration.Load(defaultConfiguration, ReadEnvironment) is null,
            "Evidence generation must remain disabled by default.");
        Assert(CompletedHarnessEvidenceResult(defaultConfiguration) == LiveEvidenceResult.Blocked,
            "Partial live harness coverage must never emit a gate-level Pass result.");

        environment["SUTTY_TEST_MODES"] = "connection-info,smoke,fault,scale,soak";
        var allModesConfiguration = LiveConfiguration.Load(ReadEnvironment);
        var invoked = new List<string>();
        var phases = new List<string>();
        await RunSelectedModesAsync(
            allModesConfiguration,
            null,
            new LiveModeOperations(
                _ => RecordModeAsync("connection-info", invoked),
                (_, _) => RecordModeAsync("direct-password-gate", invoked),
                _ => RecordModeAsync("smoke", invoked),
                _ => RecordModeAsync("fault", invoked),
                _ => RecordModeAsync("scale", invoked),
                _ => RecordModeAsync("soak", invoked)),
            phases.Add);
        var expectedModes = new[] { "connection-info", "smoke", "fault", "scale", "soak" };
        Assert(invoked.SequenceEqual(expectedModes) && phases.SequenceEqual(expectedModes),
            "Live mode dispatch order or coverage changed.");

        var programPath = FindProgramSourcePath();
        var programSource = await File.ReadAllTextAsync(programPath);
        var connectionInfoStart = programSource.IndexOf(
            "static async Task RunConnectionInfoAsync",
            StringComparison.Ordinal);
        var connectionInfoEnd = connectionInfoStart >= 0
            ? programSource.IndexOf(
                "static async Task RunDirectPasswordGateAsync",
                connectionInfoStart,
                StringComparison.Ordinal)
            : -1;
        Assert(connectionInfoStart >= 0 && connectionInfoEnd > connectionInfoStart,
            "The connection-info source boundary is unavailable.");
        var connectionInfoSource = programSource[connectionInfoStart..connectionInfoEnd];
        foreach (var forbiddenCall in new[]
                 {
                     "ExecuteCommandAsync",
                     "OpenTerminalAsync",
                     ".Sftp",
                     "RunSmokeAsync",
                     "RunFaultInjectionAsync",
                     "RunScaleAsync",
                     "RunSoakAsync",
                 })
        {
            Assert(!connectionInfoSource.Contains(forbiddenCall, StringComparison.Ordinal),
                "The connection-info mode contains a forbidden operation.");
        }
        AssertSourceSliceContains(
            programSource,
            "static async Task RunSmokeAsync",
            "static async Task RunFaultInjectionAsync",
            "smoke",
            "ExecuteCommandAsync",
            "VerifyInteractiveTerminalAsync",
            "UploadPathAsync",
            "DownloadPathAsync");
        AssertSourceSliceContains(
            programSource,
            "static async Task RunFaultInjectionAsync",
            "static async Task RunScaleAsync",
            "fault",
            "crossedThreshold",
            "DisconnectAsync",
            "ResumedBytes > 0");
        AssertSourceSliceContains(
            programSource,
            "static async Task RunScaleAsync",
            "static async Task RunSoakAsync",
            "scale",
            "LargeGigabytes",
            "FileCount",
            "UploadPathAsync",
            "EnumerateTreeAsync");
        AssertSourceSliceContains(
            programSource,
            "static async Task RunSoakAsync",
            "static SshNetSession CreateSession",
            "soak",
            "SessionCount",
            "SoakMinutes",
            "Task.WhenAll");
        AssertSourceSliceContains(
            programSource,
            "static async Task RunDirectPasswordGateAsync",
            "static async Task RunSmokeAsync",
            "direct-password-gate",
            "VerifyCandidatePackageAsync",
            "AuthenticationFailed",
            "HostKeyRejected",
            "CancellationTokenSource",
            "blackhole",
            "VerifyServerAuditAsync",
            "DeleteRemoteTreeRequiredAsync");

        var candidatePackage = Path.Combine(
            scratch,
            "Sutty-v0.1.0-alpha.4-win-x64.zip");
        await using (var package = new FileStream(
                         candidatePackage,
                         FileMode.CreateNew,
                         FileAccess.ReadWrite,
                         FileShare.None))
        using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: false))
        {
            var coreEntry = archive.CreateEntry("sutty.Core.dll", CompressionLevel.NoCompression);
            await using (var destination = coreEntry.Open())
            await using (var source = File.OpenRead(typeof(SshNetSession).Assembly.Location))
            {
                await source.CopyToAsync(destination);
            }
            var buildInfoEntry = archive.CreateEntry("BUILDINFO.txt", CompressionLevel.NoCompression);
            await using var buildInfo = new StreamWriter(
                buildInfoEntry.Open(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await buildInfo.WriteAsync(
                $"Sutty v0.1.0-alpha.4\nCommit: {new string('a', 40)}\nArchitecture: x64\n");
        }
        var candidateSha256 = (await HashFileAsync(candidatePackage)).ToLowerInvariant();
        await VerifyCandidatePackageAsync(candidatePackage, candidateSha256, new string('a', 40));
        await ExpectFailureAsync(
            () => VerifyCandidatePackageAsync(
                candidatePackage,
                new string('c', 64),
                new string('a', 40)),
            "A candidate package with a mismatched SHA-256 must fail closed.");

        var unsafePackage = Path.Combine(scratch, "unsafe.zip");
        await using (var package = new FileStream(
                         unsafePackage,
                         FileMode.CreateNew,
                         FileAccess.ReadWrite,
                         FileShare.None))
        using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: false))
        {
            archive.CreateEntry("../sutty.Core.dll");
        }
        await ExpectFailureAsync(
            () => VerifyCandidatePackageAsync(
                unsafePackage,
                HashFileAsync(unsafePackage).GetAwaiter().GetResult().ToLowerInvariant(),
                new string('a', 40)),
            "A candidate package with an unsafe ZIP entry must fail closed.");

        environment["SUTTY_TEST_MODES"] = "direct-password-gate";
        environment["SUTTY_TEST_HOST_KEY_SHA256"] = $"SHA256:{new string('A', 43)}";
        environment["SUTTY_TEST_BLACKHOLE_HOST"] = "sensitive-blackhole.invalid";
        environment["SUTTY_TEST_BLACKHOLE_PORT"] = "2222";
        environment["SUTTY_TEST_SERVER_AUDIT_COMMAND"] = "sutty-lab-audit-summary";
        environment["SUTTY_TEST_PACKAGE_PATH"] = candidatePackage;
        var directPasswordConfiguration = LiveConfiguration.Load(ReadEnvironment);
        Assert(CompletedHarnessEvidenceResult(directPasswordConfiguration) == LiveEvidenceResult.Pass,
            "Only the distinct Direct Password full-gate mode may produce Pass after completion.");
        environment["SUTTY_EVIDENCE_OUTPUT_DIR"] = Path.Combine(scratch, "direct-evidence");
        environment["SUTTY_EVIDENCE_APPROVED"] = "1";
        environment["SUTTY_EVIDENCE_GATE_ID"] = "SSH-LIVE-001";
        environment["SUTTY_EVIDENCE_COMMIT"] = new string('a', 40);
        environment["SUTTY_EVIDENCE_PACKAGE_SHA256"] = candidateSha256;
        environment["SUTTY_EVIDENCE_SERVER_FAMILY"] = "OpenSSH";
        environment["SUTTY_EVIDENCE_SERVER_VERSION"] = "9.6p1";
        var directPasswordEvidence = LiveEvidenceConfiguration.Load(
            directPasswordConfiguration,
            ReadEnvironment) ?? throw new InvalidOperationException(
                "The full Direct Password mode did not map to evidence.");
        var directPasswordBundle = await LiveEvidenceWriter.WriteAsync(
            directPasswordEvidence,
            directPasswordConfiguration,
            new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(20),
            LiveEvidenceResult.Pass,
            "complete");
        var directPasswordManifest = await File.ReadAllTextAsync(
            Path.Combine(directPasswordBundle, LiveEvidenceWriter.ManifestFileName));
        using (var directPasswordSummary = JsonDocument.Parse(await File.ReadAllTextAsync(
                   Path.Combine(directPasswordBundle, LiveEvidenceWriter.SummaryFileName))))
        {
            var root = directPasswordSummary.RootElement;
            var checks = root.GetProperty("checks").EnumerateArray().ToArray();
            Assert(directPasswordManifest.Contains("result: \"Pass\"", StringComparison.Ordinal) &&
                   root.GetProperty("result").GetString() == "Pass" &&
                   checks.Length == 12 &&
                   checks.All(check => check.GetProperty("result").GetString() == "Pass") &&
                   !root.TryGetProperty("blocking_category", out _),
                "The complete Direct Password gate did not emit a canonical all-Pass summary.");
        }

        environment["SUTTY_TEST_HOST_KEY_SHA256"] = "SHA256:sensitive-fingerprint";
        environment.Remove("SUTTY_TEST_BLACKHOLE_HOST");
        environment.Remove("SUTTY_TEST_BLACKHOLE_PORT");
        environment.Remove("SUTTY_TEST_SERVER_AUDIT_COMMAND");
        environment.Remove("SUTTY_TEST_PACKAGE_PATH");

        environment["SUTTY_TEST_MODES"] = "connection-info";
        var evidenceLiveConfiguration = LiveConfiguration.Load(ReadEnvironment);
        environment["SUTTY_EVIDENCE_OUTPUT_DIR"] = Path.Combine(scratch, "published");
        environment["SUTTY_EVIDENCE_APPROVED"] = "1";
        environment["SUTTY_EVIDENCE_GATE_ID"] = "SSH-INFO-001";
        environment["SUTTY_EVIDENCE_COMMIT"] = new string('a', 40);
        environment["SUTTY_EVIDENCE_PACKAGE_SHA256"] = new string('b', 64);
        environment["SUTTY_EVIDENCE_SERVER_FAMILY"] = "OpenSSH";
        environment["SUTTY_EVIDENCE_SERVER_VERSION"] = "9.6p1";
        var evidence = LiveEvidenceConfiguration.Load(
            evidenceLiveConfiguration,
            ReadEnvironment) ?? throw new InvalidOperationException(
                "Explicit evidence configuration was not enabled.");
        Assert(!evidence.RedactionReviewed,
            "Generated evidence must remain an unreviewed candidate by default.");
        environment["SUTTY_EVIDENCE_REDACTION_REVIEWED"] = "1";
        var reviewedEvidence = LiveEvidenceConfiguration.Load(
            evidenceLiveConfiguration,
            ReadEnvironment) ?? throw new InvalidOperationException(
                "Explicitly reviewed evidence configuration was not enabled.");
        Assert(reviewedEvidence.RedactionReviewed,
            "Explicit redaction review acknowledgement was not preserved.");
        environment.Remove("SUTTY_EVIDENCE_REDACTION_REVIEWED");

        var priorRawKey = Environment.GetEnvironmentVariable("SUTTY_TEST_SSH_PRIVATE_KEY");
        Environment.SetEnvironmentVariable(
            "SUTTY_TEST_SSH_PRIVATE_KEY",
            "-----BEGIN PRIVATE KEY-----sensitive-raw-key-----END PRIVATE KEY-----");
        string blockedBundle;
        string failedBundle;
        try
        {
            var started = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);
            blockedBundle = await LiveEvidenceWriter.WriteAsync(
                evidence,
                evidenceLiveConfiguration,
                started,
                TimeSpan.FromSeconds(2),
                LiveEvidenceResult.Blocked,
                "self-test");
            failedBundle = await LiveEvidenceWriter.WriteAsync(
                evidence,
                evidenceLiveConfiguration,
                started.AddMinutes(1),
                TimeSpan.FromSeconds(3),
                LiveEvidenceResult.Fail,
                "connection-info",
                LiveFailureCategory.AssertionFailed);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUTTY_TEST_SSH_PRIVATE_KEY", priorRawKey);
        }

        var publishedBundles = Directory.GetDirectories(evidence.OutputRoot);
        Assert(publishedBundles.Length == 2 &&
               publishedBundles.Contains(blockedBundle, StringComparer.OrdinalIgnoreCase) &&
               publishedBundles.Contains(failedBundle, StringComparer.OrdinalIgnoreCase),
            "Evidence bundles were not atomically promoted under the explicit output root.");
        Assert(Directory.GetDirectories(
                    evidence.OutputRoot,
                    ".sutty-evidence-staging-*",
                    SearchOption.TopDirectoryOnly)
                .Length == 0,
            "Evidence staging directory remained after atomic promotion.");

        foreach (var bundle in publishedBundles)
        {
            Assert(Regex.IsMatch(
                    Path.GetFileName(bundle),
                    "^[a-z0-9]+(?:-[a-z0-9]+)+$",
                    RegexOptions.CultureInvariant) &&
                   Path.GetFileName(bundle).Length <= 64,
                "Evidence bundle directory name is not canonical lowercase ASCII.");
            var files = Directory.GetFiles(bundle)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert(files.SequenceEqual(
                    new[] { LiveEvidenceWriter.ManifestFileName, LiveEvidenceWriter.SummaryFileName }
                        .Order(StringComparer.Ordinal)),
                "Evidence bundle file inventory is not canonical.");
            var manifest = await File.ReadAllTextAsync(
                Path.Combine(bundle, LiveEvidenceWriter.ManifestFileName));
            var summary = await File.ReadAllTextAsync(
                Path.Combine(bundle, LiveEvidenceWriter.SummaryFileName));
            foreach (var requiredManifestText in new[]
                     {
                         "schema_version: 1",
                         "gate_id: \"SSH-INFO-001\"",
                         "expected_host_fingerprint: \"SHA256:[redacted]\"",
                         "  - \"summary.json\"",
                         "redaction_reviewed: false",
                     })
            {
                Assert(manifest.Contains(requiredManifestText, StringComparison.Ordinal),
                    "Evidence manifest is missing a required canonical field.");
            }
            using var summaryDocument = JsonDocument.Parse(summary);
            var summaryRoot = summaryDocument.RootElement;
            Assert(summaryRoot.GetProperty("gate_id").GetString() == "SSH-INFO-001" &&
                   summaryRoot.GetProperty("redaction_reviewed").ValueKind == JsonValueKind.False &&
                   summaryRoot.GetProperty("privacy_notice").ValueKind == JsonValueKind.String,
                "Redacted summary contradicts the manifest identity.");

            var combined = manifest + summary;
            foreach (var sensitiveValue in evidenceLiveConfiguration.SensitiveValues.Append(
                         "-----BEGIN PRIVATE KEY-----sensitive-raw-key-----END PRIVATE KEY-----"))
            {
                Assert(!combined.Contains(sensitiveValue, StringComparison.Ordinal),
                    "Evidence output contains a sensitive sentinel.");
            }
        }
        var blockedManifest = await File.ReadAllTextAsync(
            Path.Combine(blockedBundle, LiveEvidenceWriter.ManifestFileName));
        var blockedSummary = await File.ReadAllTextAsync(
            Path.Combine(blockedBundle, LiveEvidenceWriter.SummaryFileName));
        Assert(blockedManifest.Contains("result: \"Blocked\"", StringComparison.Ordinal) &&
               blockedSummary.Contains(
                   "\"blocking_category\": \"ManualGateCoverageRequired\"",
                   StringComparison.Ordinal) &&
               blockedSummary.Contains("\"result\": \"Pass\"", StringComparison.Ordinal),
            "Partial live checks must remain a Blocked gate result with passed sub-checks.");
        var failedSummary = await File.ReadAllTextAsync(
            Path.Combine(failedBundle, LiveEvidenceWriter.SummaryFileName));
        Assert(failedSummary.Contains(
                "\"failure_category\": \"AssertionFailed\"",
                StringComparison.Ordinal),
            "Failure evidence does not contain its stable category.");

        environment.Remove("SUTTY_EVIDENCE_APPROVED");
        ExpectFailure(
            () => LiveEvidenceConfiguration.Load(evidenceLiveConfiguration, ReadEnvironment),
            "Evidence output without explicit approval must fail closed.");
        environment["SUTTY_EVIDENCE_APPROVED"] = "1";
        environment["SUTTY_TEST_MODES"] = "smoke,fault";
        var multiModeConfiguration = LiveConfiguration.Load(ReadEnvironment);
        ExpectFailure(
            () => LiveEvidenceConfiguration.Load(multiModeConfiguration, ReadEnvironment),
            "Evidence output with multiple modes must fail closed.");
        environment["SUTTY_TEST_MODES"] = "fault";
        var faultConfiguration = LiveConfiguration.Load(ReadEnvironment);
        ExpectFailure(
            () => LiveEvidenceConfiguration.Load(faultConfiguration, ReadEnvironment),
            "A mismatched mode and gate id must fail closed.");

        environment["SUTTY_TEST_MODES"] = "connection-info";
        environment["SUTTY_EVIDENCE_COMMIT"] = new string('0', 40);
        ExpectFailure(
            () => LiveEvidenceConfiguration.Load(evidenceLiveConfiguration, ReadEnvironment),
            "An all-zero evidence commit must fail closed.");
        environment["SUTTY_EVIDENCE_COMMIT"] = new string('a', 40);
        environment["SUTTY_EVIDENCE_PACKAGE_SHA256"] = new string('0', 64);
        ExpectFailure(
            () => LiveEvidenceConfiguration.Load(evidenceLiveConfiguration, ReadEnvironment),
            "An all-zero package digest must fail closed.");

        Assert(ClassifyConnectionFailure("authentication denied") ==
               LiveFailureCategory.AuthenticationFailed,
            "Authentication failure classification changed.");
        Assert(ClassifyFailure(new TimeoutException()) == LiveFailureCategory.Timeout,
            "Timeout failure classification changed.");
    }
    finally
    {
        var resolvedScratch = Path.GetFullPath(scratch);
        if (Directory.Exists(resolvedScratch) &&
            resolvedScratch.StartsWith(temporaryBase, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(resolvedScratch).StartsWith(
                "sutty-live-evidence-self-test-",
                StringComparison.Ordinal))
        {
            Directory.Delete(resolvedScratch, recursive: true);
        }
    }
}

static Task RecordModeAsync(string mode, ICollection<string> invoked)
{
    invoked.Add(mode);
    return Task.CompletedTask;
}

static string FindProgramSourcePath()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
         directory is not null;
         directory = directory.Parent)
    {
        var projectPath = Path.Combine(directory.FullName, "sutty.LiveServer.SelfTest.csproj");
        var programPath = Path.Combine(directory.FullName, "Program.cs");
        if (File.Exists(projectPath) && File.Exists(programPath))
            return programPath;
    }

    throw new InvalidOperationException("The live harness source directory is unavailable.");
}

static void ExpectFailure(Action action, string message)
{
    try
    {
        action();
    }
    catch
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static async Task ExpectFailureAsync(Func<Task> action, string message)
{
    try
    {
        await action();
    }
    catch
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static void AssertSourceSliceContains(
    string source,
    string startMarker,
    string endMarker,
    string sliceName,
    params string[] requiredTokens)
{
    var start = source.IndexOf(startMarker, StringComparison.Ordinal);
    var end = start >= 0
        ? source.IndexOf(endMarker, start, StringComparison.Ordinal)
        : -1;
    Assert(start >= 0 && end > start, $"The {sliceName} source boundary is unavailable.");
    var slice = source[start..end];
    foreach (var requiredToken in requiredTokens)
    {
        Assert(slice.Contains(requiredToken, StringComparison.Ordinal),
            $"The {sliceName} live behavior no longer contains a required operation.");
    }
}

static string CreateScratch(string mode)
{
    var path = Path.Combine(Path.GetTempPath(), $"sutty-live-{mode}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(path);
    return path;
}

static LiveFailureCategory ClassifyFailure(Exception error)
{
    while (error is AggregateException { InnerExceptions.Count: 1 } aggregate)
        error = aggregate.InnerExceptions[0];
    if (error is LiveAcceptanceFailureException acceptanceFailure)
        return acceptanceFailure.Category;
    if (error is OperationCanceledException)
        return LiveFailureCategory.Canceled;
    if (error is TimeoutException)
        return LiveFailureCategory.Timeout;
    if (error is HostKeyChangedException or System.Security.SecurityException)
        return LiveFailureCategory.HostKeyRejected;

    return error.GetType().Name switch
    {
        "SshAuthenticationException" => LiveFailureCategory.AuthenticationFailed,
        "RoutePolicyViolationException" => LiveFailureCategory.RouteFailed,
        "ProxyException" or "SocketException" or "SshConnectionException" =>
            LiveFailureCategory.TransportFailed,
        _ when error is InvalidOperationException => LiveFailureCategory.AssertionFailed,
        _ => LiveFailureCategory.Unexpected,
    };
}

static LiveFailureCategory ClassifyConnectionFailure(string? processLocalDetail)
{
    var detail = processLocalDetail?.ToLowerInvariant() ?? "";
    if (detail.Contains("auth", StringComparison.Ordinal) ||
        detail.Contains("permission denied", StringComparison.Ordinal) ||
        detail.Contains("인증", StringComparison.Ordinal))
        return LiveFailureCategory.AuthenticationFailed;
    if (detail.Contains("host key", StringComparison.Ordinal) ||
        detail.Contains("fingerprint", StringComparison.Ordinal) ||
        detail.Contains("호스트 키", StringComparison.Ordinal))
        return LiveFailureCategory.HostKeyRejected;
    if (detail.Contains("route", StringComparison.Ordinal) ||
        detail.Contains("proxy", StringComparison.Ordinal) ||
        detail.Contains("socks", StringComparison.Ordinal) ||
        detail.Contains("jump", StringComparison.Ordinal) ||
        detail.Contains("경로", StringComparison.Ordinal))
        return LiveFailureCategory.RouteFailed;
    if (detail.Contains("timeout", StringComparison.Ordinal) ||
        detail.Contains("timed out", StringComparison.Ordinal) ||
        detail.Contains("시간 초과", StringComparison.Ordinal))
        return LiveFailureCategory.Timeout;
    return LiveFailureCategory.TransportFailed;
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

sealed record LiveModeOperations(
    Func<LiveConfiguration, Task> ConnectionInfo,
    Func<LiveConfiguration, LiveEvidenceConfiguration?, Task> DirectPasswordGate,
    Func<LiveConfiguration, Task> Smoke,
    Func<LiveConfiguration, Task> Fault,
    Func<LiveConfiguration, Task> Scale,
    Func<LiveConfiguration, Task> Soak);

enum LiveEvidenceResult
{
    Pass,
    Fail,
    Blocked,
}

enum LiveFailureCategory
{
    Canceled,
    Timeout,
    AuthenticationFailed,
    HostKeyRejected,
    RouteFailed,
    TransportFailed,
    AssertionFailed,
    Unexpected,
}

sealed class LiveAcceptanceFailureException(LiveFailureCategory category)
    : InvalidOperationException(category.ToString())
{
    public LiveFailureCategory Category { get; } = category;
}

sealed record LiveEvidenceConfiguration(
    string OutputRoot,
    string GateId,
    string Commit,
    string PackageSha256,
    string ServerFamily,
    string ServerVersion,
    bool RedactionReviewed)
{
    private static readonly Regex GatePattern = new(
        "^[A-Z0-9]+(?:-[A-Z0-9]+)+$",
        RegexOptions.CultureInvariant);
    private static readonly Regex CommitPattern = new(
        "^[0-9a-f]{40}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ServerFamilyPattern = new(
        "^[A-Za-z][A-Za-z0-9_+\u002D]{0,31}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ServerVersionPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._+~\u002D]{0,31}$",
        RegexOptions.CultureInvariant);

    public static LiveEvidenceConfiguration? Load(
        LiveConfiguration configuration,
        Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var outputRoot = readEnvironment("SUTTY_EVIDENCE_OUTPUT_DIR") ?? "";
        var approval = readEnvironment("SUTTY_EVIDENCE_APPROVED") ?? "";
        var gateId = readEnvironment("SUTTY_EVIDENCE_GATE_ID") ?? "";
        var commit = readEnvironment("SUTTY_EVIDENCE_COMMIT") ?? "";
        var packageSha256 = readEnvironment("SUTTY_EVIDENCE_PACKAGE_SHA256") ?? "";
        var serverFamily = readEnvironment("SUTTY_EVIDENCE_SERVER_FAMILY") ?? "";
        var serverVersion = readEnvironment("SUTTY_EVIDENCE_SERVER_VERSION") ?? "";
        var redactionReviewed = readEnvironment("SUTTY_EVIDENCE_REDACTION_REVIEWED") ?? "";
        var requested = new[]
        {
            outputRoot,
            approval,
            gateId,
            commit,
            packageSha256,
            serverFamily,
            serverVersion,
            redactionReviewed,
        }.Any(value => !string.IsNullOrWhiteSpace(value));
        if (!requested)
            return null;

        if (approval != "1" || redactionReviewed is not ("" or "0" or "1") ||
            gateId.Length > 64 ||
            commit.All(character => character == '0') ||
            packageSha256.All(character => character == '0') ||
            !Path.IsPathFullyQualified(outputRoot) ||
            string.Equals(
                Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetPathRoot(Path.GetFullPath(outputRoot))?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) ||
            !GatePattern.IsMatch(gateId) ||
            !CommitPattern.IsMatch(commit) ||
            !Sha256Pattern.IsMatch(packageSha256) ||
            !ServerFamilyPattern.IsMatch(serverFamily) ||
            !ServerVersionPattern.IsMatch(serverVersion))
        {
            throw new InvalidOperationException(
                "Live evidence configuration is incomplete or invalid; no connection was attempted.");
        }
        if (configuration.Modes.Count != 1 ||
            !GateMatchesMode(configuration.Modes.Single(), gateId, configuration.AuthMethod))
        {
            throw new InvalidOperationException(
                "Live evidence requires exactly one mode and its matching gate id.");
        }

        if (configuration.SensitiveValues.Any(value =>
                string.Equals(serverFamily, value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(serverVersion, value, StringComparison.OrdinalIgnoreCase) ||
                value.Length >= 4 &&
                (serverFamily.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                 serverVersion.Contains(value, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException(
                "Live evidence operator metadata overlaps sensitive connection data.");
        }

        return new LiveEvidenceConfiguration(
            Path.GetFullPath(outputRoot),
            gateId,
            commit,
            packageSha256,
            serverFamily,
            serverVersion,
            redactionReviewed == "1");
    }

    private static bool GateMatchesMode(
        string mode,
        string gateId,
        SshAuthMethod authentication) => mode switch
    {
        "connection-info" => gateId == "SSH-INFO-001",
        "direct-password-gate" => gateId == "SSH-LIVE-001" &&
                                  authentication == SshAuthMethod.Password,
        "fault" => gateId == "SSH-FAULT-001",
        "smoke" => (gateId, authentication) switch
        {
            ("SSH-LIVE-001", SshAuthMethod.Password) => true,
            ("SSH-LIVE-002", SshAuthMethod.PublicKey) => true,
            ("SSH-LIVE-003", SshAuthMethod.Agent) => true,
            ("SSH-LIVE-004", SshAuthMethod.KeyboardInteractive) => true,
            _ => false,
        },
        "scale" => gateId.StartsWith("XFER-LIVE-", StringComparison.Ordinal) ||
                   gateId.StartsWith("PERF-LIVE-", StringComparison.Ordinal),
        "soak" => gateId.StartsWith("SSH-SOAK-", StringComparison.Ordinal),
        _ => false,
    };
}

static class LiveEvidenceWriter
{
    public const string ManifestFileName = "manifest.yml";
    public const string SummaryFileName = "summary.json";

    public static async Task<string> WriteAsync(
        LiveEvidenceConfiguration evidence,
        LiveConfiguration configuration,
        DateTimeOffset startedAtUtc,
        TimeSpan elapsed,
        LiveEvidenceResult result,
        string phase,
        LiveFailureCategory? failureCategory = null)
    {
        var safePhase = SafePhase(phase);
        var started = startedAtUtc.ToUniversalTime();
        var durationSeconds = (long)Math.Ceiling(Math.Max(0, elapsed.TotalSeconds));
        var startedText = started.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture);
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new InvalidOperationException(
                "Live evidence supports only x64 and arm64 Windows runners."),
        };
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Live evidence requires a Windows runner.");

        var windowsBuild = Environment.OSVersion.Version.ToString();
        var route = "Direct";
        var authentication = configuration.AuthMethod.ToString();
        var expectedHostFingerprint = string.IsNullOrWhiteSpace(configuration.ExpectedHostKeySha256)
            ? "NotRecorded"
            : "SHA256:[redacted]";
        var resultText = result.ToString();
        var manifest = string.Join('\n', new[]
        {
            "schema_version: 1",
            $"gate_id: {YamlString(evidence.GateId)}",
            $"commit: {YamlString(evidence.Commit)}",
            $"package_sha256: {YamlString(evidence.PackageSha256)}",
            $"windows_build: {YamlString(windowsBuild)}",
            $"architecture: {YamlString(architecture)}",
            $"server_family: {YamlString(evidence.ServerFamily)}",
            $"server_version: {YamlString(evidence.ServerVersion)}",
            $"route: {YamlString(route)}",
            $"authentication: {YamlString(authentication)}",
            $"expected_host_fingerprint: {YamlString(expectedHostFingerprint)}",
            $"result: {YamlString(resultText)}",
            $"started_at_utc: {YamlString(startedText)}",
            $"duration_seconds: {durationSeconds.ToString(CultureInfo.InvariantCulture)}",
            "evidence_files:",
            $"  - {YamlString(SummaryFileName)}",
            $"redaction_reviewed: {evidence.RedactionReviewed.ToString().ToLowerInvariant()}",
            "",
        });
        var summaryDocument = new Dictionary<string, object?>
        {
            ["schema_version"] = 1,
            ["gate_id"] = evidence.GateId,
            ["result"] = resultText,
            ["started_at_utc"] = startedText,
            ["duration_seconds"] = durationSeconds,
            ["check_id"] = safePhase,
            ["checks"] = OrderedChecks(configuration.Modes).Select(check => new
            {
                id = check,
                result = result == LiveEvidenceResult.Fail ? "Fail" : "Pass",
            }).ToArray(),
            ["redaction_reviewed"] = evidence.RedactionReviewed,
            ["privacy_notice"] =
                "Connection identifiers, credentials, filesystem locations, session content, and cryptographic material are excluded.",
        };
        if (result == LiveEvidenceResult.Blocked)
            summaryDocument["blocking_category"] = "ManualGateCoverageRequired";
        if (failureCategory is not null)
            summaryDocument["failure_category"] = failureCategory.Value.ToString();
        var summary = JsonSerializer.Serialize(
            summaryDocument,
            new JsonSerializerOptions { WriteIndented = true }) + "\n";

        AssertPrivacySafe(manifest, summary, configuration);

        Directory.CreateDirectory(evidence.OutputRoot);
        var identifier = Guid.NewGuid().ToString("N");
        var bundleSuffix = string.Join(
            '-',
            started.ToString("yyyyMMdd't'HHmmss'z'", CultureInfo.InvariantCulture),
            evidence.Commit[..12],
            identifier[..12]);
        var maximumGateSegmentLength = 64 - bundleSuffix.Length - 1;
        var gateSegment = evidence.GateId.ToLowerInvariant();
        if (gateSegment.Length > maximumGateSegmentLength)
            gateSegment = gateSegment[..maximumGateSegmentLength].TrimEnd('-');
        var bundleName = $"{gateSegment}-{bundleSuffix}";
        var finalPath = Path.Combine(evidence.OutputRoot, bundleName);
        var stagingPath = Path.Combine(
            evidence.OutputRoot,
            $".sutty-evidence-staging-{identifier}");
        if (Directory.Exists(finalPath) || File.Exists(finalPath))
            throw new InvalidOperationException("Live evidence bundle already exists.");

        Directory.CreateDirectory(stagingPath);
        try
        {
            await WriteDurableTextAsync(Path.Combine(stagingPath, SummaryFileName), summary);
            await WriteDurableTextAsync(Path.Combine(stagingPath, ManifestFileName), manifest);
            Directory.Move(stagingPath, finalPath);
            return finalPath;
        }
        catch
        {
            try
            {
                if (Directory.Exists(stagingPath))
                    Directory.Delete(stagingPath, recursive: true);
            }
            catch
            {
                // Preserve the original publication failure; staging paths are never reported.
            }
            throw;
        }
    }

    private static string YamlString(string value) => JsonSerializer.Serialize(value);

    private static string[] OrderedChecks(IReadOnlySet<string> modes)
    {
        if (modes.SetEquals(new[] { "direct-password-gate" }))
        {
            return
            [
                "package-sha256",
                "package-commit-identity",
                "package-core-identity",
                "authentication-success",
                "authentication-rejection",
                "host-key-rejection",
                "connection-cancellation",
                "transport-timeout",
                "negotiated-reconnect",
                "command-pty-sftp",
                "remote-local-cleanup",
                "server-session-audit",
            ];
        }

        return new[] { "connection-info", "smoke", "fault", "scale", "soak" }
            .Where(modes.Contains)
            .ToArray();
    }

    public static string SafePhase(string phase) => phase switch
    {
        "configuration" or "connection-info" or "direct-password-gate" or "smoke" or "fault" or "scale" or
        "soak" or "evidence" or "complete" or "self-test" => phase,
        _ => "unknown",
    };

    private static void AssertPrivacySafe(
        string manifest,
        string summary,
        LiveConfiguration configuration)
    {
        var rawPrivateKey = Environment.GetEnvironmentVariable("SUTTY_TEST_SSH_PRIVATE_KEY") ?? "";
        foreach (var sensitiveValue in configuration.SensitiveValues.Append(rawPrivateKey))
        {
            if (sensitiveValue.Length >= 4 &&
                (manifest.Contains(sensitiveValue, StringComparison.OrdinalIgnoreCase) ||
                 summary.Contains(sensitiveValue, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "Live evidence redaction validation failed; no bundle was published.");
            }
        }
    }

    private static async Task WriteDurableTextAsync(string path, string content)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
        stream.Flush(flushToDisk: true);
    }
}

sealed record LiveConfiguration(
    string Host,
    int Port,
    string Username,
    SshAuthMethod AuthMethod,
    string Password,
    string PrivateKeyPath,
    string PrivateKeyPassphrase,
    string ExpectedHostKeySha256,
    bool TrustNewHost,
    string RemoteRoot,
    string BlackholeHost,
    int BlackholePort,
    string ServerAuditCommand,
    string PackagePath,
    IReadOnlySet<string> Modes,
    IReadOnlyList<string> KeyboardInteractiveAnswers,
    int SessionCount,
    int SoakMinutes,
    int LargeGigabytes,
    int FileCount,
    int FaultMegabytes)
{
    public IEnumerable<string> SensitiveValues => new[]
    {
        Host,
        Username,
        Password,
        PrivateKeyPath,
        PrivateKeyPassphrase,
        ExpectedHostKeySha256,
        RemoteRoot,
        BlackholeHost,
        ServerAuditCommand,
        PackagePath,
    }.Concat(KeyboardInteractiveAnswers).Where(value => !string.IsNullOrEmpty(value));

    public bool CanTrust(string fingerprint) => TrustNewHost ||
        MatchesExpectedFingerprint(fingerprint);

    public bool MatchesExpectedFingerprint(string fingerprint) =>
        !string.IsNullOrWhiteSpace(ExpectedHostKeySha256) &&
        string.Equals(
            ExpectedHostKeySha256.Trim().TrimEnd('='),
            fingerprint.Trim().TrimEnd('='),
            StringComparison.OrdinalIgnoreCase);

    public static LiveConfiguration Load(Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var host = Required(readEnvironment, "SUTTY_TEST_SSH_HOST");
        var username = Required(readEnvironment, "SUTTY_TEST_SSH_USER");
        var password = readEnvironment("SUTTY_TEST_SSH_PASSWORD") ?? "";
        var keyPath = readEnvironment("SUTTY_TEST_SSH_KEY_PATH") ?? "";
        var authName = readEnvironment("SUTTY_TEST_SSH_AUTH") ??
            (keyPath.Length > 0 ? "PublicKey" : password.Length > 0 ? "Password" : "Agent");
        if (!Enum.TryParse<SshAuthMethod>(authName, true, out var authMethod) || !Enum.IsDefined(authMethod))
            throw new InvalidOperationException("SUTTY_TEST_SSH_AUTH is invalid.");
        var modes = (readEnvironment("SUTTY_TEST_MODES") ?? "smoke")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        if (modes.Count == 0)
            throw new InvalidOperationException("SUTTY_TEST_MODES must select at least one mode.");
        if (modes.Any(mode => mode is not "connection-info" and not "smoke" and not "fault" and
                              not "scale" and not "soak" and not "direct-password-gate"))
            throw new InvalidOperationException("SUTTY_TEST_MODES contains an unknown mode.");
        var expectedKey = readEnvironment("SUTTY_TEST_HOST_KEY_SHA256") ?? "";
        var trustNew = Bool(readEnvironment, "SUTTY_TEST_TRUST_NEW_HOST");
        if (expectedKey.Length == 0 && !trustNew)
            throw new InvalidOperationException(
                "Set SUTTY_TEST_HOST_KEY_SHA256, or explicitly set SUTTY_TEST_TRUST_NEW_HOST=1.");

        var blackholeHost = readEnvironment("SUTTY_TEST_BLACKHOLE_HOST") ?? host;
        var blackholePort = Int(readEnvironment, "SUTTY_TEST_BLACKHOLE_PORT", 0, 0, 65_535);
        var serverAuditCommand = readEnvironment("SUTTY_TEST_SERVER_AUDIT_COMMAND") ?? "";
        var packagePath = readEnvironment("SUTTY_TEST_PACKAGE_PATH") ?? "";
        if (modes.Contains("direct-password-gate"))
        {
            if (modes.Count != 1 || authMethod != SshAuthMethod.Password ||
                string.IsNullOrEmpty(password) || trustNew ||
                !Regex.IsMatch(
                    expectedKey,
                    "^SHA256:[A-Za-z0-9+/]{43}={0,1}$",
                    RegexOptions.CultureInvariant) ||
                string.IsNullOrWhiteSpace(blackholeHost) || blackholePort == 0 ||
                serverAuditCommand != "sutty-lab-audit-summary" ||
                !Path.IsPathFullyQualified(packagePath) || !File.Exists(packagePath) ||
                !string.Equals(
                    Path.GetFileName(packagePath),
                    "Sutty-v0.1.0-alpha.4-win-x64.zip",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "direct-password-gate requires an isolated Password target, pinned host key, " +
                    "blackhole endpoint, canonical server audit command, and exact absolute ZIP path.");
            }
        }

        return new LiveConfiguration(
            host,
            Int(readEnvironment, "SUTTY_TEST_SSH_PORT", 22, 1, 65_535),
            username,
            authMethod,
            password,
            keyPath,
            readEnvironment("SUTTY_TEST_SSH_KEY_PASSPHRASE") ?? "",
            expectedKey,
            trustNew,
            readEnvironment("SUTTY_TEST_REMOTE_ROOT") ?? "/tmp",
            blackholeHost,
            blackholePort,
            serverAuditCommand,
            packagePath,
            modes,
            (readEnvironment("SUTTY_TEST_KBI_ANSWERS") ?? "")
                .Split('|', StringSplitOptions.RemoveEmptyEntries),
            Int(readEnvironment, "SUTTY_TEST_SESSION_COUNT", 16, 1, 16),
            Int(readEnvironment, "SUTTY_TEST_SOAK_MINUTES", 60, 1, 10_080),
            Int(readEnvironment, "SUTTY_TEST_LARGE_GB", 100, 1, 1_024),
            Int(readEnvironment, "SUTTY_TEST_FILE_COUNT", 100_000, 1, 1_000_000),
            Int(readEnvironment, "SUTTY_TEST_FAULT_MB", 512, 16, 1_048_576));
    }

    private static string Required(Func<string, string?> readEnvironment, string name) =>
        readEnvironment(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required environment variable is missing: {name}");

    private static bool Bool(Func<string, string?> readEnvironment, string name) =>
        readEnvironment(name) is "1" or "true" or "TRUE";

    private static int Int(
        Func<string, string?> readEnvironment,
        string name,
        int fallback,
        int minimum,
        int maximum) =>
        int.TryParse(readEnvironment(name), out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
}
