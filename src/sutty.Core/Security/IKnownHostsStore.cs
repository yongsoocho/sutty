namespace sutty.Core.Security;

public interface IKnownHostsStore
{
    string StorePath { get; }

    KnownHostRecord? Find(HostEndpointIdentity endpoint);

    /// <summary>
    /// Saves a previously unknown key. An existing different key always throws
    /// <see cref="HostKeyChangedException"/> and is never replaced implicitly.
    /// </summary>
    KnownHostRecord Trust(HostEndpointIdentity endpoint, HostKeyData key);

    IReadOnlyList<KnownHostRecord> GetAll();
}
