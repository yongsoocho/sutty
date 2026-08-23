using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace sutty.Core.Security;

public enum HostKeyTrustState
{
    Unknown,
    Trusted,
    Changed,
}

public enum HostKeyDecision
{
    Cancel,
    TrustOnce,
    TrustAndSave,
}

public enum HostKeyTrustSource
{
    None,
    Connection,
    Persistent,
}

public enum KnownHostActivityType
{
    Trusted,
    Rotated,
    Removed,
}

/// <summary>A public SSH host key. This contains no credentials or other secrets.</summary>
public sealed class HostKeyData : IEquatable<HostKeyData>
{
    public const int MaximumRawKeyBytes = 1024 * 1024;

    private readonly byte[] _rawKey;

    private HostKeyData(string algorithm, byte[] rawKey)
    {
        Algorithm = algorithm;
        _rawKey = rawKey;
        Sha256Fingerprint = ComputeSha256Fingerprint(rawKey);
    }

    public string Algorithm { get; }

    /// <summary>OpenSSH-style, non-padded SHA-256 fingerprint including the SHA256: prefix.</summary>
    public string Sha256Fingerprint { get; }

    /// <summary>
    /// A copy of the complete public host-key blob as received from SSH.NET. Returning a
    /// copy prevents callers from mutating the key behind its verified fingerprint.
    /// </summary>
    public ReadOnlyMemory<byte> RawKey => _rawKey.ToArray();

    public static HostKeyData Create(string algorithm, ReadOnlySpan<byte> rawKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);

        algorithm = algorithm.Trim();
        if (algorithm.Length > 128 || algorithm.Any(char.IsControl) || algorithm.Any(char.IsWhiteSpace))
            throw new ArgumentException("Host-key algorithm is invalid.", nameof(algorithm));
        if (rawKey.Length is 0 or > MaximumRawKeyBytes)
            throw new ArgumentOutOfRangeException(nameof(rawKey), "Host key has an invalid size.");

        return new HostKeyData(algorithm, rawKey.ToArray());
    }

    /// <summary>
    /// Creates a key and verifies an independently supplied SHA-256 fingerprint. The
    /// fingerprint may include or omit the standard SHA256: prefix and Base64 padding.
    /// </summary>
    public static HostKeyData CreateVerified(
        string algorithm,
        ReadOnlySpan<byte> rawKey,
        string sha256Fingerprint)
    {
        var key = Create(algorithm, rawKey);
        var normalized = NormalizeSha256Fingerprint(sha256Fingerprint);
        if (!string.Equals(key.Sha256Fingerprint, normalized, StringComparison.Ordinal))
            throw new CryptographicException("The supplied host-key fingerprint does not match the raw key.");
        return key;
    }

    public bool HasSameRawKey(HostKeyData other)
    {
        ArgumentNullException.ThrowIfNull(other);
        // The raw SSH public-key blob is authoritative. RSA signature aliases can expose
        // different negotiated algorithm names while still presenting this exact same key.
        return _rawKey.Length == other._rawKey.Length &&
               CryptographicOperations.FixedTimeEquals(_rawKey, other._rawKey);
    }

    public bool Equals(HostKeyData? other) =>
        other is not null &&
        string.Equals(Algorithm, other.Algorithm, StringComparison.Ordinal) &&
        HasSameRawKey(other);

    public override bool Equals(object? obj) => Equals(obj as HostKeyData);

    public override int GetHashCode() => HashCode.Combine(Algorithm, Sha256Fingerprint);

    public override string ToString() => $"{Algorithm} {Sha256Fingerprint}";

    internal HostKeyData Clone() => Create(Algorithm, _rawKey);

    internal string ToBase64() => Convert.ToBase64String(_rawKey);

    private static string ComputeSha256Fingerprint(ReadOnlySpan<byte> rawKey) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(rawKey)).TrimEnd('=');

    private static string NormalizeSha256Fingerprint(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        var body = fingerprint.Trim();
        if (body.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            body = body[7..];
        body = body.TrimEnd('=');

        try
        {
            var padding = (4 - body.Length % 4) % 4;
            var decoded = Convert.FromBase64String(body + new string('=', padding));
            if (decoded.Length != 32)
                throw new FormatException();
        }
        catch (FormatException ex)
        {
            throw new FormatException("Host-key SHA-256 fingerprint is invalid.", ex);
        }

        return "SHA256:" + body;
    }
}

