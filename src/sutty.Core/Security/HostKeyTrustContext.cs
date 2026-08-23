namespace sutty.Core.Security;

/// <summary>
/// Trust state shared by every SSH transport in one logical connection. Create one
/// context for the SSH client and its SFTP client; discard it when that connection ends.
/// </summary>
public sealed class HostKeyTrustContext
{
    private readonly object _gate = new();
    private readonly IKnownHostsStore _knownHosts;
    private readonly Dictionary<string, HostKeyData> _trustedForConnection =
        new(StringComparer.Ordinal);

    public HostKeyTrustContext(IKnownHostsStore? knownHosts = null)
    {
        _knownHosts = knownHosts ?? KnownHostsStore.Default;
    }

    public HostKeyVerification Evaluate(HostEndpointIdentity endpoint, HostKeyData presentedKey)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(presentedKey);

        lock (_gate)
            return EvaluateCore(endpoint, presentedKey);
    }

    /// <summary>
    /// Applies a user decision to an unknown key. Changed keys always throw and cannot
    /// be trusted or persisted through this API.
    /// </summary>
    public bool ApplyDecision(
        HostEndpointIdentity endpoint,
        HostKeyData presentedKey,
        HostKeyDecision decision)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(presentedKey);
        if (!Enum.IsDefined(decision))
            throw new ArgumentOutOfRangeException(nameof(decision));

        lock (_gate)
        {
            if (decision == HostKeyDecision.Cancel)
                return false;

            var current = EvaluateCore(endpoint, presentedKey);
            if (current.State == HostKeyTrustState.Changed)
                throw new HostKeyChangedException(endpoint, current.TrustedKey!, presentedKey);

            if (decision == HostKeyDecision.TrustAndSave)
            {
                _knownHosts.Trust(endpoint, presentedKey);
                return true;
            }

            if (current.State == HostKeyTrustState.Trusted)
                return true;

            _trustedForConnection[endpoint.Value] = presentedKey.Clone();
            return true;
        }
    }

    /// <summary>
    /// Applies a deliberate changed-key rotation. The persisted key must still match
    /// the key that was shown to the user, and both explicit confirmation and a reason
    /// are required. Cancellation leaves the store untouched.
    /// </summary>
    public bool ApplyRotation(
        HostKeyVerification verification,
        HostKeyRotationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(decision);

        lock (_gate)
        {
            if (!decision.Confirmed)
                return false;

            if (verification is not
                {
                    State: HostKeyTrustState.Changed,
                    Source: HostKeyTrustSource.Persistent,
                    TrustedKey: not null,
                })
            {
                throw new InvalidOperationException(
                    "Host-key rotation requires a changed persisted key.");
            }

            _knownHosts.Rotate(
                verification.Endpoint,
                verification.TrustedKey,
                verification.PresentedKey,
                decision.Reason);
            return true;
        }
    }

    /// <summary>Updates last-used time only for an exact persisted-key match.</summary>
    public void MarkPersistentKeyUsed(
        HostEndpointIdentity endpoint,
        HostKeyData presentedKey)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(presentedKey);

        lock (_gate)
        {
            var current = EvaluateCore(endpoint, presentedKey);
            if (current is { State: HostKeyTrustState.Trusted, Source: HostKeyTrustSource.Persistent })
                _knownHosts.MarkUsed(endpoint, presentedKey);
        }
    }

    private HostKeyVerification EvaluateCore(
        HostEndpointIdentity endpoint,
        HostKeyData presentedKey)
    {
        var persisted = _knownHosts.Find(endpoint);
        if (persisted is not null)
        {
            var state = persisted.Key.Equals(presentedKey)
                ? HostKeyTrustState.Trusted
                : HostKeyTrustState.Changed;
            return new HostKeyVerification(
                endpoint,
                presentedKey,
                state,
                HostKeyTrustSource.Persistent,
                persisted.Key);
        }

        if (_trustedForConnection.TryGetValue(endpoint.Value, out var trustedOnce))
        {
            var state = trustedOnce.Equals(presentedKey)
                ? HostKeyTrustState.Trusted
                : HostKeyTrustState.Changed;
            return new HostKeyVerification(
                endpoint,
                presentedKey,
                state,
                HostKeyTrustSource.Connection,
                trustedOnce);
        }

        return new HostKeyVerification(
            endpoint,
            presentedKey,
            HostKeyTrustState.Unknown,
            HostKeyTrustSource.None,
            null);
    }
}
