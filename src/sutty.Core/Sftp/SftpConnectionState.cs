namespace sutty.Core.Sftp;

/// <summary>
/// Availability of the optional SFTP subsystem for an SSH session.
/// SSH can remain connected when this subsystem is unavailable.
/// </summary>
public enum SftpConnectionState
{
    NotConnected,
    Connecting,
    Ready,
    Unavailable,
}
