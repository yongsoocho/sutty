namespace sutty.Core.Sessions;

public enum SessionState
{
    /// <summary>생성만 되고 아직 연결 시도 전.</summary>
    Idle,
    Connecting,
    Connected,
    Disconnecting,
    Disconnected,
    Failed,
}
