using sutty.Core.Security;
using sutty.Core.Diagnostics;
using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Core.Sessions;
using sutty.Core.Sftp;
using sutty.Core.Terminal;
using Renci.SshNet;
using Renci.SshNet.Common;
using SshNet.Agent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

if (args.Length == 3 &&
    string.Equals(args[0], "--known-hosts-lock-worker", StringComparison.Ordinal))
{
    var workerIndex = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
    var workerStore = new KnownHostsStore(args[1]);
    workerStore.Trust(
        HostEndpointIdentity.Create($"lock-worker-{workerIndex}.example", 22),
        HostKeyData.Create(
            "ssh-ed25519",
            Enumerable.Repeat(checked((byte)(workerIndex + 1)), 32).ToArray()));
    return;
}

AssertSshNet2026PublicApi();
AssertNegotiatedConnectionInfoContract();
AssertSshAgentAdapterLoads();
AssertLastUsedRaceIsBestEffort();
await AssertKeyboardInteractiveCancellationAsync();
await AssertJumpRouteDiagnosticBoundaryAsync();

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
Assert(failedSession.NegotiatedInfo is null,
    "new SSH session has no stale negotiated-information snapshot");
using (var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
    await failedSession.ConnectAsync(connectionTimeout.Token);
Assert(failedSession.State == SessionState.Failed,
    "refused SSH connection enters failed state");
Assert(failedSession.NegotiatedInfo is null,
    "failed SSH connection clears negotiated-information snapshot");
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
await AssertUnexpectedPrimaryTransportFailureAsync();
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
        DisableDirect = true,
        AllowedRouteTypes = [ConnectionRouteType.Socks5],
    });
Assert(proxyRoute.Type == ConnectionRouteType.Socks5, "strict proxy route resolution");
AssertThrows<RoutePolicyViolationException>(
    () => RouteResolver.Resolve(
        new ConnectionRoute(),
        new ConnectionRoutePolicy { DisableDirect = true }),
    "strict route policy blocks direct fallback");
try
{
    _ = RouteResolver.Resolve(
        new ConnectionRoute(),
        new ConnectionRoutePolicy { DisableDirect = true });
    throw new InvalidOperationException("Expected strict-route rejection.");
}
catch (RoutePolicyViolationException error)
{
    Assert(error.Code == ConnectionRouteErrorCodes.StrictRouteDirectBlocked,
        "strict route rejection exposes a stable error code");
}

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

var correlationContext = ConnectionCorrelationContext.Create(
    new SshConnectionInfo
    {
        Host = "server.internal",
        Port = 22,
        Username = "operator",
    },
    proxyRoute);
Assert(correlationContext.RouteId == "corp-proxy", "correlation context carries route id");
Assert(correlationContext.CorrelationId.Length == 32, "correlation context id");
Assert(!correlationContext.ToString().Contains(proxyPassword, StringComparison.Ordinal),
    "correlation context excludes proxy credentials");

var scratch = Path.Combine(
    Path.GetTempPath(),
    $"sutty-host-key-self-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratch);

try
{
    AssertSshConnectionPreflight(scratch);
    AssertConnectionDiagnosticsAndSupportBundle(scratch);

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
    var initiallyTrusted = store.Find(endpoint)!;
    Assert(initiallyTrusted.TrustedAtUtc == initiallyTrusted.LastUsedAtUtc,
        "new known host initializes first-trusted and last-used time together");
    Assert(store.GetActivity().Single() is { Type: KnownHostActivityType.Trusted },
        "initial explicit trust creates local security activity");
    var markedUsedAt = initiallyTrusted.LastUsedAtUtc.AddMinutes(2);
    var markedUsed = store.MarkUsed(endpoint, firstKey, markedUsedAt);
    Assert(markedUsed.LastUsedAtUtc == markedUsedAt &&
           new KnownHostsStore(storePath).Find(endpoint)?.LastUsedAtUtc == markedUsedAt,
        "exact persisted key updates durable last-used time");
    AssertThrows<HostKeyChangedException>(
        () => store.MarkUsed(endpoint, changedKey),
        "different key cannot update last-used metadata");

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

    var rotationContext = new HostKeyTrustContext(store);
    var rotationCandidate = rotationContext.Evaluate(endpoint, changedKey);
    Assert(rotationCandidate.State == HostKeyTrustState.Changed,
        "persisted changed key becomes a rotation candidate");
    Assert(!rotationContext.ApplyRotation(
            rotationCandidate,
            HostKeyRotationDecision.Cancelled) &&
           store.Find(endpoint)!.Key.Equals(firstKey),
        "cancelled rotation leaves the persisted key untouched");
    AssertThrows<ArgumentException>(
        () => rotationContext.ApplyRotation(
            rotationCandidate,
            new HostKeyRotationDecision(true, " ")),
        "rotation requires a user-entered reason");
    Assert(rotationContext.ApplyRotation(
            rotationCandidate,
            new HostKeyRotationDecision(true, "planned server key rotation")),
        "confirmed changed key can be deliberately rotated");
    Assert(rotationContext.Evaluate(endpoint, changedKey) is
           { State: HostKeyTrustState.Trusted, Source: HostKeyTrustSource.Persistent } &&
           store.Find(endpoint) is { } rotatedRecord &&
           rotatedRecord.Key.Equals(changedKey) &&
           rotatedRecord.TrustedAtUtc == initiallyTrusted.TrustedAtUtc,
        "rotation preserves first-trusted time and trusts only the replacement key");
    Assert(store.GetActivity().First() is
           { Type: KnownHostActivityType.Rotated, Reason: "planned server key rotation" },
        "rotation records bounded local activity and its explicit reason");

    var reasonEndpoint = HostEndpointIdentity.Create("rotation-reason.example", 22);
    store.Trust(reasonEndpoint, firstKey);
    foreach (var deceptiveReason in new[]
             {
                 "visible\u200Bhidden",
                 "visible\u202Ehidden",
                 "visible\u2066hidden",
                 "\u00A0\u2007\u202F",
                 "\u034F\uFE0F",
             })
    {
        Assert(!HostKeyRotationReason.TryNormalize(deceptiveReason, out _),
            "shared rotation-reason policy rejects deceptive text before confirmation");
        AssertThrows<ArgumentException>(
            () => store.Rotate(reasonEndpoint, firstKey, changedKey, deceptiveReason),
            "rotation reason rejects zero-width, bidi, and visually blank text");
    }
    Assert(HostKeyRotationReason.TryNormalize(
               "  planned\u00A0\u2002rotation  ",
               out var normalizedRotationReason) &&
           normalizedRotationReason == "planned rotation",
        "shared rotation-reason policy returns the canonical visible value");
    store.Rotate(
        reasonEndpoint,
        firstKey,
        changedKey,
        normalizedRotationReason);
    Assert(store.GetActivity().First() is
           { Type: KnownHostActivityType.Rotated, Reason: "planned rotation" },
        "rotation reason compatibility-normalizes and collapses visible whitespace");

    var racedEndpoint = HostEndpointIdentity.Create("rotation-race.example", 22);
    store.Trust(racedEndpoint, firstKey);
    var racedContext = new HostKeyTrustContext(store);
    var displayedRotation = racedContext.Evaluate(racedEndpoint, changedKey);
    var interveningKey = HostKeyData.Create("ssh-ed25519", [51, 52, 53, 54]);
    store.Rotate(racedEndpoint, firstKey, interveningKey, "concurrent administrator update");
    AssertThrows<HostKeyChangedException>(
        () => racedContext.ApplyRotation(
            displayedRotation,
            new HostKeyRotationDecision(true, "stale prompt must fail")),
        "rotation compares against the exact old key shown in the prompt");
    Assert(store.Find(racedEndpoint)!.Key.Equals(interveningKey),
        "stale rotation prompt cannot overwrite an unseen concurrent key");

    var multiInstancePath = Path.Combine(scratch, "known-hosts-multi-instance.json");
    var firstInstance = new KnownHostsStore(multiInstancePath);
    var staleInstance = new KnownHostsStore(multiInstancePath);
    var multiEndpoint = HostEndpointIdentity.Create("multi-instance.example", 22);
    firstInstance.Trust(multiEndpoint, firstKey);
    Assert(staleInstance.Find(multiEndpoint)!.Key.Equals(firstKey),
        "second store instance initially observes the trusted key");
    firstInstance.Rotate(multiEndpoint, firstKey, interveningKey, "concurrent process rotation");
    AssertThrows<HostKeyChangedException>(
        () => staleInstance.Rotate(
            multiEndpoint,
            firstKey,
            changedKey,
            "stale process must fail"),
        "stale store instance cannot overwrite a concurrent rotation");
    Assert(staleInstance.Find(multiEndpoint)!.Key.Equals(interveningKey),
        "trust reads refresh after another store instance rotates a key");
    Assert(firstInstance.Remove(multiEndpoint, interveningKey) &&
           staleInstance.Find(multiEndpoint) is null,
        "trust reads observe removal performed by another store instance");

    var atomicSnapshotPath = Path.Combine(scratch, "known-hosts-atomic-snapshot.json");
    var snapshotWriter = new KnownHostsStore(atomicSnapshotPath);
    var snapshotReader = new KnownHostsStore(atomicSnapshotPath);
    var snapshotEndpoint = HostEndpointIdentity.Create("snapshot.example", 22);
    snapshotWriter.Trust(snapshotEndpoint, firstKey);
    var snapshotViolations = 0;
    var snapshotReads = 0;
    var snapshotWriterTask = Task.Run(() =>
    {
        for (var iteration = 0; iteration < 24; iteration++)
        {
            snapshotWriter.Remove(snapshotEndpoint, firstKey);
            snapshotWriter.Trust(snapshotEndpoint, firstKey);
        }
    });
    do
    {
        var atomicSnapshot = snapshotReader.GetSnapshot(100);
        var latestEndpointActivity = atomicSnapshot.Activity
            .First(item => item.Endpoint == snapshotEndpoint);
        var endpointPresent = atomicSnapshot.Hosts
            .Any(item => item.Endpoint == snapshotEndpoint);
        var snapshotIsConsistent = endpointPresent
            ? latestEndpointActivity.Type == KnownHostActivityType.Trusted
            : latestEndpointActivity.Type == KnownHostActivityType.Removed;
        if (!snapshotIsConsistent)
            Interlocked.Increment(ref snapshotViolations);
        snapshotReads++;
    }
    while (!snapshotWriterTask.IsCompleted);
    await snapshotWriterTask;
    Assert(snapshotReads > 0 && snapshotViolations == 0,
        "atomic Known Hosts snapshot never mixes host and activity revisions");

    AssertKnownHostsCrossProcessLock(scratch);

    var removableEndpoint = HostEndpointIdentity.Create("remove.example", 2200);
    store.Trust(removableEndpoint, firstKey);
    store.Rotate(removableEndpoint, firstKey, changedKey, "test rotation before stale delete");
    AssertThrows<HostKeyChangedException>(
        () => store.Remove(removableEndpoint, firstKey),
        "stale known-host UI cannot delete a newly rotated key");
    Assert(store.Remove(removableEndpoint, changedKey) &&
           store.Find(removableEndpoint) is null &&
           store.GetActivity().First().Type == KnownHostActivityType.Removed,
        "explicit deletion uses compare-and-swap and records activity");

    var validV2Text = File.ReadAllText(storePath);
    var undefinedActivityPath = Path.Combine(scratch, "known-hosts-undefined-activity.json");
    var undefinedActivity = JsonNode.Parse(validV2Text)!.AsObject();
    undefinedActivity["activity"]!.AsArray()[0]!["type"] = "999";
    File.WriteAllText(undefinedActivityPath, undefinedActivity.ToJsonString());
    AssertThrows<InvalidDataException>(
        () => new KnownHostsStore(undefinedActivityPath).GetAll(),
        "v2 store rejects undefined numeric activity types");

    var missingLastUsedPath = Path.Combine(scratch, "known-hosts-missing-last-used.json");
    var missingLastUsed = JsonNode.Parse(validV2Text)!.AsObject();
    missingLastUsed["hosts"]!.AsArray()[0]!.AsObject().Remove("lastUsedAtUtc");
    File.WriteAllText(missingLastUsedPath, missingLastUsed.ToJsonString());
    AssertThrows<InvalidDataException>(
        () => new KnownHostsStore(missingLastUsedPath).GetAll(),
        "v2 store requires last-used timestamps");

    var missingActivityPath = Path.Combine(scratch, "known-hosts-missing-activity.json");
    var missingActivity = JsonNode.Parse(validV2Text)!.AsObject();
    missingActivity.Remove("activity");
    File.WriteAllText(missingActivityPath, missingActivity.ToJsonString());
    AssertThrows<InvalidDataException>(
        () => new KnownHostsStore(missingActivityPath).GetAll(),
        "v2 store requires its activity collection");

    var nullEntryPath = Path.Combine(scratch, "known-hosts-null-entry.json");
    var nullEntry = JsonNode.Parse(validV2Text)!.AsObject();
    nullEntry["hosts"]!.AsArray().Add(null);
    File.WriteAllText(nullEntryPath, nullEntry.ToJsonString());
    AssertThrows<InvalidDataException>(
        () => new KnownHostsStore(nullEntryPath).GetAll(),
        "null known-host entries are normalized to invalid-data failures");

    var hiddenReasonPath = Path.Combine(scratch, "known-hosts-hidden-reason.json");
    var hiddenReason = JsonNode.Parse(validV2Text)!.AsObject();
    hiddenReason["activity"]!.AsArray()[0]!["reason"] = "visible\u200Bhidden";
    File.WriteAllText(hiddenReasonPath, hiddenReason.ToJsonString());
    AssertThrows<InvalidDataException>(
        () => new KnownHostsStore(hiddenReasonPath).GetSnapshot(),
        "persisted Known Hosts activity rejects hidden Unicode formatting");

    var legacyPath = Path.Combine(scratch, "known-hosts-v1.json");
    var legacyTrustedAt = DateTimeOffset.UtcNow.AddDays(-1);
    var legacyDocument = new JsonObject
    {
        ["version"] = 1,
        ["hosts"] = new JsonArray
        {
            new JsonObject
            {
                ["identity"] = endpoint.Value,
                ["algorithm"] = firstKey.Algorithm,
                ["sha256Fingerprint"] = firstKey.Sha256Fingerprint,
                ["rawKey"] = Convert.ToBase64String(firstKey.RawKey.Span),
                ["trustedAtUtc"] = legacyTrustedAt,
            },
        },
    };
    File.WriteAllText(legacyPath, legacyDocument.ToJsonString());
    var legacyStore = new KnownHostsStore(legacyPath);
    var migratedLegacy = legacyStore.Find(endpoint)!;
    Assert(migratedLegacy.LastUsedAtUtc == migratedLegacy.TrustedAtUtc,
        "v1 known-host file loads with a safe last-used migration default");
    legacyStore.MarkUsed(endpoint, firstKey, migratedLegacy.LastUsedAtUtc.AddMinutes(2));
    Assert(JsonNode.Parse(File.ReadAllText(legacyPath))!["version"]!.GetValue<int>() == 2,
        "first v1 metadata mutation writes the v2 known-host schema");

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

static TException CaptureException<TException>(Action action, string name)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException error)
    {
        return error;
    }

    throw new InvalidOperationException($"Self-test failed: {name} did not throw {typeof(TException).Name}.");
}

