using sutty.Core.Models;

namespace sutty.Core.Sessions;

/// <summary>열려 있는 세션 목록을 관리한다. (탭 1개 = 세션 1개)</summary>
public sealed class SessionManager
{
    private readonly List<ISshSession> _sessions = [];

    public IReadOnlyList<ISshSession> Sessions => _sessions;

    /// <summary>세션을 만들어 목록에 추가한다. 연결은 호출자가 ConnectAsync로 시작.</summary>
    public ISshSession Create(SshConnectionInfo info)
    {
        ISshSession session = new SshNetSession(info);
        _sessions.Add(session);
        return session;
    }

    /// <summary>세션을 안전하게 끊고 목록에서 제거한다.</summary>
    public async Task CloseAsync(ISshSession session)
    {
        await session.DisconnectAsync().ConfigureAwait(false);
        _sessions.Remove(session);
    }
}
