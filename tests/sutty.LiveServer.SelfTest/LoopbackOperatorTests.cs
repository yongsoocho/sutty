using System.Net;
using System.Net.Sockets;
using System.Text;
using sutty.Core.Models;
using sutty.Core.Routing;
using sutty.Core.Sessions;
using sutty.Core.Sftp;

internal static class LoopbackOperatorTests
{
    internal static async Task RunAsync(bool jump, string scratch)
    {
        var route = jump ? "jump" : "direct";
        var session = LoopbackTransferTests.CreateSession(jump, jump ? "otp-user" : "key-user",
            jump ? SshAuthMethod.KeyboardInteractive : SshAuthMethod.PublicKey);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        string workingCopy;
        try
        {
            await session.ConnectAsync(ct);
            Check(session.State == SessionState.Connected && session.SftpState == SftpConnectionState.Ready,
                "The terminal transport and SFTP must both authenticate.");
            await VerifyLocalForwardAsync(session, ct);
            var path = $"/edit-{route}.txt";
            var edit = new RemoteEditSession($"loopback-{route}", path, scratch);
            var before = await RemoteEditSession.ReadRemoteStampAsync(session.Sftp, path, ct);
            Check(before is not null, "Fixture file must exist.");
            workingCopy = edit.AllocateWorkingCopy();
            await session.Sftp.DownloadPathAsync(path, workingCopy, SftpTransferOptions.Default, ct: ct);
            await edit.AcceptDownloadAsync(before!, await RemoteEditSession.ReadRemoteStampAsync(session.Sftp, path, ct), ct);
            Check(!edit.HasRemoteContentConflict(await edit.ReadRemoteVersionAsync(session.Sftp, ct)), "Baseline must match.");
            await File.WriteAllTextAsync(workingCopy, "local edit", ct);
            var savedTime = new DateTime(2020, 4, 5, 6, 7, 8, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(workingCopy, savedTime.AddMilliseconds(750));
            var snapshot = await edit.CreateUploadAsync(ct);
            await session.Sftp.UploadPathAsync(snapshot.LocalPath, path, SftpTransferOptions.Default, ct: ct);
            var after = await RemoteEditSession.ReadRemoteStampAsync(session.Sftp, path, ct);
            Check(after?.Modified == savedTime, "An editor upload must preserve the actual save time to the second.");
            await edit.AcceptUploadAsync(snapshot, path, after, ct);
            Check(!edit.HasRemoteContentConflict(await edit.ReadRemoteVersionAsync(session.Sftp, ct)), "Saved edit must verify.");
            await File.WriteAllTextAsync(workingCopy, "pending edit", ct);
            var changed = Path.Combine(scratch, $"other-{route}.txt");
            await File.WriteAllTextAsync(changed, "other", ct);
            await session.Sftp.UploadPathAsync(changed, path,
                SftpTransferOptions.Default with { ConflictPolicy = SftpConflictPolicy.Overwrite }, ct: ct);
            Check(await edit.HasChangesAsync(ct) && edit.HasRemoteContentConflict(await edit.ReadRemoteVersionAsync(session.Sftp, ct)),
                "A conflicting remote edit must require review and retain the working copy.");
            await CheckMetadataFailureAsync(session.Sftp, changed, route, ct);
            await CheckFileTimesAsync(session.Sftp, scratch, route, ct);
        }
        finally { await session.DisconnectAsync(); }
        Check(await File.ReadAllTextAsync(workingCopy, ct) == "pending edit", "The working copy must survive disconnect.");
        Console.WriteLine($"Operator checks via {route} passed: authentication, SFTP edit conflict/recovery, tunnel collision/restart/stop.");
    }

    private static async Task CheckFileTimesAsync(ISftpService sftp, string scratch, string route, CancellationToken ct)
    {
        var local = Path.Combine(scratch, $"new-{route}.txt");
        var name = Path.GetFileName(local);
        var remote = "/" + name;
        var modified = new DateTime(2018, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        await File.WriteAllTextAsync(local, "new file", ct);
        File.SetLastWriteTimeUtc(local, modified.AddMilliseconds(250));
        await sftp.UploadFileAsync(local, "/", ct: ct);
        Check((await RemoteEditSession.ReadRemoteStampAsync(sftp, remote, ct))?.Modified == modified,
            "A new single-file upload must preserve modification time.");
        var copy = local + ".copy";
        await sftp.DownloadFileAsync(remote, copy, ct: ct);
        Check(File.GetLastWriteTimeUtc(copy) == modified, "A new single-file download must preserve modification time.");

        // Same bytes with a stale timestamp must not take the checksum fast path.
        var updated = modified.AddDays(1);
        File.SetLastWriteTimeUtc(local, updated.AddMilliseconds(900));
        var result = await sftp.UploadPathAsync(local, remote, SftpTransferOptions.Default, ct: ct);
        var stamp = await RemoteEditSession.ReadRemoteStampAsync(sftp, remote, ct);
        Check(result.FilesTransferred == 1 && stamp?.Modified == updated, "An overwrite must synchronize identical content's timestamp.");
        await sftp.DownloadPathAsync(remote, copy, SftpTransferOptions.Default, ct: ct);
        Check(File.GetLastWriteTimeUtc(copy) == updated, "A verified download must correct a stale timestamp.");
        var info = new FileInfo(local);
        var localEntry = new DirectoryEntryMetadata(name, DirectoryEntryKind.File, info.Length, info.LastWriteTimeUtc);
        var remoteEntry = new DirectoryEntryMetadata(name, DirectoryEntryKind.File, stamp!.Size, stamp.Modified);
        Check(DirectoryComparison.Compare([localEntry], [remoteEntry]).Single().Match == DirectoryMatch.SameMetadata,
            "The transferred file must compare equal at server precision.");
    }

    private static async Task CheckMetadataFailureAsync(ISftpService sftp, string local, string route, CancellationToken ct)
    {
        File.SetLastWriteTimeUtc(local, new DateTime(2019, 5, 4, 3, 2, 1, DateTimeKind.Utc));
        foreach (var failure in new[] { "deny", "ignore" })
        {
            var destination = $"/{failure}-{route}.txt";
            try
            {
                await sftp.UploadPathAsync(local, destination, SftpTransferOptions.Default with { Resume = false }, ct: ct);
            }
            catch (InvalidOperationException error) when (error.Message.Contains("modification time", StringComparison.Ordinal))
            {
                continue;
            }
            throw new InvalidOperationException("The transfer must explain rejected or unapplied attributes.");
        }
    }

    private static async Task VerifyLocalForwardAsync(SshNetSession session, CancellationToken ct)
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Server.ExclusiveAddressUse = true;
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var id = await session.AddTunnelAsync(new SshPortForwardingRule
        {
            Type = SshPortForwardingType.Local, BindPort = port, DestinationHost = "127.0.0.1",
            DestinationPort = int.Parse(LoopbackTransferTests.Setting("ECHO_PORT")),
        }, ct);
        try { await session.StartTunnelAsync(id, ct: ct); }
        catch (SocketException) { }
        Check(session.Tunnels.Single(t => t.Id == id).State == TunnelState.Failed && session.State == SessionState.Connected,
            "An occupied port must fail independently of its session.");
        occupied.Stop();
        await session.StartTunnelAsync(id, ct: ct);
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            var stream = client.GetStream();
            var bytes = Encoding.ASCII.GetBytes("ping");
            await stream.WriteAsync(bytes, ct);
            var reply = new byte[bytes.Length];
            await stream.ReadExactlyAsync(reply, ct);
            Check(reply.SequenceEqual(bytes), "Tunnel must carry the actual echo reply.");
        }
        await session.StopTunnelAsync(id, ct);
        Check(session.Tunnels.Single(t => t.Id == id).State == TunnelState.Stopped, "Tunnel must stop.");
        using var reclaimed = new TcpListener(IPAddress.Loopback, port);
        reclaimed.Start();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