static void AssertKnownHostsCrossProcessLock(string scratch)
{
    const int workerCount = 6;
    var storePath = Path.Combine(scratch, "known-hosts-cross-process.json");
    var processPath = Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot locate the security self-test process.");
    var workers = new List<Process>(workerCount);

    try
    {
        for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            var startInfo = new ProcessStartInfo(processPath)
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            if (string.Equals(
                    Path.GetFileNameWithoutExtension(processPath),
                    "dotnet",
                    StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
            }
            startInfo.ArgumentList.Add("--known-hosts-lock-worker");
            startInfo.ArgumentList.Add(storePath);
            startInfo.ArgumentList.Add(
                workerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            workers.Add(Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start a Known Hosts lock worker."));
        }

        foreach (var worker in workers)
        {
            if (!worker.WaitForExit(milliseconds: 30_000))
            {
                worker.Kill(entireProcessTree: true);
                throw new TimeoutException("Known Hosts lock worker timed out.");
            }

            var output = worker.StandardOutput.ReadToEnd();
            var error = worker.StandardError.ReadToEnd();
            Assert(worker.ExitCode == 0,
                $"cross-process Known Hosts worker exits cleanly ({output} {error})");
        }
    }
    finally
    {
        foreach (var worker in workers)
            worker.Dispose();
    }

    var snapshot = new KnownHostsStore(storePath).GetSnapshot(workerCount);
    Assert(snapshot.Hosts.Count == workerCount &&
           snapshot.Activity.Count == workerCount &&
           Enumerable.Range(0, workerCount).All(index =>
               snapshot.Hosts.Any(record =>
                   record.Endpoint == HostEndpointIdentity.Create(
                       $"lock-worker-{index}.example",
                       22))),
        "cross-process file lock preserves every concurrent Known Hosts mutation");
    Assert(File.Exists(storePath + ".lock"),
        "Known Hosts uses a durable same-directory cross-session lock file");
}

static void AssertSshConnectionPreflight(string scratch)
{
    var targetPassword = CreateTestSecret("preflight-target-password");
    var keyPassphrase = CreateTestSecret("preflight-key-passphrase");
    var routePassword = CreateTestSecret("preflight-route-password");
    var keyPathMarker = CreateTestSecret("preflight-key-path");
    var existingKeyPath = Path.Combine(scratch, $"{keyPathMarker}.key");
    File.WriteAllText(existingKeyPath, "existence-only preflight fixture");

    SshConnectionInfo CreateValidInfo() => new()
    {
        Host = "server.internal",
        Port = 22,
        Username = "operator",
        AuthMethod = SshAuthMethod.Password,
        Password = targetPassword,
        Passphrase = keyPassphrase,
        Route = new ConnectionRoute
        {
            Id = "preflight-proxy",
            Type = ConnectionRouteType.Socks5,
            Host = "proxy.internal",
            Port = 1080,
            Username = "proxy-user",
            Password = routePassword,
        },
        RoutePolicy = new ConnectionRoutePolicy
        {
            DisableDirect = true,
            AllowedRouteTypes = [ConnectionRouteType.Socks5],
        },
    };

    var logCountBefore = ConnectionLogStore.Snapshot().Count;
    SshConnectionPreflightValidator.Validate(CreateValidInfo());
    var publicKeyInfo = CreateValidInfo();
    publicKeyInfo.AuthMethod = SshAuthMethod.PublicKey;
    publicKeyInfo.PrivateKeyPath = existingKeyPath;
    SshConnectionPreflightValidator.Validate(publicKeyInfo);
    Assert(typeof(SshConnectionPreflightValidator)
               .GetMethod(nameof(SshConnectionPreflightValidator.Validate))!
               .ReturnType == typeof(void),
        "preflight success returns no route or credential-bearing value");

    var invalidHost = CreateValidInfo();
    invalidHost.Host = "invalid/host";
    var blankUsername = CreateValidInfo();
    blankUsername.Username = " ";
    var controlUsername = CreateValidInfo();
    controlUsername.Username = "operator\nadmin";
    var oversizedUsername = CreateValidInfo();
    oversizedUsername.Username = new string('u', SshConnectionPreflightValidator.MaximumUsernameLength + 1);
    var undefinedAuthentication = CreateValidInfo();
    undefinedAuthentication.AuthMethod = (SshAuthMethod)int.MaxValue;
    var undefinedRoute = CreateValidInfo();
    undefinedRoute.Route.Type = (ConnectionRouteType)int.MaxValue;
    var inputErrors = new[]
    {
        CaptureException<ArgumentException>(
            () => SshConnectionPreflightValidator.Validate(invalidHost),
            "preflight rejects an invalid endpoint"),
        CaptureException<ArgumentException>(
            () => SshConnectionPreflightValidator.Validate(blankUsername),
            "preflight requires a username"),
        CaptureException<ArgumentException>(
            () => SshConnectionPreflightValidator.Validate(controlUsername),
            "preflight rejects control characters in a username"),
        CaptureException<ArgumentException>(
            () => SshConnectionPreflightValidator.Validate(oversizedUsername),
            "preflight bounds the username"),
        CaptureException<ArgumentException>(
            () => SshConnectionPreflightValidator.Validate(undefinedAuthentication),
            "preflight rejects an undefined authentication method"),
        CaptureException<ArgumentException>(
            () => SshConnectionPreflightValidator.Validate(undefinedRoute),
            "preflight rejects an undefined route type"),
    };
    Assert(inputErrors.All(error => ConnectionExceptionClassifier.Classify(error) is
           {
               Stage: ConnectionDiagnosticStage.InputValidation,
               ErrorCode: ConnectionDiagnosticErrorCodes.InputInvalid,
           }),
        "preflight argument failures classify as stable input errors");

    var unselectedKey = CreateValidInfo();
    unselectedKey.AuthMethod = SshAuthMethod.PublicKey;
    unselectedKey.PrivateKeyPath = "";
    var unselectedKeyError = CaptureException<ArgumentException>(
        () => SshConnectionPreflightValidator.Validate(unselectedKey),
        "preflight requires a selected public key");
    Assert(ConnectionExceptionClassifier.Classify(unselectedKeyError).ErrorCode ==
           ConnectionDiagnosticErrorCodes.InputInvalid,
        "an unselected public key remains an input error");

    var missingKeyPath = Path.Combine(scratch, $"missing-{keyPathMarker}.key");
    var missingKey = CreateValidInfo();
    missingKey.AuthMethod = SshAuthMethod.PublicKey;
    missingKey.PrivateKeyPath = missingKeyPath;
    var missingKeyError = CaptureException<FileNotFoundException>(
        () => SshConnectionPreflightValidator.Validate(missingKey),
        "preflight preserves a missing-key exception");
    Assert(ConnectionExceptionClassifier.Classify(missingKeyError) is
           {
               Stage: ConnectionDiagnosticStage.Authentication,
               ErrorCode: ConnectionDiagnosticErrorCodes.AuthenticationKeyFileMissing,
           } &&
           missingKeyError.FileName is null &&
           !missingKeyError.ToString().Contains(keyPathMarker, StringComparison.Ordinal),
        "missing public keys classify as authentication failures without exposing paths");

    var blockedRoute = CreateValidInfo();
    blockedRoute.Route = new ConnectionRoute();
    blockedRoute.RoutePolicy = new ConnectionRoutePolicy { DisableDirect = true };
    var routeError = CaptureException<RoutePolicyViolationException>(
        () => SshConnectionPreflightValidator.Validate(blockedRoute),
        "preflight preserves strict-route policy failures");
    Assert(ConnectionExceptionClassifier.Classify(routeError) is
           {
               Stage: ConnectionDiagnosticStage.ProxyOrJumpRoute,
               ErrorCode: ConnectionDiagnosticErrorCodes.RoutePolicyBlocked,
           },
        "preflight route-policy failures retain the stable route classification");

    var diagnosticText = string.Join('\n', inputErrors.Select(error => error.ToString())) +
                         unselectedKeyError + missingKeyError + routeError;
    Assert(!diagnosticText.Contains(targetPassword, StringComparison.Ordinal) &&
           !diagnosticText.Contains(keyPassphrase, StringComparison.Ordinal) &&
           !diagnosticText.Contains(routePassword, StringComparison.Ordinal) &&
           ConnectionLogStore.Snapshot().Count == logCountBefore,
        "preflight neither exposes credential values nor writes connection logs");
}

static void AssertConnectionDiagnosticsAndSupportBundle(string scratch)
{
    var orderedStages = Enum.GetValues<ConnectionDiagnosticStage>();
    Assert(orderedStages.Length == 9 &&
           orderedStages.Select(stage => (int)stage).SequenceEqual(Enumerable.Range(1, 9)),
        "Connection Doctor exposes the exact ordered nine-stage contract");

    var dnsFailure = ConnectionExceptionClassifier.Classify(
        new SocketException((int)SocketError.HostNotFound));
    Assert(dnsFailure is
        {
            Stage: ConnectionDiagnosticStage.DnsAndTcp,
            Status: ConnectionDiagnosticStatus.Failed,
            ErrorCode: ConnectionDiagnosticErrorCodes.DnsLookupFailed,
        },
        "DNS failures have a stable stage and error code");

    var sshNetTimeoutSecret = CreateTestSecret("sshnet-timeout-message");
    var sshNetHandshakeTimeout = ConnectionExceptionClassifier.Classify(
        new SshOperationTimeoutException(sshNetTimeoutSecret));
    var sshNetTcpTimeout = ConnectionExceptionClassifier.Classify(
        new SshOperationTimeoutException(sshNetTimeoutSecret),
        ConnectionDiagnosticStage.DnsAndTcp);
    var sshNetRouteTimeout = ConnectionExceptionClassifier.Classify(
        new SshOperationTimeoutException(sshNetTimeoutSecret),
        ConnectionDiagnosticStage.ProxyOrJumpRoute,
        ConnectionRouteType.Socks5);
    var sshNetAuthenticationTimeout = ConnectionExceptionClassifier.Classify(
        new SshOperationTimeoutException(sshNetTimeoutSecret),
        ConnectionDiagnosticStage.Authentication);
    Assert(sshNetHandshakeTimeout is
           {
               Stage: ConnectionDiagnosticStage.SshHandshake,
               ErrorCode: ConnectionDiagnosticErrorCodes.SshHandshakeTimedOut,
           } &&
           sshNetTcpTimeout is
           {
               Stage: ConnectionDiagnosticStage.DnsAndTcp,
               ErrorCode: ConnectionDiagnosticErrorCodes.TcpTimedOut,
           } &&
           sshNetRouteTimeout is
           {
               Stage: ConnectionDiagnosticStage.ProxyOrJumpRoute,
               ErrorCode: ConnectionDiagnosticErrorCodes.RouteTimedOut,
           } &&
           sshNetAuthenticationTimeout is
           {
               Stage: ConnectionDiagnosticStage.Authentication,
               ErrorCode: ConnectionDiagnosticErrorCodes.AuthenticationTimedOut,
           },
        "SSH.NET operation timeouts honor handshake, TCP, route, and authentication stages");
    Assert(!sshNetHandshakeTimeout.TechnicalDetail.Contains(
            sshNetTimeoutSecret,
            StringComparison.Ordinal),
        "SSH.NET timeout diagnostics exclude the exception message");

    var routeSecret = CreateTestSecret("route-exception-message");
    var socksFailure = ConnectionExceptionClassifier.Classify(
        new InvalidOperationException(
            $"proxy.internal operator {routeSecret}",
            new SocketException((int)SocketError.ConnectionRefused)),
        ConnectionDiagnosticStage.ProxyOrJumpRoute,
        ConnectionRouteType.Socks5);
    Assert(socksFailure is
        {
            Stage: ConnectionDiagnosticStage.ProxyOrJumpRoute,
            ErrorCode: ConnectionDiagnosticErrorCodes.RouteSocks5Refused,
        },
        "SOCKS5 refusal has the documented stable route code");
    Assert(!socksFailure.TechnicalDetail.Contains(routeSecret, StringComparison.Ordinal) &&
           !socksFailure.TechnicalDetail.Contains("proxy.internal", StringComparison.Ordinal),
        "classified technical detail excludes exception messages and endpoints");

    var authFailure = ConnectionExceptionClassifier.Classify(
        new SshAuthenticationException("synthetic authentication failure"));
    Assert(authFailure.Stage == ConnectionDiagnosticStage.Authentication &&
           authFailure.ErrorCode == ConnectionDiagnosticErrorCodes.AuthenticationFailed,
        "SSH authentication failures have a stable classification");
    var hostKeyFailure = ConnectionExceptionClassifier.Classify(
        new SecurityException("synthetic untrusted host key"));
    Assert(hostKeyFailure.Stage == ConnectionDiagnosticStage.HostKey &&
           hostKeyFailure.ErrorCode == ConnectionDiagnosticErrorCodes.HostKeyRejected,
        "host-key rejection has a stable classification");
    var classifierEndpoint = HostEndpointIdentity.Create("classifier.example", 22);
    var classifierOldKey = HostKeyData.Create("ssh-ed25519", [1, 2, 3, 4]);
    var classifierNewKey = HostKeyData.Create("ssh-ed25519", [5, 6, 7, 8]);
    var sftpChangedHostKey = ConnectionExceptionClassifier.Classify(
        new HostKeyChangedException(classifierEndpoint, classifierOldKey, classifierNewKey),
        ConnectionDiagnosticStage.SftpSubsystem);
    var sftpRejectedHostKey = ConnectionExceptionClassifier.Classify(
        new SecurityException("synthetic SFTP host-key rejection"),
        ConnectionDiagnosticStage.SftpSubsystem);
    Assert(sftpChangedHostKey is
           {
               Stage: ConnectionDiagnosticStage.HostKey,
               ErrorCode: ConnectionDiagnosticErrorCodes.HostKeyChanged,
           } &&
           sftpRejectedHostKey is
           {
               Stage: ConnectionDiagnosticStage.HostKey,
               ErrorCode: ConnectionDiagnosticErrorCodes.HostKeyRejected,
           },
        "SFTP reconnect host-key changes and rejections retain security precedence");
    var sftpDiagnosticSession = new SshNetSession(new SshConnectionInfo
    {
        Host = "sftp-diagnostic.example",
        Port = 22,
        Username = "operator",
        AuthMethod = SshAuthMethod.Password,
        Password = "synthetic-only",
    });
    ConnectionDiagnosticEventStore.Shared.Append(
        sftpDiagnosticSession.CorrelationContext.CorrelationId,
        ConnectionDiagnosticResult.Running(ConnectionDiagnosticStage.SftpSubsystem));
    var recordSftpDiagnostic = typeof(SshNetSession).GetMethod(
        "RecordSftpDiagnostic",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    Assert(recordSftpDiagnostic is not null,
        "SFTP dependent-failure diagnostic test seam remains available");
    recordSftpDiagnostic!.Invoke(sftpDiagnosticSession, [sftpChangedHostKey]);
    var sftpHostKeyEvents = ConnectionDiagnosticEventStore.Shared
        .Snapshot(sftpDiagnosticSession.CorrelationContext.CorrelationId);
    Assert(sftpHostKeyEvents.Count == 3 &&
           sftpHostKeyEvents[0] is
           {
               Stage: ConnectionDiagnosticStage.SftpSubsystem,
               Status: ConnectionDiagnosticStatus.Running,
           } &&
           sftpHostKeyEvents[1] is
           {
               Stage: ConnectionDiagnosticStage.SftpSubsystem,
               Status: ConnectionDiagnosticStatus.Failed,
               ErrorCode: ConnectionDiagnosticErrorCodes.HostKeyChanged,
           } &&
           sftpHostKeyEvents[2] is
           {
               Stage: ConnectionDiagnosticStage.HostKey,
               Status: ConnectionDiagnosticStatus.Failed,
               ErrorCode: ConnectionDiagnosticErrorCodes.HostKeyChanged,
           },
        "SFTP host-key failure closes the subsystem stage before preserving the causal security result");
    var sftpFailure = ConnectionExceptionClassifier.Classify(
        new SshException("synthetic subsystem rejection"),
        ConnectionDiagnosticStage.SftpSubsystem);
    Assert(sftpFailure.Stage == ConnectionDiagnosticStage.SftpSubsystem &&
           sftpFailure.ErrorCode == ConnectionDiagnosticErrorCodes.SftpSubsystemUnavailable,
        "SFTP subsystem failures honor the explicit stage");
    var cancelled = ConnectionExceptionClassifier.Classify(
        new OperationCanceledException(),
        ConnectionDiagnosticStage.Pty);
    Assert(cancelled.Status == ConnectionDiagnosticStatus.Cancelled &&
           cancelled.ErrorCode == ConnectionDiagnosticErrorCodes.ConnectionCancelled,
        "cancellation remains distinct from failure");

    var boundedStore = new ConnectionDiagnosticEventStore(capacity: 3);
    var boundedCorrelationId = Guid.NewGuid().ToString("N");
    for (var index = 0; index < 4; index++)
    {
        boundedStore.Append(
            boundedCorrelationId,
            ConnectionDiagnosticStage.InputValidation,
            ConnectionDiagnosticStatus.Succeeded,
            elapsed: TimeSpan.FromMilliseconds(index));
    }
    var boundedSnapshot = boundedStore.Snapshot();
    Assert(boundedStore.Count == 3 &&
           boundedSnapshot.Select(entry => entry.Sequence).SequenceEqual([2L, 3L, 4L]),
        "connection diagnostic event storage is bounded and ordered");

    const int concurrentCapacity = 97;
    const int concurrentWrites = 4_096;
    for (var round = 0; round < 4; round++)
    {
        var concurrentStore = new ConnectionDiagnosticEventStore(concurrentCapacity);
        var concurrentCorrelationId = Guid.NewGuid().ToString("N");
        Parallel.For(0, concurrentWrites, index =>
        {
            concurrentStore.Append(
                concurrentCorrelationId,
                ConnectionDiagnosticStage.DnsAndTcp,
                ConnectionDiagnosticStatus.Succeeded,
                elapsed: TimeSpan.FromMilliseconds(index));
        });

        var concurrentSnapshot = concurrentStore.Snapshot();
        var expectedTail = Enumerable
            .Range(concurrentWrites - concurrentCapacity + 1, concurrentCapacity)
            .Select(sequence => (long)sequence);
        Assert(concurrentStore.Count == concurrentCapacity &&
               concurrentSnapshot.Select(entry => entry.Sequence).SequenceEqual(expectedTail),
            $"concurrent diagnostic append preserves ordered bounded tail (round {round + 1})");
    }

    var fairStore = new ConnectionDiagnosticEventStore();
    var quietFailureCorrelationId = Guid.NewGuid().ToString("N");
    var noisyCorrelationId = Guid.NewGuid().ToString("N");
    fairStore.Append(
        quietFailureCorrelationId,
        ConnectionDiagnosticStage.ProxyOrJumpRoute,
        ConnectionDiagnosticStatus.Failed,
        ConnectionDiagnosticErrorCodes.RouteSocks5Refused);
    for (var index = 0;
         index < ConnectionDiagnosticEventStore.DefaultCapacity + 64;
         index++)
    {
        fairStore.Append(
            noisyCorrelationId,
            ConnectionDiagnosticStage.DnsAndTcp,
            ConnectionDiagnosticStatus.Succeeded);
    }

    Assert(fairStore.Count == ConnectionDiagnosticEventStore.DefaultCapacity &&
           fairStore.Snapshot(quietFailureCorrelationId).Single() is
           {
               Stage: ConnectionDiagnosticStage.ProxyOrJumpRoute,
               Status: ConnectionDiagnosticStatus.Failed,
               ErrorCode: ConnectionDiagnosticErrorCodes.RouteSocks5Refused,
           },
        "one noisy connection cannot evict another connection's only stage failure");
    var retainedFailurePath = Path.Combine(scratch, "support-retained-quiet-failure.zip");
    new SupportBundleService(fairStore).Create(
        retainedFailurePath,
        new SupportBundleContext(
            "0.1.0-alpha.5",
            "abcdef012345",
            "10.0.26100.0",
            Architecture.X64,
            ConnectionRouteType.Socks5,
            SshAuthMethod.Password,
            ConnectionDiagnosticErrorCodes.RouteSocks5Refused,
            quietFailureCorrelationId,
            SettingsSchemaVersion: 1));
    var retainedFailureReport = ReadSupportBundleReport(retainedFailurePath);
    Assert(retainedFailureReport["stableErrorCode"]!.GetValue<string>() ==
               ConnectionDiagnosticErrorCodes.RouteSocks5Refused &&
           retainedFailureReport["events"]!.AsArray().Single()!["errorCode"]!.GetValue<string>() ==
               ConnectionDiagnosticErrorCodes.RouteSocks5Refused,
        "support bundle retains an older selected tab's error and structured event");

    var correlationGuid = Guid.NewGuid();
    var correlationInput = correlationGuid.ToString("D").ToUpperInvariant();
    var correlationId = correlationGuid.ToString("N");
    var unrelatedCorrelationId = Guid.NewGuid().ToString("N");
    var events = new ConnectionDiagnosticEventStore(capacity: 256);
    events.Append(
        correlationInput,
        ConnectionDiagnosticResult.Succeeded(ConnectionDiagnosticStage.InputValidation),
        TimeSpan.FromMilliseconds(3));
    events.Append(correlationInput, socksFailure, TimeSpan.FromMilliseconds(17));
    events.Append(
        unrelatedCorrelationId,
        ConnectionDiagnosticResult.Succeeded(ConnectionDiagnosticStage.DnsAndTcp));
    for (var index = 0; index < SupportBundleService.MaximumRecentEvents + 5; index++)
    {
        events.Append(
            correlationInput,
            ConnectionDiagnosticResult.Succeeded(ConnectionDiagnosticStage.DnsAndTcp),
            TimeSpan.FromMilliseconds(index));
    }

    var endpointMarker = CreateTestSecret("endpoint");
    var hostnameMarker = CreateTestSecret("hostname");
    var usernameMarker = CreateTestSecret("username");
    var pathMarker = CreateTestSecret("path");
    var transcriptMarker = CreateTestSecret("transcript");
    var commandOutputMarker = CreateTestSecret("command-output");
    ConnectionLogStore.Append(
        Guid.NewGuid(),
        usernameMarker,
        $"{endpointMarker}:{hostnameMarker}",
        ConnectionLogSeverity.Error,
        "support-bundle-exclusion-fixture",
        transcriptMarker,
        commandOutputMarker,
        pathMarker);

    var context = new SupportBundleContext(
        " 0.1.0-alpha.5 ",
        " ABCDEF012345 ",
        " 10.0.26200.0 ",
        Architecture.X64,
        ConnectionRouteType.Socks5,
        SshAuthMethod.Password,
        ConnectionDiagnosticErrorCodes.RouteSocks5Refused.ToLowerInvariant(),
        correlationInput,
        SettingsSchemaVersion: 3);
    var service = new SupportBundleService(events);
    var firstPath = Path.Combine(scratch, "support-bundle-first.zip");
    var secondPath = Path.Combine(scratch, "support-bundle-second.zip");
    var firstResult = service.Create(firstPath, context);
    var secondResult = service.Create(secondPath, context);

    Assert(File.ReadAllBytes(firstPath).SequenceEqual(File.ReadAllBytes(secondPath)),
        "identical support-bundle inputs produce deterministic ZIP bytes");
    Assert(firstResult.Entries.SequenceEqual([
            SupportBundleService.ManifestEntryName,
            SupportBundleService.ReportEntryName,
        ], StringComparer.Ordinal) &&
        firstResult.SizeBytes <= SupportBundleService.MaximumArchiveBytes &&
        firstResult.Sha256.Length == 64,
        "support-bundle result reports its bounded exact inventory and digest");

    byte[] manifestBytes;
    byte[] reportBytes;
    using (var archive = ZipFile.OpenRead(firstPath))
    {
        Assert(archive.Entries.Select(entry => entry.FullName).SequenceEqual([
                SupportBundleService.ManifestEntryName,
                SupportBundleService.ReportEntryName,
            ], StringComparer.Ordinal),
            "support bundle contains only manifest.json and report.json in stable order");
        Assert(archive.Entries.All(entry =>
                entry.LastWriteTime.DateTime == new DateTime(1980, 1, 1, 0, 0, 0)),
            "support-bundle ZIP metadata uses a deterministic timestamp");
        manifestBytes = ReadZipEntry(archive, SupportBundleService.ManifestEntryName);
        reportBytes = ReadZipEntry(archive, SupportBundleService.ReportEntryName);
    }

    var manifest = JsonNode.Parse(manifestBytes)!.AsObject();
    Assert(manifest["schemaVersion"]!.GetValue<int>() == SupportBundleService.SchemaVersion &&
           manifest["format"]!.GetValue<string>() == "sutty-support-bundle" &&
           manifest["files"]!.AsArray()
               .Select(value => value!.GetValue<string>())
               .SequenceEqual(["manifest.json", "report.json"], StringComparer.Ordinal),
        "support-bundle manifest declares the exact allowlist");
    Assert(manifest["reportSizeBytes"]!.GetValue<long>() == reportBytes.LongLength &&
           manifest["reportSha256"]!.GetValue<string>() ==
               Convert.ToHexString(SHA256.HashData(reportBytes)).ToLowerInvariant(),
        "support-bundle manifest binds the report size and digest");

    var report = JsonNode.Parse(reportBytes)!.AsObject();
    var expectedReportProperties = new[]
    {
        "appBuild",
        "appVersion",
        "authenticationType",
        "correlationId",
        "events",
        "processArchitecture",
        "routeType",
        "schemaVersion",
        "settingsSchemaVersion",
        "stableErrorCode",
        "windowsBuild",
    };
    Assert(report.Select(property => property.Key)
            .Order(StringComparer.Ordinal)
            .SequenceEqual(expectedReportProperties, StringComparer.Ordinal),
        "support-bundle report has only reviewed top-level fields");
    Assert(report["appVersion"]!.GetValue<string>() == "0.1.0-alpha.5" &&
           report["appBuild"]!.GetValue<string>() == "ABCDEF012345" &&
           report["windowsBuild"]!.GetValue<string>() == "10.0.26200.0" &&
           report["processArchitecture"]!.GetValue<string>() == "x64" &&
           report["stableErrorCode"]!.GetValue<string>() ==
               ConnectionDiagnosticErrorCodes.RouteSocks5Refused &&
           report["correlationId"]!.GetValue<string>() == correlationId,
        "support-bundle values are safely normalized");

    var eventArray = report["events"]!.AsArray();
    Assert(eventArray.Count == SupportBundleService.MaximumRecentEvents &&
           eventArray.All(value => value!["correlationId"]!.GetValue<string>() == correlationId),
        "support bundle includes only the bounded recent events for one correlation id");
    Assert(eventArray.Any(value =>
            value!["stage"]!.GetValue<string>() ==
                ConnectionDiagnosticStage.ProxyOrJumpRoute.ToString() &&
            value["status"]!.GetValue<string>() ==
                ConnectionDiagnosticStatus.Failed.ToString() &&
            value["errorCode"]!.GetValue<string>() ==
                ConnectionDiagnosticErrorCodes.RouteSocks5Refused),
        "support bundle retains the latest event for every stage despite a noisy recent tail");
    var expectedEventProperties = new[]
    {
        "correlationId",
        "elapsedMilliseconds",
        "errorCode",
        "sequence",
        "stage",
        "status",
        "timestampUtc",
    };
    Assert(eventArray.All(value => value!.AsObject()
            .Select(property => property.Key)
            .Order(StringComparer.Ordinal)
            .SequenceEqual(expectedEventProperties, StringComparer.Ordinal)),
        "support-bundle events contain only structured allowlisted fields");

    var combinedJson = Encoding.UTF8.GetString(manifestBytes) + Encoding.UTF8.GetString(reportBytes);
    foreach (var excludedMarker in new[]
             {
                 endpointMarker,
                 hostnameMarker,
                 usernameMarker,
                 pathMarker,
                 transcriptMarker,
                 commandOutputMarker,
                 routeSecret,
                 unrelatedCorrelationId,
             })
    {
        Assert(!combinedJson.Contains(excludedMarker, StringComparison.Ordinal),
            $"support bundle excludes sensitive marker {excludedMarker}");
    }
    var forbiddenProperties = new[]
    {
        "endpoint", "host", "hostname", "username", "path", "transcript",
        "command", "commandOutput", "output", "message", "detail",
    };
    Assert(report.Select(property => property.Key)
            .Concat(eventArray.SelectMany(value => value!.AsObject().Select(property => property.Key)))
            .All(property => !forbiddenProperties.Contains(property, StringComparer.OrdinalIgnoreCase)),
        "support-bundle JSON has no sensitive free-text field names");

    var originalBytes = File.ReadAllBytes(firstPath);
    AssertThrows<IOException>(
        () => service.Create(firstPath, context),
        "support bundle refuses a non-atomic implicit overwrite");
    Assert(File.ReadAllBytes(firstPath).SequenceEqual(originalBytes),
        "failed support-bundle creation preserves the complete destination");
    var replacementPath = Path.Combine(scratch, "support-replacement.zip");
    File.WriteAllText(replacementPath, "sentinel destination");
    var replacementResult = service.Create(replacementPath, context, overwrite: true);
    Assert(File.ReadAllBytes(replacementPath).SequenceEqual(originalBytes) &&
           replacementResult.Sha256 == firstResult.Sha256 &&
           !Directory.EnumerateFiles(
               scratch,
               ".support-replacement.zip.*.tmp",
               SearchOption.TopDirectoryOnly).Any(),
        "picker-created destination is atomically replaced without a temporary-file leak");

    var snapshotEvents = new ConnectionDiagnosticEventStore();
    var snapshotCorrelationId = Guid.NewGuid().ToString("N");
    var snapshotContext = context with
    {
        CorrelationId = snapshotCorrelationId,
        StableErrorCode = ConnectionDiagnosticErrorCodes.None,
    };
    snapshotEvents.Append(
        snapshotCorrelationId,
        ConnectionDiagnosticStage.Pty,
        ConnectionDiagnosticStatus.Failed,
        ConnectionDiagnosticErrorCodes.PtyRequestFailed);
    var snapshotService = new SupportBundleService(snapshotEvents);
    var failedPreview = snapshotService.Preview(snapshotCorrelationId);
    Assert(failedPreview.StableErrorCode == ConnectionDiagnosticErrorCodes.PtyRequestFailed &&
           failedPreview.EventCount == 1 &&
           failedPreview.FailureStage == ConnectionDiagnosticStage.Pty &&
           failedPreview.FailureStatus == ConnectionDiagnosticStatus.Failed &&
           failedPreview.FailureSequence is > 0 &&
           failedPreview.FailureTimestampUtc is not null &&
           failedPreview.SnapshotSha256.Length == 64 &&
           failedPreview.SnapshotSha256.All(character =>
               character is >= '0' and <= '9' or >= 'a' and <= 'f'),
        "support-bundle preview binds the selected stable failure and exact event snapshot");
    var failedSnapshotPath = Path.Combine(scratch, "support-snapshot-failed.zip");
    snapshotService.Create(
        failedSnapshotPath,
        snapshotContext with
        {
            StableErrorCode = failedPreview.StableErrorCode,
            FallbackFailureStage = failedPreview.FailureStage,
            ExpectedDiagnosticSnapshotSha256 = failedPreview.SnapshotSha256,
        });
    var failedSnapshotReport = ReadSupportBundleReport(failedSnapshotPath);
    Assert(failedSnapshotReport["stableErrorCode"]!.GetValue<string>() ==
               ConnectionDiagnosticErrorCodes.PtyRequestFailed &&
           failedSnapshotReport["events"]!.AsArray().Any(value =>
               value!["stage"]!.GetValue<string>() == ConnectionDiagnosticStage.Pty.ToString() &&
               value["status"]!.GetValue<string>() == ConnectionDiagnosticStatus.Failed.ToString()),
        "support bundle derives its top error and events from the same failure snapshot");

    snapshotEvents.Append(
        snapshotCorrelationId,
        ConnectionDiagnosticStage.Pty,
        ConnectionDiagnosticStatus.Succeeded,
        ConnectionDiagnosticErrorCodes.None);
    var resolvedSnapshotPath = Path.Combine(scratch, "support-snapshot-resolved.zip");
    snapshotService.Create(resolvedSnapshotPath, snapshotContext);
    var resolvedSnapshotReport = ReadSupportBundleReport(resolvedSnapshotPath);
    Assert(resolvedSnapshotReport["stableErrorCode"]!.GetValue<string>() ==
               ConnectionDiagnosticErrorCodes.None,
        "support bundle keeps NONE when the selected context and event snapshot are resolved");

    var recoveredSnapshotPath = Path.Combine(scratch, "support-snapshot-recovered.zip");
    snapshotService.Create(
        recoveredSnapshotPath,
        snapshotContext with
        {
            StableErrorCode = ConnectionDiagnosticErrorCodes.PtyRequestFailed,
            FallbackFailureStage = ConnectionDiagnosticStage.Pty,
        });
    var recoveredSnapshotReport = ReadSupportBundleReport(recoveredSnapshotPath);
    Assert(recoveredSnapshotReport["stableErrorCode"]!.GetValue<string>() ==
               ConnectionDiagnosticErrorCodes.None &&
           recoveredSnapshotReport["events"]!.AsArray().Any(value =>
               value!["stage"]!.GetValue<string>() == ConnectionDiagnosticStage.Pty.ToString() &&
               value["status"]!.GetValue<string>() == ConnectionDiagnosticStatus.Succeeded.ToString()),
        "support bundle does not revive a requested failure that the captured events explicitly resolved");

    var missingEventCorrelationId = Guid.NewGuid().ToString("N");
    var missingEventPath = Path.Combine(scratch, "support-missing-event-fallback.zip");
    snapshotService.Create(
        missingEventPath,
        snapshotContext with
        {
            CorrelationId = missingEventCorrelationId,
            StableErrorCode = ConnectionDiagnosticErrorCodes.AuthenticationFailed.ToLowerInvariant(),
            FallbackFailureStage = ConnectionDiagnosticStage.Authentication,
        });
    var missingEventReport = ReadSupportBundleReport(missingEventPath);
    Assert(missingEventReport["stableErrorCode"]!.GetValue<string>() ==
               ConnectionDiagnosticErrorCodes.AuthenticationFailed &&
           missingEventReport["events"]!.AsArray().Count == 0,
        "support bundle normalizes and preserves the requested failure when event recording is unavailable");

    var canonicalFallbacks = new (string Code, ConnectionDiagnosticStage Stage)[]
    {
        (ConnectionDiagnosticErrorCodes.InputInvalid, ConnectionDiagnosticStage.InputValidation),
        (ConnectionDiagnosticErrorCodes.DnsLookupFailed, ConnectionDiagnosticStage.DnsAndTcp),
        (ConnectionDiagnosticErrorCodes.TcpConnectionRefused, ConnectionDiagnosticStage.DnsAndTcp),
        (ConnectionDiagnosticErrorCodes.TcpTimedOut, ConnectionDiagnosticStage.DnsAndTcp),
        (ConnectionDiagnosticErrorCodes.TcpUnreachable, ConnectionDiagnosticStage.DnsAndTcp),
        (ConnectionDiagnosticErrorCodes.TcpFailed, ConnectionDiagnosticStage.DnsAndTcp),
        (ConnectionDiagnosticErrorCodes.RoutePolicyBlocked, ConnectionDiagnosticStage.ProxyOrJumpRoute),
        (ConnectionDiagnosticErrorCodes.RouteSocks5Refused, ConnectionDiagnosticStage.ProxyOrJumpRoute),
        (ConnectionDiagnosticErrorCodes.RouteProxyRefused, ConnectionDiagnosticStage.ProxyOrJumpRoute),
        (ConnectionDiagnosticErrorCodes.RouteJumpRefused, ConnectionDiagnosticStage.ProxyOrJumpRoute),
        (ConnectionDiagnosticErrorCodes.RouteAuthenticationFailed, ConnectionDiagnosticStage.ProxyOrJumpRoute),
        (ConnectionDiagnosticErrorCodes.RouteTimedOut, ConnectionDiagnosticStage.ProxyOrJumpRoute),
        (ConnectionDiagnosticErrorCodes.RouteFailed, ConnectionDiagnosticStage.ProxyOrJumpRoute),
        (ConnectionDiagnosticErrorCodes.SshHandshakeTimedOut, ConnectionDiagnosticStage.SshHandshake),
        (ConnectionDiagnosticErrorCodes.SshHandshakeFailed, ConnectionDiagnosticStage.SshHandshake),
        (ConnectionDiagnosticErrorCodes.HostKeyChanged, ConnectionDiagnosticStage.HostKey),
        (ConnectionDiagnosticErrorCodes.HostKeyRejected, ConnectionDiagnosticStage.HostKey),
        (ConnectionDiagnosticErrorCodes.AuthenticationFailed, ConnectionDiagnosticStage.Authentication),
        (ConnectionDiagnosticErrorCodes.AuthenticationTimedOut, ConnectionDiagnosticStage.Authentication),
        (ConnectionDiagnosticErrorCodes.AuthenticationKeyFileMissing, ConnectionDiagnosticStage.Authentication),
        (ConnectionDiagnosticErrorCodes.AuthenticationKeyFileDenied, ConnectionDiagnosticStage.Authentication),
        (ConnectionDiagnosticErrorCodes.PtyRequestFailed, ConnectionDiagnosticStage.Pty),
        (ConnectionDiagnosticErrorCodes.SftpSubsystemUnavailable, ConnectionDiagnosticStage.SftpSubsystem),
        (ConnectionDiagnosticErrorCodes.PortForwardingFailed, ConnectionDiagnosticStage.PortForwarding),
    };
    foreach (var (code, stage) in canonicalFallbacks)
    {
        var fallbackPreview = snapshotService.Preview(Guid.NewGuid().ToString("N"), code, stage);
        Assert(fallbackPreview.StableErrorCode == code &&
               fallbackPreview.FailureStage == stage &&
               fallbackPreview.FailureStatus == ConnectionDiagnosticStatus.Failed,
            $"support bundle accepts canonical fallback provenance for {code}");

        var mismatchedStage = stage == ConnectionDiagnosticStage.InputValidation
            ? ConnectionDiagnosticStage.Pty
            : ConnectionDiagnosticStage.InputValidation;
        AssertThrows<ArgumentException>(
            () => snapshotService.Preview(Guid.NewGuid().ToString("N"), code, mismatchedStage),
            $"support bundle rejects mismatched fallback provenance for {code}");
    }

    foreach (var stage in Enum.GetValues<ConnectionDiagnosticStage>())
    {
        var cancelledPreview = snapshotService.Preview(
            Guid.NewGuid().ToString("N"),
            ConnectionDiagnosticErrorCodes.ConnectionCancelled,
            stage);
        var unexpectedPreview = snapshotService.Preview(
            Guid.NewGuid().ToString("N"),
            ConnectionDiagnosticErrorCodes.UnexpectedFailure,
            stage);
        Assert(cancelledPreview.FailureStage == stage &&
               cancelledPreview.FailureStatus == ConnectionDiagnosticStatus.Cancelled &&
               unexpectedPreview.FailureStage == stage &&
               unexpectedPreview.FailureStatus == ConnectionDiagnosticStatus.Failed,
            $"generic fallback codes remain valid for {stage}");
    }

    var legacyFallbackPreview = snapshotService.Preview(
        Guid.NewGuid().ToString("N"),
        ConnectionDiagnosticErrorCodes.PtyRequestFailed);
    Assert(legacyFallbackPreview.StableErrorCode == ConnectionDiagnosticErrorCodes.PtyRequestFailed &&
           legacyFallbackPreview.FailureStage is null &&
           legacyFallbackPreview.FailureStatus == ConnectionDiagnosticStatus.Failed,
        "legacy empty-correlation fallback remains compatible without invented stage provenance");
    AssertThrows<ArgumentException>(
        () => snapshotService.Preview(
            Guid.NewGuid().ToString("N"),
            ConnectionDiagnosticErrorCodes.None,
            ConnectionDiagnosticStage.Authentication),
        "support bundle rejects a fallback stage when there is no fallback failure code");

    var mismatchedFallbackPath = Path.Combine(scratch, "support-mismatched-fallback-stage.zip");
    File.WriteAllText(mismatchedFallbackPath, "sentinel fallback destination");
    AssertThrows<ArgumentException>(
        () => snapshotService.Create(
            mismatchedFallbackPath,
            snapshotContext with
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
                StableErrorCode = ConnectionDiagnosticErrorCodes.PtyRequestFailed,
                FallbackFailureStage = ConnectionDiagnosticStage.Authentication,
            },
            overwrite: true),
        "support bundle rejects a mismatched code and fallback stage before creation");
    Assert(File.ReadAllText(mismatchedFallbackPath) == "sentinel fallback destination",
        "mismatched fallback provenance preserves an existing destination");
    AssertThrows<ArgumentException>(
        () => snapshotService.Create(
            Path.Combine(scratch, "support-none-with-fallback-stage.zip"),
            snapshotContext with
            {
                CorrelationId = Guid.NewGuid().ToString("N"),
                StableErrorCode = ConnectionDiagnosticErrorCodes.None,
                FallbackFailureStage = ConnectionDiagnosticStage.Authentication,
            }),
        "support bundle rejects creation with a fallback stage but no fallback failure code");

    var partialEventCorrelationId = Guid.NewGuid().ToString("N");
    snapshotEvents.Append(
        partialEventCorrelationId,
        ConnectionDiagnosticStage.InputValidation,
        ConnectionDiagnosticStatus.Succeeded);
    snapshotEvents.Append(
        partialEventCorrelationId,
        ConnectionDiagnosticStage.DnsAndTcp,
        ConnectionDiagnosticStatus.Succeeded);
    var partialEventPath = Path.Combine(scratch, "support-partial-event-fallback.zip");
    snapshotService.Create(
        partialEventPath,
        snapshotContext with
        {
            CorrelationId = partialEventCorrelationId,
            StableErrorCode = ConnectionDiagnosticErrorCodes.AuthenticationFailed,
            FallbackFailureStage = ConnectionDiagnosticStage.Authentication,
        });
    var partialEventReport = ReadSupportBundleReport(partialEventPath);
    Assert(partialEventReport["stableErrorCode"]!.GetValue<string>() ==
               ConnectionDiagnosticErrorCodes.AuthenticationFailed &&
           partialEventReport["events"]!.AsArray().Count == 2,
        "stage-aware fallback preserves a failure whose append was missing after earlier events");

    var changedCorrelationId = Guid.NewGuid().ToString("N");
    snapshotEvents.Append(
        changedCorrelationId,
        ConnectionDiagnosticStage.Pty,
        ConnectionDiagnosticStatus.Failed,
        ConnectionDiagnosticErrorCodes.PtyRequestFailed);
    var changedPreview = snapshotService.Preview(changedCorrelationId);
    var changedContext = snapshotContext with
    {
        CorrelationId = changedCorrelationId,
        StableErrorCode = changedPreview.StableErrorCode,
        FallbackFailureStage = changedPreview.FailureStage,
        ExpectedDiagnosticSnapshotSha256 = changedPreview.SnapshotSha256,
    };
    snapshotEvents.Append(
        changedCorrelationId,
        ConnectionDiagnosticStage.Pty,
        ConnectionDiagnosticStatus.Succeeded);
    var changedPath = Path.Combine(scratch, "support-snapshot-changed.zip");
    AssertThrows<SupportBundleDiagnosticSnapshotChangedException>(
        () => snapshotService.Create(changedPath, changedContext),
        "support bundle rejects an event snapshot changed after UI preview");
    Assert(!File.Exists(changedPath),
        "changed diagnostic snapshot leaves no partial support bundle destination");
    var preservedChangedPath = Path.Combine(scratch, "support-snapshot-changed-preserved.zip");
    File.WriteAllText(preservedChangedPath, "sentinel snapshot destination");
    AssertThrows<SupportBundleDiagnosticSnapshotChangedException>(
        () => snapshotService.Create(preservedChangedPath, changedContext, overwrite: true),
        "snapshot mismatch is checked before replacing an existing destination");
    Assert(File.ReadAllText(preservedChangedPath) == "sentinel snapshot destination",
        "snapshot mismatch preserves the existing destination");

    var evictedEvents = new ConnectionDiagnosticEventStore(capacity: 1);
    var evictedService = new SupportBundleService(evictedEvents);
    var evictedCorrelationId = Guid.NewGuid().ToString("N");
    evictedEvents.Append(
        evictedCorrelationId,
        ConnectionDiagnosticStage.Pty,
        ConnectionDiagnosticStatus.Failed,
        ConnectionDiagnosticErrorCodes.PtyRequestFailed);
    evictedEvents.Append(
        evictedCorrelationId,
        ConnectionDiagnosticStage.Pty,
        ConnectionDiagnosticStatus.Succeeded);
    var evictedPath = Path.Combine(scratch, "support-evicted-failure-resolved.zip");
    evictedService.Create(
        evictedPath,
        snapshotContext with
        {
            CorrelationId = evictedCorrelationId,
            StableErrorCode = ConnectionDiagnosticErrorCodes.PtyRequestFailed,
            FallbackFailureStage = ConnectionDiagnosticStage.Pty,
        });
    var evictedReport = ReadSupportBundleReport(evictedPath);
    Assert(evictedReport["stableErrorCode"]!.GetValue<string>() ==
               ConnectionDiagnosticErrorCodes.None &&
           evictedReport["events"]!.AsArray().Count == 1 &&
           evictedReport["events"]![0]!["status"]!.GetValue<string>() ==
               ConnectionDiagnosticStatus.Succeeded.ToString(),
        "an explicit same-stage recovery remains authoritative after failure eviction");

    AssertThrows<ArgumentException>(
        () => snapshotService.Create(
            Path.Combine(scratch, "support-invalid-snapshot-hash.zip"),
            snapshotContext with { ExpectedDiagnosticSnapshotSha256 = new string('A', 64) }),
        "support bundle rejects a non-canonical expected snapshot digest");

    var mismatchCorrelationId = Guid.NewGuid().ToString("N");
    snapshotEvents.Append(
        mismatchCorrelationId,
        ConnectionDiagnosticStage.Pty,
        ConnectionDiagnosticStatus.Failed,
        ConnectionDiagnosticErrorCodes.PtyRequestFailed);
    var mismatchPath = Path.Combine(scratch, "support-diagnostic-code-mismatch.zip");
    AssertThrows<SupportBundleDiagnosticCodeMismatchException>(
        () => snapshotService.Create(
            mismatchPath,
            snapshotContext with
            {
                CorrelationId = mismatchCorrelationId,
                StableErrorCode = ConnectionDiagnosticErrorCodes.AuthenticationFailed,
            }),
        "support bundle rejects different requested and captured failure codes");
    Assert(!File.Exists(mismatchPath),
        "diagnostic-code mismatch leaves no partial support bundle destination");

    AssertThrows<ArgumentException>(
        () => service.Create(
            Path.Combine(scratch, "unsafe-build.zip"),
            context with { AppBuild = @"C:\Users\operator\secret" }),
        "support bundle rejects path-like build metadata");
    AssertThrows<ArgumentException>(
        () => events.Append(
            correlationId,
            ConnectionDiagnosticStage.SshHandshake,
            ConnectionDiagnosticStatus.Failed,
            "HOSTNAME_LEAK.example"),
        "diagnostic event store rejects unreviewed error codes");

    ConnectionLogStore.Clear();
}

static JsonObject ReadSupportBundleReport(string path)
{
    using var archive = ZipFile.OpenRead(path);
    return JsonNode.Parse(
        ReadZipEntry(archive, SupportBundleService.ReportEntryName))!.AsObject();
}

static byte[] ReadZipEntry(ZipArchive archive, string name)
{
    var entry = archive.GetEntry(name)
        ?? throw new InvalidDataException($"Missing support-bundle entry: {name}");
    if (entry.Length > SupportBundleService.MaximumReportBytes)
        throw new InvalidDataException($"Oversized support-bundle entry: {name}");
    using var input = entry.Open();
    using var output = new MemoryStream(checked((int)entry.Length));
    input.CopyTo(output);
    return output.ToArray();
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
        var transportErrorEvent = clientType.GetEvent(nameof(BaseClient.ErrorOccurred));
        Assert(transportErrorEvent?.EventHandlerType == typeof(EventHandler<ExceptionEventArgs>),
            $"SSH.NET {clientType.Name} ErrorOccurred API");
    }

    var connectionInfoType = typeof(Renci.SshNet.ConnectionInfo);
    foreach (var propertyName in new[]
             {
                 nameof(Renci.SshNet.ConnectionInfo.ServerVersion),
                 nameof(Renci.SshNet.ConnectionInfo.ClientVersion),
                 nameof(Renci.SshNet.ConnectionInfo.CurrentKeyExchangeAlgorithm),
                 nameof(Renci.SshNet.ConnectionInfo.CurrentHostKeyAlgorithm),
                 nameof(Renci.SshNet.ConnectionInfo.CurrentClientEncryption),
                 nameof(Renci.SshNet.ConnectionInfo.CurrentServerEncryption),
                 nameof(Renci.SshNet.ConnectionInfo.CurrentClientHmacAlgorithm),
                 nameof(Renci.SshNet.ConnectionInfo.CurrentServerHmacAlgorithm),
                 nameof(Renci.SshNet.ConnectionInfo.CurrentClientCompressionAlgorithm),
                 nameof(Renci.SshNet.ConnectionInfo.CurrentServerCompressionAlgorithm),
             })
    {
        var property = connectionInfoType.GetProperty(propertyName);
        Assert(property is { PropertyType: { } propertyType, CanRead: true } &&
               propertyType == typeof(string) && property.GetMethod?.IsPublic == true,
            $"SSH.NET negotiated connection property {propertyName}");
    }

    Assert(
        typeof(ShellStream).GetMethod(
            nameof(ShellStream.ChangeWindowSize),
            [typeof(uint), typeof(uint), typeof(uint), typeof(uint)]) is not null,
        "SSH.NET runtime PTY resize API");
}

static void AssertNegotiatedConnectionInfoContract()
{
    var snapshot = new SshNegotiatedConnectionInfo(
        " SSH-2.0-test-server ",
        " SSH-2.0-sutty ",
        " curve25519-sha256 ",
        " ssh-ed25519 ",
        " SHA256:host-fingerprint ",
        " chacha20-poly1305@openssh.com ",
        " aes256-gcm@openssh.com ",
        " hmac-sha2-256-etm@openssh.com ",
        " hmac-sha2-512-etm@openssh.com ",
        " none ",
        " zlib@openssh.com ");

    Assert(snapshot.ServerVersion == "SSH-2.0-test-server" &&
           snapshot.ClientVersion == "SSH-2.0-sutty" &&
           snapshot.KeyExchangeAlgorithm == "curve25519-sha256" &&
           snapshot.HostKeyAlgorithm == "ssh-ed25519" &&
           snapshot.HostKeySha256Fingerprint == "SHA256:host-fingerprint" &&
           snapshot.ClientToServerCipher == "chacha20-poly1305@openssh.com" &&
           snapshot.ServerToClientCipher == "aes256-gcm@openssh.com" &&
           snapshot.ClientToServerMac == "hmac-sha2-256-etm@openssh.com" &&
           snapshot.ServerToClientMac == "hmac-sha2-512-etm@openssh.com" &&
           snapshot.ClientToServerCompression == "none" &&
           snapshot.ServerToClientCompression == "zlib@openssh.com",
        "negotiated-information snapshot normalizes credential-free values");

    var expectedProperties = new[]
    {
        nameof(SshNegotiatedConnectionInfo.ClientToServerCipher),
        nameof(SshNegotiatedConnectionInfo.ClientToServerCompression),
        nameof(SshNegotiatedConnectionInfo.ClientToServerMac),
        nameof(SshNegotiatedConnectionInfo.ClientVersion),
        nameof(SshNegotiatedConnectionInfo.HostKeyAlgorithm),
        nameof(SshNegotiatedConnectionInfo.HostKeySha256Fingerprint),
        nameof(SshNegotiatedConnectionInfo.KeyExchangeAlgorithm),
        nameof(SshNegotiatedConnectionInfo.ServerToClientCipher),
        nameof(SshNegotiatedConnectionInfo.ServerToClientCompression),
        nameof(SshNegotiatedConnectionInfo.ServerToClientMac),
        nameof(SshNegotiatedConnectionInfo.ServerVersion),
    }.Order(StringComparer.Ordinal).ToArray();
    var publicProperties = typeof(SshNegotiatedConnectionInfo)
        .GetProperties(System.Reflection.BindingFlags.Instance |
                       System.Reflection.BindingFlags.Public);

    Assert(publicProperties.Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .SequenceEqual(expectedProperties, StringComparer.Ordinal),
        "negotiated-information public contract contains only the reviewed fields");
    Assert(publicProperties.All(property =>
            property.PropertyType == typeof(string) && property.SetMethod is null),
        "negotiated-information snapshot is immutable and retains no raw key bytes");
    Assert(publicProperties.All(property =>
            !property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) &&
            !property.Name.Contains("Passphrase", StringComparison.OrdinalIgnoreCase) &&
            !property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase) &&
            !property.Name.Contains("Raw", StringComparison.OrdinalIgnoreCase)),
        "negotiated-information snapshot exposes no secret or raw-key field");
}

