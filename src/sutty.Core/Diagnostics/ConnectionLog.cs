using Renci.SshNet.Common;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace sutty.Core.Diagnostics;

public enum ConnectionLogSeverity
{
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

/// <summary>A sanitized SSH/SFTP diagnostic entry kept for the current app run.</summary>
public sealed record ConnectionLogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    Guid SessionId,
    string SessionTitle,
    string Endpoint,
    ConnectionLogSeverity Severity,
    string Category,
    string MessageKo,
    string MessageEn,
    string? Detail);

/// <summary>
/// Thread-safe bounded connection diagnostics. Verbose protocol logging is deliberately
/// kept in memory: it is available immediately after a failure without silently leaving
/// sensitive infrastructure metadata on disk.
/// </summary>
public static class ConnectionLogStore
{
    private const int MaxEntries = 5_000;
    private static readonly object Gate = new();
    private static readonly LinkedList<ConnectionLogEntry> Entries = [];
    private static long _nextSequence;

    public static event Action<ConnectionLogEntry>? EntryAdded;

    public static IReadOnlyList<ConnectionLogEntry> Snapshot()
    {
        lock (Gate)
            return Entries.ToArray();
    }

    public static void Append(
        Guid sessionId,
        string sessionTitle,
        string endpoint,
        ConnectionLogSeverity severity,
        string category,
        string messageKo,
        string messageEn,
        string? detail = null)
    {
        ConnectionLogEntry entry;
        lock (Gate)
        {
            entry = new ConnectionLogEntry(
                ++_nextSequence,
                DateTimeOffset.UtcNow,
                sessionId,
                ConnectionLogSanitizer.Clean(sessionTitle, 256),
                ConnectionLogSanitizer.Clean(endpoint, 512),
                severity,
                ConnectionLogSanitizer.Clean(category, 256),
                ConnectionLogSanitizer.Clean(messageKo, 16_384),
                ConnectionLogSanitizer.Clean(messageEn, 16_384),
                string.IsNullOrWhiteSpace(detail)
                    ? null
                    : ConnectionLogSanitizer.Clean(detail, 65_536));
            Entries.AddLast(entry);
            while (Entries.Count > MaxEntries)
                Entries.RemoveFirst();
        }

        // Diagnostics must never change connection behaviour. A UI subscriber that is
        // closing or otherwise faulty cannot be allowed to fail the SSH operation.
        if (EntryAdded is { } handlers)
        {
            foreach (Action<ConnectionLogEntry> handler in handlers.GetInvocationList())
            {
                try { handler(entry); }
                catch { /* isolate diagnostic observers */ }
            }
        }
    }

    public static void Clear()
    {
        lock (Gate)
            Entries.Clear();
    }
}

internal static class ConnectionFailureDetails
{
    public static (string Korean, string English) Summarize(Exception error)
    {
        if (Find<SocketException>(error) is { } socket)
        {
            return socket.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData =>
                    ("호스트 이름을 찾지 못했습니다. 주소와 DNS 설정을 확인하세요.",
                     "The host name could not be resolved. Check the address and DNS settings."),
                SocketError.ConnectionRefused =>
                    ("서버가 연결을 거부했습니다. SSH 서비스와 포트를 확인하세요.",
                     "The server refused the connection. Check the SSH service and port."),
                SocketError.TimedOut =>
                    ("연결 시간이 초과되었습니다. 서버 상태, 방화벽 및 네트워크 경로를 확인하세요.",
                     "The connection timed out. Check the server, firewall, and network route."),
                SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                    ("서버로 가는 네트워크 경로에 도달할 수 없습니다.",
                     "The network route to the server is unreachable."),
                _ =>
                    ($"네트워크 소켓 오류가 발생했습니다 ({socket.SocketErrorCode}).",
                     $"A network socket error occurred ({socket.SocketErrorCode})."),
            };
        }

        if (Find<SshAuthenticationException>(error) is not null)
        {
            return ("SSH 인증에 실패했습니다. 사용자 이름, 비밀번호, 개인키 및 서버 인증 정책을 확인하세요.",
                    "SSH authentication failed. Check the username, password, private key, and server authentication policy.");
        }

        if (Find<ProxyException>(error) is not null)
        {
            return ("프록시 연결 또는 프록시 인증에 실패했습니다.",
                    "The proxy connection or proxy authentication failed.");
        }

        if (Find<SecurityException>(error) is not null)
        {
            return ("호스트 키 검증에 실패했거나 서버 키가 신뢰되지 않았습니다.",
                    "Host-key verification failed or the server key was not trusted.");
        }

        if (Find<FileNotFoundException>(error) is not null)
        {
            return ("SSH 인증에 사용할 개인키 파일을 찾지 못했습니다.",
                    "The private-key file required for SSH authentication was not found.");
        }

        if (Find<UnauthorizedAccessException>(error) is not null)
        {
            return ("개인키 또는 연결 관련 파일을 읽을 권한이 없습니다.",
                    "Permission was denied while reading a private key or connection file.");
        }

        if (Find<SshConnectionException>(error) is { } connection)
        {
            return ($"SSH 핸드셰이크가 중단되었습니다 ({connection.DisconnectReason}).",
                    $"The SSH handshake was terminated ({connection.DisconnectReason}).");
        }

        if (Find<TimeoutException>(error) is not null)
        {
            return ("SSH 연결 단계의 응답 시간이 초과되었습니다.",
                    "An SSH connection stage timed out.");
        }

        return ("SSH 연결을 완료하지 못했습니다. 아래 예외 체인과 상세 로그를 확인하세요.",
                "The SSH connection could not be completed. Review the exception chain and verbose log below.");
    }

    public static string Format(Exception error)
    {
        var output = new StringBuilder();
        var current = error;
        for (var depth = 0; current is not null && depth < 8; depth++)
        {
            if (depth > 0)
                output.AppendLine("caused by:");

            output.Append(current.GetType().FullName)
                .Append(": ")
                .AppendLine(current.Message);

            if (current is SocketException socket)
            {
                output.Append("socket-error: ")
                    .Append(socket.SocketErrorCode)
                    .Append("; native-error: ")
                    .AppendLine(socket.NativeErrorCode.ToString());
            }
            else if (current is SshConnectionException connection)
            {
                output.Append("disconnect-reason: ")
                    .AppendLine(connection.DisconnectReason.ToString());
            }

            output.Append("hresult: 0x")
                .AppendLine(current.HResult.ToString("X8"));
            current = current.InnerException;
        }

        return output.ToString().TrimEnd();
    }

    private static TException? Find<TException>(Exception? error)
        where TException : Exception
    {
        while (error is not null)
        {
            if (error is TException match)
                return match;
            error = error.InnerException;
        }

        return null;
    }
}

internal static class ConnectionLogSanitizer
{
    private static readonly Regex SecretValue = new(
        "\\b(password|passphrase|secret|token)\\b(\\s*[:=]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UriUserInfo = new(
        @"(?<=://)[^/@\s]+@",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var sanitized = SecretValue.Replace(value, "$1$2***");
        sanitized = UriUserInfo.Replace(sanitized, "***@");
        if (sanitized.Length <= maxLength)
            return sanitized;
        return sanitized[..maxLength] + "…";
    }
}
