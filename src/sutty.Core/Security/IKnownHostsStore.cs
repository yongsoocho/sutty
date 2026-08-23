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

    /// <summary>
    /// Records that a persisted key completed a trusted handshake. A different key
    /// always fails closed and is never written through this API.
    /// </summary>
    KnownHostRecord MarkUsed(
        HostEndpointIdentity endpoint,
        HostKeyData key,
        DateTimeOffset? usedAtUtc = null);

    /// <summary>
    /// Replaces a persisted key only when the caller supplies the exact currently
    /// trusted key and a non-empty user-entered reason.
    /// </summary>
    KnownHostRecord Rotate(
        HostEndpointIdentity endpoint,
        HostKeyData expectedTrustedKey,
        HostKeyData replacementKey,
        string reason);

    /// <summary>
    /// Removes one explicitly selected persisted endpoint only when it still has the
    /// exact key shown to the user.
    /// </summary>
    bool Remove(HostEndpointIdentity endpoint, HostKeyData expectedTrustedKey);

    /// <summary>
    /// Returns host records and newest-first activity from one atomic store read.
    /// </summary>
    KnownHostsSnapshot GetSnapshot(int activityLimit = 100);

    IReadOnlyList<KnownHostRecord> GetAll();

    /// <summary>Returns newest-first local trust-management activity.</summary>
    IReadOnlyList<KnownHostActivityRecord> GetActivity(int limit = 100);
}