static void AssertLastUsedRaceIsBestEffort()
{
    var endpoint = HostEndpointIdentity.Create("last-used-race.example", 22);
    var trustedKey = HostKeyData.Create("ssh-ed25519", [11, 12, 13, 14]);
    var replacementKey = HostKeyData.Create("ssh-ed25519", [21, 22, 23, 24]);
    var record = new KnownHostRecord(
        endpoint,
        trustedKey,
        DateTimeOffset.UtcNow.AddMinutes(-2),
        DateTimeOffset.UtcNow.AddMinutes(-1));
    var markMethod = typeof(SshNetSession).GetMethod(
        "TryMarkHostKeyUsed",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var verificationField = typeof(SshNetHostKeyVerifier).GetField(
        "_lastVerification",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    Assert(markMethod is not null && verificationField is not null,
        "last-used race test seams remain available");

    foreach (var raceError in new Exception[]
             {
                 new KeyNotFoundException("synthetic concurrent removal"),
                 new HostKeyChangedException(endpoint, trustedKey, replacementKey),
             })
    {
        var store = new ThrowingMarkUsedKnownHostsStore(record, raceError);
        var verifier = new SshNetHostKeyVerifier(
            endpoint,
            new HostKeyTrustContext(store));
        verificationField!.SetValue(
            verifier,
            new HostKeyVerification(
                endpoint,
                trustedKey,
                HostKeyTrustState.Trusted,
                HostKeyTrustSource.Persistent,
                trustedKey));
        var session = new SshNetSession(new SshConnectionInfo
        {
            Host = "last-used-race.example",
            Port = 22,
            Username = "operator",
            AuthMethod = SshAuthMethod.Password,
            Password = "synthetic-only",
        });

        markMethod!.Invoke(session, [verifier, endpoint]);
        Assert(store.MarkUsedCalls == 1,
            $"{raceError.GetType().Name} during best-effort last-used metadata is attempted once");
    }
}

static async Task AssertKeyboardInteractiveCancellationAsync()
{
    var info = new SshConnectionInfo
    {
        Host = "127.0.0.1",
        Port = 22,
        Username = "keyboard-interactive-user",
        AuthMethod = SshAuthMethod.KeyboardInteractive,
        KeyboardInteractivePromptAsync = (_, _) =>
            Task.FromResult<IReadOnlyList<string>?>(null),
    };
    var session = new SshNetSession(info);
    var sessionType = typeof(SshNetSession);
    var buildMethod = sessionType.GetMethod(
        "BuildKeyboardInteractive",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var cancellationDetector = sessionType.GetMethod(
        "IsKeyboardInteractiveAuthenticationCancellation",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
    var completeMethod = sessionType.GetMethod(
        "CompleteCancelledConnectionAttemptAsync",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    Assert(buildMethod is not null && cancellationDetector is not null && completeMethod is not null,
        "keyboard-interactive cancellation lifecycle test seams remain available");

    using var method = (KeyboardInteractiveAuthenticationMethod)buildMethod!.Invoke(
        session,
        new object?[] { info.Username, "", CancellationToken.None })!;
    var promptHandlerField = typeof(KeyboardInteractiveAuthenticationMethod).GetField(
        "AuthenticationPrompt",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var promptHandler = (EventHandler<AuthenticationPromptEventArgs>?)promptHandlerField?.GetValue(method);
    Assert(promptHandler is not null,
        "keyboard-interactive method installs its prompt handler");
    var prompt = new AuthenticationPrompt(0, false, "One-time password:");
    var promptArgs = new AuthenticationPromptEventArgs(
        info.Username,
        "Additional authentication",
        "en-US",
        [prompt]);

    OperationCanceledException? promptCancellation = null;
    try
    {
        promptHandler!(method, promptArgs);
    }
    catch (OperationCanceledException error)
    {
        promptCancellation = error;
    }
    Assert(promptCancellation is not null,
        "dismissed keyboard-interactive prompt raises a cancellation");

    var wrappedCancellation = new InvalidOperationException(
        "synthetic SSH.NET wrapper",
        promptCancellation);
    Assert((bool)cancellationDetector!.Invoke(null, [wrappedCancellation])!,
        "wrapped prompt dismissal remains identifiable as user cancellation");
    Assert(!(bool)cancellationDetector.Invoke(null, [new OperationCanceledException()])!,
        "unrelated operation cancellation is not relabelled as prompt dismissal");

    Exception? taskPropagatedCancellation = null;
    try
    {
        await Task.Run(() => Task.Run(() => promptHandler!(method, promptArgs)));
    }
    catch (Exception error)
    {
        taskPropagatedCancellation = error;
    }
    Assert(taskPropagatedCancellation is not null &&
           (bool)cancellationDetector.Invoke(null, [taskPropagatedCancellation])!,
        "prompt cancellation marker survives the SSH connect task boundary");

    info.KeyboardInteractivePromptAsync = (_, _) =>
        Task.FromResult<IReadOnlyList<string>?>([]);
    InvalidOperationException? invalidResponse = null;
    try
    {
        promptHandler!(method, promptArgs);
    }
    catch (InvalidOperationException error)
    {
        invalidResponse = error;
    }
    Assert(invalidResponse is not null &&
           !(bool)cancellationDetector.Invoke(null, [invalidResponse])!,
        "invalid keyboard-interactive responses remain ordinary failures");

    var completion = (Task)completeMethod!.Invoke(
        session,
        new object?[]
        {
            wrappedCancellation,
            ConnectionDiagnosticStage.Authentication,
            System.Diagnostics.Stopwatch.GetTimestamp(),
            true,
        })!;
    await completion;
    Assert(session.State == SessionState.Disconnected &&
           session.LastDiagnostic is
           {
               Stage: ConnectionDiagnosticStage.Authentication,
               Status: ConnectionDiagnosticStatus.Cancelled,
               ErrorCode: ConnectionDiagnosticErrorCodes.ConnectionCancelled,
           },
        "prompt dismissal completes the session as cancelled authentication");
    var promptCancellationEvents = ConnectionDiagnosticEventStore.Shared
        .Snapshot(session.CorrelationContext.CorrelationId);
    Assert(promptCancellationEvents.LastOrDefault() is
            {
                Stage: ConnectionDiagnosticStage.Authentication,
                Status: ConnectionDiagnosticStatus.Cancelled,
                ErrorCode: ConnectionDiagnosticErrorCodes.ConnectionCancelled,
            },
        "prompt dismissal records a stable cancelled-authentication diagnostic event");
    Assert(promptCancellationEvents.Any(entry =>
               entry.Stage == ConnectionDiagnosticStage.DnsAndTcp &&
               entry.Status == ConnectionDiagnosticStatus.Succeeded) &&
           promptCancellationEvents.Any(entry =>
               entry.Stage == ConnectionDiagnosticStage.SshHandshake &&
               entry.Status == ConnectionDiagnosticStatus.Succeeded) &&
           promptCancellationEvents.Any(entry =>
               entry.Stage == ConnectionDiagnosticStage.HostKey &&
               entry.Status == ConnectionDiagnosticStatus.Succeeded),
        "target prompt dismissal completes the transport stages that reached authentication");
}

static async Task AssertJumpRouteDiagnosticBoundaryAsync()
{
    static SshNetSession CreateJumpSession() => new(new SshConnectionInfo
    {
        Host = "target.example",
        Port = 22,
        Username = "target-user",
        AuthMethod = SshAuthMethod.Password,
        Route = new ConnectionRoute
        {
            Type = ConnectionRouteType.SshJump,
            Host = "jump.example",
            Port = 22,
            Username = "jump-user",
            AuthMethod = SshAuthMethod.Password,
        },
    });

    var recordMethod = typeof(SshNetSession).GetMethod(
        "RecordCompletedConnectionStagesBefore",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var completeCancellationMethod = typeof(SshNetSession).GetMethod(
        "CompleteCancelledConnectionAttemptAsync",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var promptCancellationType = typeof(SshNetSession).GetNestedType(
        "KeyboardInteractiveAuthenticationCancelledException",
        System.Reflection.BindingFlags.NonPublic);
    var promptCancellationConstructor = promptCancellationType?.GetConstructor(
        System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic,
        binder: null,
        [typeof(CancellationToken)],
        modifiers: null);
    Assert(recordMethod is not null && completeCancellationMethod is not null &&
           promptCancellationConstructor is not null,
        "jump-route diagnostic-boundary test seam remains available");

    var jumpSideErrors = new Exception[]
    {
        new SecurityException("synthetic jump-host key rejection"),
        new SshAuthenticationException("synthetic jump-host authentication rejection"),
        new SshException("synthetic jump-host handshake rejection"),
    };
    foreach (var jumpSideError in jumpSideErrors)
    {
        var jumpSideSession = CreateJumpSession();
        ConnectionDiagnosticEventStore.Shared.Append(
            jumpSideSession.CorrelationContext.CorrelationId,
            ConnectionDiagnosticResult.Running(
                ConnectionDiagnosticStage.ProxyOrJumpRoute));
        var jumpSideFailure = ConnectionExceptionClassifier.Classify(
            jumpSideError,
            ConnectionDiagnosticStage.ProxyOrJumpRoute,
            ConnectionRouteType.SshJump);
        recordMethod!.Invoke(
            jumpSideSession,
            new object?[] { jumpSideFailure, jumpSideError, false });
        ConnectionDiagnosticEventStore.Shared.Append(
            jumpSideSession.CorrelationContext.CorrelationId,
            jumpSideFailure);
        var jumpSideEvents = ConnectionDiagnosticEventStore.Shared.Snapshot(
            jumpSideSession.CorrelationContext.CorrelationId);
        Assert(!jumpSideEvents.Any(entry =>
                entry.Stage == ConnectionDiagnosticStage.ProxyOrJumpRoute &&
                entry.Status == ConnectionDiagnosticStatus.Succeeded),
            "jump-host key, authentication, and handshake failures do not complete the route");
        var terminalRouteEvent = jumpSideEvents.Last(entry =>
            entry.Stage == ConnectionDiagnosticStage.ProxyOrJumpRoute);
        Assert(terminalRouteEvent.Status == ConnectionDiagnosticStatus.Failed &&
               terminalRouteEvent.ErrorCode == jumpSideFailure.ErrorCode,
            "jump-host failure closes the running route with its causal error code");
        Assert(!jumpSideEvents.Any(entry =>
                (entry.Stage is ConnectionDiagnosticStage.DnsAndTcp or
                    ConnectionDiagnosticStage.SshHandshake) &&
                entry.Status == ConnectionDiagnosticStatus.Succeeded),
            "jump-host failures do not claim completed target transport stages");
        if (jumpSideFailure.Stage == ConnectionDiagnosticStage.HostKey)
        {
            Assert(jumpSideEvents.Last() is
                   {
                       Stage: ConnectionDiagnosticStage.HostKey,
                       Status: ConnectionDiagnosticStatus.Failed,
                   } finalHostKeyEvent &&
                   finalHostKeyEvent.ErrorCode == terminalRouteEvent.ErrorCode,
                "jump-host key failure preserves HostKey as the final causal outcome");
        }
    }

    var jumpPromptSession = CreateJumpSession();
    ConnectionDiagnosticEventStore.Shared.Append(
        jumpPromptSession.CorrelationContext.CorrelationId,
        ConnectionDiagnosticResult.Running(
            ConnectionDiagnosticStage.ProxyOrJumpRoute));
    var jumpPromptCancellation = (Exception)promptCancellationConstructor!.Invoke(
        [CancellationToken.None]);
    var jumpPromptCompletion = (Task)completeCancellationMethod!.Invoke(
        jumpPromptSession,
        new object?[]
        {
            jumpPromptCancellation,
            ConnectionDiagnosticStage.ProxyOrJumpRoute,
            System.Diagnostics.Stopwatch.GetTimestamp(),
            false,
        })!;
    await jumpPromptCompletion;
    var jumpPromptEvents = ConnectionDiagnosticEventStore.Shared.Snapshot(
        jumpPromptSession.CorrelationContext.CorrelationId);
    Assert(jumpPromptSession.State == SessionState.Disconnected &&
           jumpPromptSession.LastDiagnostic is
           {
               Stage: ConnectionDiagnosticStage.ProxyOrJumpRoute,
               Status: ConnectionDiagnosticStatus.Cancelled,
               ErrorCode: ConnectionDiagnosticErrorCodes.ConnectionCancelled,
           } &&
           jumpPromptEvents.LastOrDefault() is
           {
               Stage: ConnectionDiagnosticStage.ProxyOrJumpRoute,
               Status: ConnectionDiagnosticStatus.Cancelled,
           } &&
           !jumpPromptEvents.Any(entry =>
               entry.Stage == ConnectionDiagnosticStage.Authentication),
        "jump-host prompt dismissal closes the running route as cancelled, not target authentication");

    var targetSession = CreateJumpSession();
    var targetHostKeyError = new SecurityException("synthetic target host-key rejection");
    var targetHostKeyFailure = ConnectionExceptionClassifier.Classify(
        targetHostKeyError,
        ConnectionDiagnosticStage.SshHandshake,
        ConnectionRouteType.SshJump);
    recordMethod!.Invoke(
        targetSession,
        new object?[] { targetHostKeyFailure, targetHostKeyError, true });
    var targetEvents = ConnectionDiagnosticEventStore.Shared.Snapshot(
        targetSession.CorrelationContext.CorrelationId);
    Assert(targetEvents.Any(entry =>
               entry.Stage == ConnectionDiagnosticStage.ProxyOrJumpRoute &&
               entry.Status == ConnectionDiagnosticStatus.Succeeded) &&
           targetEvents.Any(entry =>
               entry.Stage == ConnectionDiagnosticStage.DnsAndTcp &&
               entry.Status == ConnectionDiagnosticStatus.Succeeded) &&
           targetEvents.Any(entry =>
               entry.Stage == ConnectionDiagnosticStage.SshHandshake &&
               entry.Status == ConnectionDiagnosticStatus.Succeeded),
        "target host-key failure records the already-completed jump route and target transport stages");

    var targetPromptSession = CreateJumpSession();
    var targetPromptCancellation = (Exception)promptCancellationConstructor.Invoke(
        [CancellationToken.None]);
    var targetPromptCompletion = (Task)completeCancellationMethod.Invoke(
        targetPromptSession,
        new object?[]
        {
            targetPromptCancellation,
            ConnectionDiagnosticStage.Authentication,
            System.Diagnostics.Stopwatch.GetTimestamp(),
            true,
        })!;
    await targetPromptCompletion;
    var targetPromptEvents = ConnectionDiagnosticEventStore.Shared.Snapshot(
        targetPromptSession.CorrelationContext.CorrelationId);
    Assert(targetPromptEvents.Any(entry =>
               entry.Stage == ConnectionDiagnosticStage.ProxyOrJumpRoute &&
               entry.Status == ConnectionDiagnosticStatus.Succeeded) &&
           targetPromptEvents.Any(entry =>
               entry.Stage == ConnectionDiagnosticStage.DnsAndTcp &&
               entry.Status == ConnectionDiagnosticStatus.Succeeded) &&
           targetPromptEvents.Any(entry =>
               entry.Stage == ConnectionDiagnosticStage.SshHandshake &&
               entry.Status == ConnectionDiagnosticStatus.Succeeded) &&
           targetPromptEvents.Any(entry =>
               entry.Stage == ConnectionDiagnosticStage.HostKey &&
               entry.Status == ConnectionDiagnosticStatus.Succeeded) &&
           targetPromptEvents.LastOrDefault() is
           {
               Stage: ConnectionDiagnosticStage.Authentication,
               Status: ConnectionDiagnosticStatus.Cancelled,
           },
        "target prompt dismissal completes the jump and target transport before cancelling authentication");

    var targetLocalAuthSession = CreateJumpSession();
    var targetLocalAuthError = new FileNotFoundException(
        "synthetic target private-key disappearance");
    var targetLocalAuthFailure = ConnectionExceptionClassifier.Classify(
        targetLocalAuthError,
        ConnectionDiagnosticStage.Authentication,
        ConnectionRouteType.SshJump);
    recordMethod!.Invoke(
        targetLocalAuthSession,
        new object?[] { targetLocalAuthFailure, targetLocalAuthError, true });
    var targetLocalAuthEvents = ConnectionDiagnosticEventStore.Shared.Snapshot(
        targetLocalAuthSession.CorrelationContext.CorrelationId);
    Assert(targetLocalAuthEvents.Count(entry =>
               entry.Stage == ConnectionDiagnosticStage.ProxyOrJumpRoute &&
               entry.Status == ConnectionDiagnosticStatus.Succeeded) == 1 &&
           !targetLocalAuthEvents.Any(entry =>
               (entry.Stage is ConnectionDiagnosticStage.DnsAndTcp or
                   ConnectionDiagnosticStage.SshHandshake) &&
               entry.Status == ConnectionDiagnosticStatus.Succeeded),
        "target-local authentication failure keeps the completed jump route without claiming target network success");
}

static async Task AssertUnexpectedPrimaryTransportFailureAsync()
{
    var session = new SshNetSession(new SshConnectionInfo
    {
        Host = "127.0.0.1",
        Port = 22,
        Username = "transport-fault-user",
        AuthMethod = SshAuthMethod.Password,
        Password = "synthetic-only",
    });
    var client = new SshClient(new Renci.SshNet.ConnectionInfo(
        "127.0.0.1",
        22,
        "transport-fault-user",
        new NoneAuthenticationMethod("transport-fault-user")));
    var snapshot = new SshNegotiatedConnectionInfo(
        "SSH-2.0-test-server",
        "SSH-2.0-sutty",
        "curve25519-sha256",
        "ssh-ed25519",
        "SHA256:test-fingerprint",
        "aes256-gcm@openssh.com",
        "aes256-gcm@openssh.com",
        "none",
        "none",
        "none",
        "none");

    var sessionType = typeof(SshNetSession);
    var publishMethod = sessionType.GetMethod(
        "PublishPrimaryTransport",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var errorMethod = sessionType.GetMethod(
        "PrimarySsh_ErrorOccurred",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var stateProperty = sessionType.GetProperty(nameof(SshNetSession.State));
    Assert(publishMethod is not null && errorMethod is not null &&
           stateProperty?.SetMethod is { IsPublic: false },
        "primary-transport failure lifecycle test seam remains available");

    var observedStates = new List<SessionState>();
    var stateGate = new object();
    session.StateChanged += (_, state) =>
    {
        lock (stateGate)
            observedStates.Add(state);
    };

    publishMethod!.Invoke(session, [client, snapshot]);
    stateProperty!.SetValue(session, SessionState.Connected);
    var transportError = new IOException("synthetic primary transport loss");
    errorMethod!.Invoke(session, [client, new ExceptionEventArgs(transportError)]);

    Assert(session.NegotiatedInfo is null,
        "primary-transport error clears negotiated information synchronously");
    var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
    while (session.State != SessionState.Failed && DateTimeOffset.UtcNow < deadline)
        await Task.Delay(10);

    Assert(session.State == SessionState.Failed &&
           session.TerminalState == TerminalState.Closed &&
           session.SftpState == SftpConnectionState.NotConnected,
        "unexpected primary-transport loss reaches stable failed subsystem states");
    Assert(session.LastError == transportError.Message,
        "unexpected primary-transport loss preserves the original error");
    lock (stateGate)
    {
        Assert(observedStates.Contains(SessionState.Disconnecting) &&
               observedStates.LastOrDefault() == SessionState.Failed,
            "unexpected primary-transport loss publishes disconnecting before failed");
    }
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

sealed class ThrowingMarkUsedKnownHostsStore(
    KnownHostRecord record,
    Exception markUsedError) : IKnownHostsStore
{
    public int MarkUsedCalls { get; private set; }

    public string StorePath => "memory://throwing-mark-used";

    public KnownHostRecord? Find(HostEndpointIdentity endpoint) =>
        endpoint == record.Endpoint ? record : null;

    public KnownHostRecord MarkUsed(
        HostEndpointIdentity endpoint,
        HostKeyData key,
        DateTimeOffset? usedAtUtc = null)
    {
        MarkUsedCalls++;
        throw markUsedError;
    }

    public KnownHostsSnapshot GetSnapshot(int activityLimit = 100) =>
        new([record], []);

    public IReadOnlyList<KnownHostRecord> GetAll() => [record];

    public IReadOnlyList<KnownHostActivityRecord> GetActivity(int limit = 100) => [];

    public KnownHostRecord Trust(HostEndpointIdentity endpoint, HostKeyData key) =>
        throw new NotSupportedException();

    public KnownHostRecord Rotate(
        HostEndpointIdentity endpoint,
        HostKeyData expectedTrustedKey,
        HostKeyData replacementKey,
        string reason) => throw new NotSupportedException();

    public bool Remove(HostEndpointIdentity endpoint, HostKeyData expectedTrustedKey) =>
        throw new NotSupportedException();
}
