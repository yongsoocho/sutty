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
            var changed = Path.Combine(scratch, $"other-{route}.txt");
            await File.WriteAllTextAsync(changed, "other", ct);
            await session.Sftp.UploadPathAsync(changed, path,
                SftpTransferOptions.Default with { ConflictPolicy = SftpConflictPolicy.Overwrite }, ct: ct);
            Check(await edit.HasChangesAsync(ct) && edit.HasRemoteContentConflict(await edit.ReadRemoteVersionAsync(session.Sftp, ct)),
                "A conflicting remote edit must require review and retain the working copy.");
        }
        finally { await session.DisconnectAsync(); }
        Check(await File.ReadAllTextAsync(workingCopy, ct) == "local edit", "The working copy must survive disconnect.");
        Console.WriteLine($"Operator checks via {route} passed: authentication, SFTP edit conflict/recovery, tunnel collision/restart/stop.");
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
