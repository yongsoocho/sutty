using sutty.Core.Models;
using sutty.Core.Security;
using sutty.Core.Sessions;
using sutty.Core.Sftp;
using sutty.Core.Terminal;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

var configuration = LiveConfiguration.Load();
var modes = configuration.Modes;
Console.WriteLine($"Live acceptance target: {configuration.Username}@{configuration.Host}:{configuration.Port}");
Console.WriteLine($"Modes: {string.Join(", ", modes)}");

if (modes.Contains("smoke"))
    await RunSmokeAsync(configuration);
if (modes.Contains("fault"))
    await RunFaultInjectionAsync(configuration);
if (modes.Contains("scale"))
    await RunScaleAsync(configuration);
if (modes.Contains("soak"))
    await RunSoakAsync(configuration);

Console.WriteLine("Live SSH/SFTP acceptance tests passed.");

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
            $"SFTP is not ready: {session.LastSftpError}");

        var command = await session.ExecuteCommandAsync("printf 'sutty-live-command'");
        Assert(command.Succeeded && command.StandardOutput == "sutty-live-command",
            $"SSH exec failed: {command.StandardError}");

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
    Assert(session.State == SessionState.Connected,
        $"SSH connection failed: {session.LastError}");
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

    if (!string.IsNullOrWhiteSpace(configuration.ExpectedHostKeySha256))
    {
        Assert(configuration.CanTrust(hostKeyFingerprint),
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
    catch (Exception error)
    {
        Console.Error.WriteLine($"Remote cleanup warning: {error.Message}");
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

static string CreateScratch(string mode)
{
    var path = Path.Combine(Path.GetTempPath(), $"sutty-live-{mode}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(path);
    return path;
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
    IReadOnlySet<string> Modes,
    IReadOnlyList<string> KeyboardInteractiveAnswers,
    int SessionCount,
    int SoakMinutes,
    int LargeGigabytes,
    int FileCount,
    int FaultMegabytes)
{
    public bool CanTrust(string fingerprint) => TrustNewHost ||
        !string.IsNullOrWhiteSpace(ExpectedHostKeySha256) &&
        string.Equals(
            ExpectedHostKeySha256.Trim().TrimEnd('='),
            fingerprint.Trim().TrimEnd('='),
            StringComparison.OrdinalIgnoreCase);

    public static LiveConfiguration Load()
    {
        var host = Required("SUTTY_TEST_SSH_HOST");
        var username = Required("SUTTY_TEST_SSH_USER");
        var password = Environment.GetEnvironmentVariable("SUTTY_TEST_SSH_PASSWORD") ?? "";
        var keyPath = Environment.GetEnvironmentVariable("SUTTY_TEST_SSH_KEY_PATH") ?? "";
        var authName = Environment.GetEnvironmentVariable("SUTTY_TEST_SSH_AUTH") ??
            (keyPath.Length > 0 ? "PublicKey" : password.Length > 0 ? "Password" : "Agent");
        if (!Enum.TryParse<SshAuthMethod>(authName, true, out var authMethod) || !Enum.IsDefined(authMethod))
            throw new InvalidOperationException("SUTTY_TEST_SSH_AUTH is invalid.");
        var modes = (Environment.GetEnvironmentVariable("SUTTY_TEST_MODES") ?? "smoke")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        if (modes.Any(mode => mode is not "smoke" and not "fault" and not "scale" and not "soak"))
            throw new InvalidOperationException("SUTTY_TEST_MODES contains an unknown mode.");
        var expectedKey = Environment.GetEnvironmentVariable("SUTTY_TEST_HOST_KEY_SHA256") ?? "";
        var trustNew = Bool("SUTTY_TEST_TRUST_NEW_HOST");
        if (expectedKey.Length == 0 && !trustNew)
            throw new InvalidOperationException(
                "Set SUTTY_TEST_HOST_KEY_SHA256, or explicitly set SUTTY_TEST_TRUST_NEW_HOST=1.");

        return new LiveConfiguration(
            host,
            Int("SUTTY_TEST_SSH_PORT", 22, 1, 65_535),
            username,
            authMethod,
            password,
            keyPath,
            Environment.GetEnvironmentVariable("SUTTY_TEST_SSH_KEY_PASSPHRASE") ?? "",
            expectedKey,
            trustNew,
            Environment.GetEnvironmentVariable("SUTTY_TEST_REMOTE_ROOT") ?? "/tmp",
            modes,
            (Environment.GetEnvironmentVariable("SUTTY_TEST_KBI_ANSWERS") ?? "")
                .Split('|', StringSplitOptions.RemoveEmptyEntries),
            Int("SUTTY_TEST_SESSION_COUNT", 16, 1, 16),
            Int("SUTTY_TEST_SOAK_MINUTES", 60, 1, 10_080),
            Int("SUTTY_TEST_LARGE_GB", 100, 1, 1_024),
            Int("SUTTY_TEST_FILE_COUNT", 100_000, 1, 1_000_000),
            Int("SUTTY_TEST_FAULT_MB", 512, 16, 1_048_576));
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required environment variable is missing: {name}");

    private static bool Bool(string name) =>
        Environment.GetEnvironmentVariable(name) is "1" or "true" or "TRUE";

    private static int Int(string name, int fallback, int minimum, int maximum) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
}
