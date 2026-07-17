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
