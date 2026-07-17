using sutty.Core.Models;
using sutty.Core.Sftp;

namespace sutty.Core.Sessions;

/// <summary>
/// 실제 네트워크 없이 연결/해제 흐름만 흉내 내는 세션.
/// UI(탭, 상태 표시, 파일 트리)를 먼저 완성하기 위한 스텁.
/// </summary>
public sealed class MockSshSession : ISshSession
{
    public Guid Id { get; } = Guid.NewGuid();
    public SshConnectionInfo Info { get; }
    public SessionState State { get; private set; } = SessionState.Idle;
    public string? LastError => null;
    public ISftpService Sftp { get; }

    public event EventHandler<SessionState>? StateChanged;

    public MockSshSession(SshConnectionInfo info)
    {
        Info = info;
        Sftp = new MockSftpService(info.Username);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (State is SessionState.Connecting or SessionState.Connected)
            return;

        SetState(SessionState.Connecting);
        await Task.Delay(700, ct); // 핸드셰이크 + 인증 흉내
        SetState(SessionState.Connected);
    }

    public async Task<string> RunCommandAsync(string command, CancellationToken ct = default)
    {
        await Task.Delay(150, ct); // 왕복 흉내

        var user = string.IsNullOrWhiteSpace(Info.Username) ? "admin" : Info.Username;

        // "cd <cwd> 2>/dev/null; cd <target> && pwd" — cd 흐름 흉내 (경로 추적용)
        if (command.Contains("&& pwd"))
        {
            var idx = command.LastIndexOf("cd ", StringComparison.Ordinal);
            var target = command[(idx + 3)..].Replace("&& pwd", "").Trim();
            return target switch
            {
                "~" or "" => $"/home/{user}",
                _ when target.StartsWith('/') => target.TrimEnd('/'),
                _ => $"/home/{user}/{target.TrimEnd('/')}",
            };
        }

        if (command.Contains("uname"))
            return "Welcome to Ubuntu 22.04.4 LTS (GNU/Linux 5.15.0-105-generic x86_64)\n * Documentation: https://help.ubuntu.com";

        // 앞에 붙는 "cd xxx 2>/dev/null; " 프리픽스는 떼고 사용자가 친 명령만 보여준다
        var semi = command.IndexOf("; ", StringComparison.Ordinal);
        var userCommand = semi >= 0 ? command[(semi + 2)..] : command;
        return $"mock: '{userCommand}' executed.";
    }

    public async Task DisconnectAsync()
    {
        if (State is SessionState.Idle or SessionState.Disconnected or SessionState.Disconnecting)
            return;

        SetState(SessionState.Disconnecting);
        await Task.Delay(250); // 채널 정리 흉내
        SetState(SessionState.Disconnected);
    }

    private void SetState(SessionState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }
}
