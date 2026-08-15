using sutty.Core.Security;
using sutty.Core.Diagnostics;
using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Core.Sessions;
using Renci.SshNet;
using Renci.SshNet.Common;
using SshNet.Agent;
using System.Text.Json.Nodes;

AssertSshNet2026PublicApi();
AssertSshAgentAdapterLoads();

ConnectionLogStore.Clear();
var logPassword = CreateTestSecret("log-password");
var logPassphrase = CreateTestSecret("log-passphrase");
var logToken = CreateTestSecret("log-token");
var logNamedSecret = CreateTestSecret("log-secret");
var logProxySecret = CreateTestSecret("proxy-password");
ConnectionLogStore.Append(
    Guid.NewGuid(),
    "diagnostic-test",
    "server.internal:22",
    ConnectionLogSeverity.Error,
    "authentication",
    $"password={logPassword} passphrase:{logPassphrase}",
    $"token={logToken} secret='{logNamedSecret}'",
    $"proxy=https://operator:{logProxySecret}@proxy.internal:8443");
var sanitizedLog = ConnectionLogStore.Snapshot().Single();
Assert(!sanitizedLog.MessageKo.Contains(logPassword, StringComparison.Ordinal),
    "connection log redacts password values");
Assert(!sanitizedLog.MessageKo.Contains(logPassphrase, StringComparison.Ordinal),
    "connection log redacts passphrase values");
Assert(!sanitizedLog.MessageEn.Contains(logNamedSecret, StringComparison.Ordinal),
    "connection log redacts named secret values");
Assert(!sanitizedLog.Detail!.Contains(logProxySecret, StringComparison.Ordinal),
    "connection log redacts URI user info");
ConnectionLogStore.Clear();

