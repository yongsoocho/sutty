using Renci.SshNet.Common;

namespace sutty.Core.Security;

/// <summary>
/// Fail-closed adapter for SSH.NET HostKeyReceived events. Attach
/// <see cref="HandleHostKeyReceived"/> before Connect/ConnectAsync on both SshClient and
/// SftpClient, and keep it attached for each client's full lifetime so rekeys are checked.
/// </summary>
public sealed class SshNetHostKeyVerifier
{
    private readonly object _observationGate = new();
    private readonly HostEndpointIdentity _endpoint;
    private readonly HostKeyTrustContext _trustContext;
    private HostKeyVerification? _lastVerification;
    private Exception? _lastError;

    public SshNetHostKeyVerifier(
        HostEndpointIdentity endpoint,
        HostKeyTrustContext trustContext)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _trustContext = trustContext ?? throw new ArgumentNullException(nameof(trustContext));
    }

    public HostKeyVerification? LastVerification
    {
        get
        {
            lock (_observationGate)
                return _lastVerification;
        }
    }

    public Exception? LastError
    {
        get
        {
            lock (_observationGate)
                return _lastError;
        }
    }

    /// <summary>
    /// SSH.NET invokes this synchronously during key exchange. It never waits for UI:
    /// unknown and changed keys are rejected, then the caller can inspect LastVerification,
    /// collect a decision for Unknown, apply it to the context, and retry with a new client.
    /// </summary>
    public void HandleHostKeyReceived(object? sender, HostKeyEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // SSH.NET defaults must never decide trust for Sutty.
        args.CanTrust = false;

        try
        {
            var key = HostKeyData.CreateVerified(
                args.HostKeyName,
                args.HostKey,
                args.FingerPrintSHA256);
            var verification = _trustContext.Evaluate(_endpoint, key);

            lock (_observationGate)
            {
                _lastVerification = verification;
                _lastError = null;
            }

            args.CanTrust = verification.State == HostKeyTrustState.Trusted;
        }
        catch (Exception ex)
        {
            // Corrupt storage, malformed event data, and I/O failures all deny the key.
            lock (_observationGate)
            {
                _lastVerification = null;
                _lastError = ex;
            }
            args.CanTrust = false;
        }
    }

    public bool ApplyLastDecision(HostKeyDecision decision)
    {
        HostKeyVerification verification;
        lock (_observationGate)
        {
            verification = _lastVerification
                ?? throw new InvalidOperationException("No host-key verification is available.");
        }

        return _trustContext.ApplyDecision(
            verification.Endpoint,
            verification.PresentedKey,
            decision);
    }

    public bool ApplyLastRotation(HostKeyRotationDecision decision)
    {
        HostKeyVerification verification;
        lock (_observationGate)
        {
            verification = _lastVerification
                ?? throw new InvalidOperationException("No host-key verification is available.");
        }

        return _trustContext.ApplyRotation(verification, decision);
    }

    /// <summary>
    /// Called only after the client handshake has completed so management UI can show
    /// the last time an exact persisted key was successfully used.
    /// </summary>
    public void MarkLastKeyUsed()
    {
        HostKeyVerification? verification;
        lock (_observationGate)
            verification = _lastVerification;

        if (verification is { State: HostKeyTrustState.Trusted })
        {
            _trustContext.MarkPersistentKeyUsed(
                verification.Endpoint,
                verification.PresentedKey);
        }
    }

    public void ResetObservation()
    {
        lock (_observationGate)
        {
            _lastVerification = null;
            _lastError = null;
        }
    }
}
