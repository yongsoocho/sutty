using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace sutty.Core.Routing;

/// <summary>
/// Adapts an OpenSSH-style ProxyCommand's stdin/stdout byte stream to a loopback TCP
/// endpoint that SSH.NET can consume. One process is created per SSH/SFTP connection.
/// </summary>
internal sealed class ProxyCommandBridge : IAsyncDisposable
{
    private readonly string _command;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private readonly Task _acceptLoop;
    private int _nextConnectionId;

    public int Port { get; }
    public string? LastError { get; private set; }

    public ProxyCommandBridge(string command, string targetHost, int targetPort, string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        _command = command
            .Replace("%h", targetHost, StringComparison.Ordinal)
            .Replace("%p", targetPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace("%r", username, StringComparison.Ordinal);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(backlog: 8);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_lifetime.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                var id = Interlocked.Increment(ref _nextConnectionId);
                var task = BridgeConnectionAsync(client, ct);
                _connections[id] = task;
                _ = task.ContinueWith(
                    completedTask => _connections.TryRemove(id, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
        }
        catch (SocketException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            LastError = error.Message;
        }
    }

    private async Task BridgeConnectionAsync(TcpClient tcpClient, CancellationToken lifetimeToken)
    {
        using var ownedClient = tcpClient;
        using var process = CreateProcess();
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("ProxyCommand process could not be started.");

            var network = tcpClient.GetStream();
            var stderrTask = ReadErrorAsync(process, connectionCts.Token);
            var socketToProcess = PumpSocketToProcessAsync(
                network,
                process.StandardInput.BaseStream,
                connectionCts.Token);
            var processToSocket = process.StandardOutput.BaseStream.CopyToAsync(
                network,
                connectionCts.Token);
            var exited = process.WaitForExitAsync(connectionCts.Token);

            await Task.WhenAny(socketToProcess, processToSocket, exited).ConfigureAwait(false);
            connectionCts.Cancel();
            tcpClient.Close();

            try { await Task.WhenAll(socketToProcess, processToSocket).ConfigureAwait(false); }
            catch (OperationCanceledException) when (connectionCts.IsCancellationRequested) { }

            TryKill(process);
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException) { }

            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                LastError = stderr.Trim();
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            LastError = error.Message;
            try { tcpClient.Close(); } catch { }
            TryKill(process);
        }
    }

    private Process CreateProcess()
    {
        var commandProcessor = Environment.GetEnvironmentVariable("COMSPEC");
        if (string.IsNullOrWhiteSpace(commandProcessor))
            commandProcessor = "cmd.exe";
        var startInfo = new ProcessStartInfo
        {
            FileName = commandProcessor,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(_command);
        return new Process { StartInfo = startInfo };
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (process.Id != 0 && !process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (NotSupportedException) { }
    }

    private static async Task PumpSocketToProcessAsync(
        Stream network,
        Stream processInput,
        CancellationToken ct)
    {
        try
        {
            await network.CopyToAsync(processInput, ct).ConfigureAwait(false);
            await processInput.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            try { processInput.Close(); } catch { }
        }
    }

    private static async Task<string> ReadErrorAsync(Process process, CancellationToken ct)
    {
        try
        {
            var text = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            const int maxLength = 8_192;
            return text.Length <= maxLength ? text : text[^maxLength..];
        }
        catch (OperationCanceledException)
        {
            return "";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested)
            return;
        _lifetime.Cancel();
        _listener.Stop();
        try { await _acceptLoop.ConfigureAwait(false); } catch { }
        var connections = _connections.Values.ToArray();
        if (connections.Length > 0)
        {
            try { await Task.WhenAll(connections).ConfigureAwait(false); } catch { }
        }
        _lifetime.Dispose();
    }
}