var failedConnectionPassword = CreateTestSecret("failed-connection");
var failedSession = new SshNetSession(new SshConnectionInfo
{
    Host = "127.0.0.1",
    Port = 1,
    Username = "diagnostic-user",
    AuthMethod = SshAuthMethod.Password,
    Password = failedConnectionPassword,
});
using (var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
    await failedSession.ConnectAsync(connectionTimeout.Token);
Assert(failedSession.State == SessionState.Failed,
    "refused SSH connection enters failed state");
var failedConnectionLogs = ConnectionLogStore.Snapshot()
    .Where(entry => entry.SessionId == failedSession.Id)
    .ToArray();
Assert(failedConnectionLogs.Any(entry => entry.Severity >= ConnectionLogSeverity.Error),
    "failed SSH connection emits an error diagnostic");
Assert(failedConnectionLogs.Any(entry => entry.Severity == ConnectionLogSeverity.Verbose),
    "failed SSH connection captures verbose SSH.NET diagnostics");
Assert(!string.Join('\n', failedConnectionLogs.Select(entry =>
        $"{entry.MessageKo}\n{entry.MessageEn}\n{entry.Detail}"))
    .Contains(failedConnectionPassword, StringComparison.Ordinal),
    "SSH.NET verbose diagnostics exclude the password");
await failedSession.DisconnectAsync();
ConnectionLogStore.Clear();

var proxyPassword = CreateTestSecret("proxy-route");
var proxyRoute = RouteResolver.Resolve(
    new ConnectionRoute
    {
        Id = "corp-proxy",
        Type = ConnectionRouteType.Socks5,
        Host = "proxy.internal",
        Port = 1080,
        Username = "proxy-user",
        Password = proxyPassword,
    },
    new ConnectionRoutePolicy
    {
        EnterpriseMode = true,
        AllowedRouteTypes = [ConnectionRouteType.Socks5],
    });
Assert(proxyRoute.Type == ConnectionRouteType.Socks5, "enterprise proxy route resolution");
AssertThrows<RoutePolicyViolationException>(
    () => RouteResolver.Resolve(
        new ConnectionRoute(),
        new ConnectionRoutePolicy { EnterpriseMode = true }),
    "enterprise route policy blocks direct fallback");

var proxyCommandRoute = RouteResolver.Resolve(
    new ConnectionRoute
    {
        Type = ConnectionRouteType.ExternalProxyCommand,
        Command = "ssh -W %h:%p jump.example",
    },
    new ConnectionRoutePolicy());
Assert(proxyCommandRoute.Command.Contains("%h:%p", StringComparison.Ordinal),
    "ProxyCommand route accepts endpoint placeholders without a proxy host field");
var expandedProxyCommand = ProxyCommandTemplate.Expand(
    proxyCommandRoute.Command,
    "server.internal",
    22,
    "domain\\operator");
Assert(expandedProxyCommand.Contains("\"server.internal\":22", StringComparison.Ordinal) &&
       expandedProxyCommand.Contains("jump.example", StringComparison.Ordinal),
    "ProxyCommand endpoint placeholders are quoted before cmd.exe execution");
AssertThrows<RoutePolicyViolationException>(
    () => ProxyCommandTemplate.Expand(
        proxyCommandRoute.Command,
        "server.internal & whoami",
        22,
        "operator"),
    "ProxyCommand rejects shell metacharacters in target substitutions");
Assert(!ForwardingExposurePolicy.IsExternalBind("127.0.0.1") &&
       !ForwardingExposurePolicy.IsExternalBind("::1") &&
       ForwardingExposurePolicy.IsExternalBind("0.0.0.0") &&
       ForwardingExposurePolicy.IsExternalBind("::"),
    "forwarding exposure policy distinguishes loopback and external listeners");

var jumpRoute = RouteResolver.Resolve(
    new ConnectionRoute
    {
        Type = ConnectionRouteType.SshJump,
        Host = "jump.example",
        Port = 22,
        Username = "jump-user",
        AuthMethod = SshAuthMethod.Agent,
    },
    new ConnectionRoutePolicy());
Assert(jumpRoute.Type == ConnectionRouteType.SshJump &&
       jumpRoute.AuthMethod == SshAuthMethod.Agent,
    "SSH jump route retains its authentication method");

var auditContext = AuditContext.Create(
    new SshConnectionInfo
    {
        Host = "server.internal",
        Port = 22,
        Username = "operator",
    },
    proxyRoute);
Assert(auditContext.RouteId == "corp-proxy", "audit context carries route id");
Assert(auditContext.CorrelationId.Length == 32, "audit context correlation id");
Assert(!auditContext.ToString().Contains(proxyPassword, StringComparison.Ordinal),
    "audit context excludes proxy credentials");

var scratch = Path.Combine(
    Path.GetTempPath(),
    $"sutty-host-key-self-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratch);

try
{
    var endpoint = HostEndpointIdentity.Create(" EXAMPLE.COM. ", 22);
    Assert(endpoint.Value == "[example.com]:22", "DNS endpoint normalization");
    Assert(HostEndpointIdentity.Parse(endpoint.Value) == endpoint, "endpoint round trip");
    Assert(
        HostEndpointIdentity.Create("[2001:0DB8:0:0::1]", 2202).Value == "[2001:db8::1]:2202",
        "IPv6 endpoint normalization");

    var source = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
    var firstKey = HostKeyData.Create("ssh-ed25519", source);
    source[0] ^= 0xff;
    Assert(firstKey.RawKey.Span[0] == 0, "host key owns an immutable copy");

    var fingerprintWithoutPrefix = firstKey.Sha256Fingerprint["SHA256:".Length..];
    var verifiedCopy = HostKeyData.CreateVerified(
        "ssh-ed25519",
        firstKey.RawKey.Span,
        fingerprintWithoutPrefix);
    Assert(firstKey.HasSameRawKey(verifiedCopy), "fingerprint verification");

    var changedKey = HostKeyData.Create(
        "ssh-ed25519",
        Enumerable.Repeat((byte)0xa5, 64).ToArray());
    var changedAlgorithm = HostKeyData.Create("ssh-rsa", firstKey.RawKey.Span);
    var storePath = Path.Combine(scratch, "known-hosts.json");
    var store = new KnownHostsStore(storePath);
    var connection = new HostKeyTrustContext(store);

    Assert(
        connection.Evaluate(endpoint, firstKey).State == HostKeyTrustState.Unknown,
        "new endpoint is unknown");
    Assert(
        connection.ApplyDecision(endpoint, firstKey, HostKeyDecision.TrustOnce),
        "trust once is accepted");
    Assert(
        connection.Evaluate(endpoint, firstKey) is
        { State: HostKeyTrustState.Trusted, Source: HostKeyTrustSource.Connection },
        "trust once is scoped to the connection");
    Assert(
        connection.Evaluate(endpoint, changedKey).State == HostKeyTrustState.Changed,
        "changed key is detected inside one connection");
    Assert(
        connection.Evaluate(endpoint, changedAlgorithm).State == HostKeyTrustState.Changed,
        "algorithm mismatch is detected even when raw bytes match");
    AssertThrows<HostKeyChangedException>(
        () => connection.ApplyDecision(endpoint, changedKey, HostKeyDecision.TrustAndSave),
        "changed trust-once key cannot be persisted");

    var otherConnection = new HostKeyTrustContext(store);
    Assert(
        otherConnection.Evaluate(endpoint, firstKey).State == HostKeyTrustState.Unknown,
        "trust once is not shared with another connection");
    Assert(
        otherConnection.ApplyDecision(endpoint, firstKey, HostKeyDecision.TrustAndSave),
        "trust and save is accepted");
    Assert(File.Exists(storePath), "known-host file is created");
    Assert(!Directory.EnumerateFiles(scratch, "*.tmp").Any(), "atomic-save temp file is removed");

    var persistedBeforeChangedAttempt = File.ReadAllBytes(storePath);
    AssertThrows<HostKeyChangedException>(
        () => store.Trust(endpoint, changedKey),
        "persistent changed key cannot replace a trusted key");
    Assert(
        persistedBeforeChangedAttempt.SequenceEqual(File.ReadAllBytes(storePath)),
        "changed-key rejection leaves persistent data untouched");

    var reloaded = new HostKeyTrustContext(new KnownHostsStore(storePath));
    Assert(
        reloaded.Evaluate(endpoint, firstKey) is
        { State: HostKeyTrustState.Trusted, Source: HostKeyTrustSource.Persistent },
        "saved key survives reload");
    Assert(
        reloaded.Evaluate(endpoint, changedKey).State == HostKeyTrustState.Changed,
        "saved changed key is detected");
    AssertThrows<HostKeyChangedException>(
        () => reloaded.ApplyDecision(endpoint, changedKey, HostKeyDecision.TrustOnce),
        "saved changed key cannot be trusted once");

    Parallel.For(0, 32, _ =>
    {
        Assert(
            reloaded.ApplyDecision(endpoint, firstKey, HostKeyDecision.TrustOnce),
            "parallel matching decision");
        Assert(
            reloaded.Evaluate(endpoint, firstKey).State == HostKeyTrustState.Trusted,
            "parallel matching evaluation");
    });

    var parallelEndpoint = HostEndpointIdentity.Create("parallel.example", 22);
    var parallelConnection = new HostKeyTrustContext(store);
    Parallel.For(0, 32, _ =>
        Assert(
            parallelConnection.ApplyDecision(
                parallelEndpoint,
                firstKey,
                HostKeyDecision.TrustOnce),
            "parallel trust-once decision"));
    Assert(
        parallelConnection.Evaluate(parallelEndpoint, firstKey) is
        { State: HostKeyTrustState.Trusted, Source: HostKeyTrustSource.Connection },
        "parallel trust-once state is consistent");

    var document = JsonNode.Parse(File.ReadAllText(storePath))!.AsObject();
    document["hosts"]!.AsArray()[0]!["sha256Fingerprint"] =
        "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    File.WriteAllText(storePath, document.ToJsonString());
    AssertThrows<InvalidDataException>(
        () => new KnownHostsStore(storePath).Find(endpoint),
        "tampered fingerprint fails closed");

    var vaultDirectory = Path.Combine(scratch, "credential-vault");
    var password = CreateTestSecret("vault-password");
    var passphrase = CreateTestSecret("vault-passphrase");
    var routePassword = CreateTestSecret("vault-route-password");
    var routePassphrase = CreateTestSecret("vault-route-passphrase");
    string credentialId;
    using (var vault = new LocalCredentialVault(vaultDirectory))
    {
        credentialId = vault.Store(new CredentialSecret(
            password, passphrase, routePassword, routePassphrase));
        Assert(vault.TryRead(credentialId, out var restored), "credential can be read");
        Assert(restored?.Password == password &&
               restored.PrivateKeyPassphrase == passphrase &&
               restored.RoutePassword == routePassword &&
               restored.RoutePrivateKeyPassphrase == routePassphrase,
            "credential round trip");
        Assert(vault.GetMetadata().Single().Id == credentialId, "credential metadata");
    }

    var vaultDocumentPath = Path.Combine(vaultDirectory, "vault.json");
    var vaultKeyPath = Path.Combine(vaultDirectory, "vault.key");
    var vaultDocumentText = File.ReadAllText(vaultDocumentPath);
    var vaultKeyText = File.ReadAllText(vaultKeyPath);
    Assert(!vaultDocumentText.Contains(password, StringComparison.Ordinal),
        "vault document excludes password plaintext");
    Assert(!vaultDocumentText.Contains(passphrase, StringComparison.Ordinal),
        "vault document excludes passphrase plaintext");
    Assert(!vaultDocumentText.Contains(routePassword, StringComparison.Ordinal),
        "vault document excludes route password plaintext");
    Assert(!vaultDocumentText.Contains(routePassphrase, StringComparison.Ordinal),
        "vault document excludes route passphrase plaintext");
    Assert(!vaultKeyText.Contains(password, StringComparison.Ordinal),
        "protected key file excludes credential plaintext");

    using (var reloadedVault = new LocalCredentialVault(vaultDirectory))
    {
        Assert(reloadedVault.TryRead(credentialId, out var restored),
            "credential survives vault reload");
        Assert(restored?.Password == password, "reloaded credential value");
        var updatedPassword = CreateTestSecret("updated-vault-password");
        reloadedVault.Store(new CredentialSecret(updatedPassword, ""), credentialId);
        Assert(reloadedVault.TryRead(credentialId, out var updated) &&
               updated?.Password == updatedPassword,
            "credential update");
        var disposableId = reloadedVault.Store(new CredentialSecret(
            CreateTestSecret("disposable-vault-value"), ""));
        Assert(reloadedVault.Delete(disposableId), "credential delete");
        Assert(!reloadedVault.TryRead(disposableId, out _), "deleted credential stays deleted");
    }

    var vaultDocument = JsonNode.Parse(File.ReadAllText(vaultDocumentPath))!.AsObject();
    var tag = vaultDocument["entries"]!.AsArray()[0]!["tag"]!.GetValue<string>();
    vaultDocument["entries"]!.AsArray()[0]!["tag"] =
        (tag[0] == 'A' ? "B" : "A") + tag[1..];
    File.WriteAllText(vaultDocumentPath, vaultDocument.ToJsonString());
    using (var tamperedVault = new LocalCredentialVault(vaultDirectory))
    {
        AssertThrows<System.Security.Cryptography.AuthenticationTagMismatchException>(
            () => tamperedVault.TryRead(credentialId, out _),
            "tampered credential fails closed");
    }

    var missingVersionDirectory = Path.Combine(scratch, "missing-version-vault");
    Directory.CreateDirectory(missingVersionDirectory);
    File.Copy(vaultKeyPath, Path.Combine(missingVersionDirectory, "vault.key"));
    File.WriteAllText(Path.Combine(missingVersionDirectory, "vault.json"), "{\"entries\":[]}");
    using (var missingVersionVault = new LocalCredentialVault(missingVersionDirectory))
    {
        AssertThrows<InvalidDataException>(
            () => missingVersionVault.GetMetadata(),
            "credential vault version is required");
    }

    Console.WriteLine("Host-key and credential-vault security self-tests passed.");
}
finally
{
    Directory.Delete(scratch, recursive: true);
}

static void Assert(bool condition, string name)
{
    if (!condition)
        throw new InvalidOperationException($"Self-test failed: {name}.");
}

static void AssertThrows<TException>(Action action, string name)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Self-test failed: {name} did not throw {typeof(TException).Name}.");
}

static void AssertSshNet2026PublicApi()
{
    var eventArgs = typeof(HostKeyEventArgs);
    Assert(eventArgs.GetProperty(nameof(HostKeyEventArgs.CanTrust)) is
    { PropertyType: { } type, CanRead: true, CanWrite: true } && type == typeof(bool),
        "SSH.NET CanTrust API");
    Assert(eventArgs.GetProperty(nameof(HostKeyEventArgs.HostKey))?.PropertyType == typeof(byte[]),
        "SSH.NET raw HostKey API");
    Assert(eventArgs.GetProperty(nameof(HostKeyEventArgs.HostKeyName))?.PropertyType == typeof(string),
        "SSH.NET HostKeyName API");
    Assert(eventArgs.GetProperty(nameof(HostKeyEventArgs.FingerPrintSHA256))?.PropertyType == typeof(string),
        "SSH.NET SHA-256 fingerprint API");

    foreach (var clientType in new[] { typeof(SshClient), typeof(SftpClient) })
    {
        var hostKeyEvent = clientType.GetEvent(nameof(BaseClient.HostKeyReceived));
        Assert(hostKeyEvent?.EventHandlerType == typeof(EventHandler<HostKeyEventArgs>),
            $"SSH.NET {clientType.Name} HostKeyReceived API");
    }

    Assert(
        typeof(ShellStream).GetMethod(
            nameof(ShellStream.ChangeWindowSize),
            [typeof(uint), typeof(uint), typeof(uint), typeof(uint)]) is not null,
        "SSH.NET runtime PTY resize API");
}

static void AssertSshAgentAdapterLoads()
{
    var requireLiveAgent = string.Equals(
        Environment.GetEnvironmentVariable("SUTTY_REQUIRE_WINDOWS_AGENT"),
        "true",
        StringComparison.OrdinalIgnoreCase);
    try
    {
        var agent = new SshAgent(TimeSpan.FromMilliseconds(500));
        var identities = agent.RequestIdentities();
        Assert(identities.All(identity => identity is IPrivateKeySource),
            "Windows SSH Agent identities implement the SSH.NET key-source contract");
        if (requireLiveAgent)
        {
            Assert(identities.Length > 0,
                "required Windows SSH Agent contains at least one test identity");
        }
    }
    catch (Exception ex) when (ex is SshAgentException or TimeoutException or IOException)
    {
        if (requireLiveAgent)
        {
            throw new InvalidOperationException(
                "A live Windows SSH Agent was required but could not be queried.",
                ex);
        }

        // The Windows service or named pipe is optional on a build machine. Reaching the
        // adapter-specific transport still proves that the adapter loaded against SSH.NET.
    }
}

static string CreateTestSecret(string purpose) =>
    string.Concat("sutty-self-test-", purpose, "-", Guid.NewGuid().ToString("N"));