public sealed record KnownHostRecord(
    HostEndpointIdentity Endpoint,
    HostKeyData Key,
    DateTimeOffset TrustedAtUtc,
    DateTimeOffset LastUsedAtUtc);

/// <summary>
/// Local public-key management activity. It intentionally contains no credentials,
/// private-key material, command text, or terminal data.
/// </summary>
public sealed record KnownHostActivityRecord(
    DateTimeOffset TimestampUtc,
    KnownHostActivityType Type,
    HostEndpointIdentity Endpoint,
    string? PreviousAlgorithm,
    string? PreviousSha256Fingerprint,
    string? CurrentAlgorithm,
    string? CurrentSha256Fingerprint,
    string Reason);

/// <summary>
/// One atomically loaded view of the persisted host keys and their newest-first
/// management activity. Both collections come from the same on-disk document.
/// </summary>
public sealed record KnownHostsSnapshot(
    IReadOnlyList<KnownHostRecord> Hosts,
    IReadOnlyList<KnownHostActivityRecord> Activity);

/// <summary>
/// Result of the deliberate changed-key prompt. Confirmation and a reason are both
/// required before Core can replace a persisted public host key.
/// </summary>
public sealed record HostKeyRotationDecision(bool Confirmed, string Reason)
{
    public static HostKeyRotationDecision Cancelled { get; } = new(false, "");
}

/// <summary>
/// Single normalization and validation policy for user-entered host-key rotation
/// reasons. Hidden formatting cannot make an apparently blank or reordered reason.
/// </summary>
public static class HostKeyRotationReason
{
    public const int MaximumLength = 256;

    public static bool TryNormalize(string? reason, out string normalizedReason)
    {
        try
        {
            normalizedReason = Normalize(reason);
            return true;
        }
        catch (ArgumentException)
        {
            normalizedReason = string.Empty;
            return false;
        }
    }

    public static string Normalize(string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        string compatibilityNormalized;
        try
        {
            compatibilityNormalized = reason.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException error)
        {
            throw new ArgumentException(
                "The host-key rotation reason contains invalid Unicode.",
                nameof(reason),
                error);
        }

        var normalized = new StringBuilder(
            Math.Min(compatibilityNormalized.Length, MaximumLength));
        var pendingSpace = false;
        var hasVisibleBase = false;
        foreach (var rune in compatibilityNormalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                throw new ArgumentException(
                    "The host-key rotation reason contains hidden or directional formatting.",
                    nameof(reason));
            }

            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }

            if (category is not UnicodeCategory.NonSpacingMark and
                not UnicodeCategory.SpacingCombiningMark and
                not UnicodeCategory.EnclosingMark)
            {
                hasVisibleBase = true;
            }

            if (pendingSpace)
            {
                normalized.Append(' ');
                pendingSpace = false;
            }
            normalized.Append(rune.ToString());
            if (normalized.Length > MaximumLength)
            {
                throw new ArgumentException(
                    "The host-key rotation reason is too long.",
                    nameof(reason));
            }
        }

        if (normalized.Length == 0 || !hasVisibleBase)
            throw new ArgumentException("The host-key rotation reason is invalid.", nameof(reason));
        return normalized.ToString();
    }
}

public sealed record HostKeyVerification(
    HostEndpointIdentity Endpoint,
    HostKeyData PresentedKey,
    HostKeyTrustState State,
    HostKeyTrustSource Source,
    HostKeyData? TrustedKey)
{
    public bool CanTrust => State == HostKeyTrustState.Trusted;
}

/// <summary>Raised when an endpoint presents a different key than the trusted key.</summary>
public sealed class HostKeyChangedException : System.Security.SecurityException
{
    public HostKeyChangedException(
        HostEndpointIdentity endpoint,
        HostKeyData trustedKey,
        HostKeyData presentedKey)
        : base($"Host key changed for {endpoint.Value}. Trusted {trustedKey.Algorithm} " +
               $"{trustedKey.Sha256Fingerprint}; presented {presentedKey.Algorithm} " +
               $"{presentedKey.Sha256Fingerprint}.")
    {
        Endpoint = endpoint;
        TrustedKey = trustedKey;
        PresentedKey = presentedKey;
    }

    public HostEndpointIdentity Endpoint { get; }
    public HostKeyData TrustedKey { get; }
    public HostKeyData PresentedKey { get; }
}
