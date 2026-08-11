using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace sutty.Core.Security;

/// <summary>
/// A credential value associated with one saved host profile. The value is serialized
/// only inside an AES-GCM envelope. Callers should keep the returned strings alive for
/// no longer than the authentication attempt requires.
/// </summary>
public sealed record CredentialSecret(string Password = "", string PrivateKeyPassphrase = "")
{
    public bool IsEmpty => string.IsNullOrEmpty(Password) && string.IsNullOrEmpty(PrivateKeyPassphrase);
}

public sealed record CredentialVaultMetadata(string Id, DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Local, account-scoped credential storage for Windows.
///
/// A random AES-256 master key is protected by Windows DPAPI for the current user.
/// Individual credential records are encrypted with AES-GCM and authenticated with the
/// record id as associated data. Neither passwords nor private-key passphrases are
/// written to SQLite, settings.json, logs, or an unencrypted file.
/// </summary>
public sealed class LocalCredentialVault : IDisposable
{
    private const int DocumentVersion = 1;
    private const int MasterKeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MaxSecretCharacters = 16 * 1024;
    private const int MaxRecords = 10_000;
    private const int MaxVaultDocumentBytes = 16 * 1024 * 1024;
    private const int MaxEncodedCiphertextCharacters = 768 * 1024;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _keyPath;
    private readonly string _vaultPath;
    private byte[]? _masterKey;
    private CredentialVaultDocument? _document;
    private bool _disposed;

    public LocalCredentialVault(string? directory = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The local credential vault requires Windows DPAPI.");

        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "sutty");
        _keyPath = Path.Combine(_directory, "vault.key");
        _vaultPath = Path.Combine(_directory, "vault.json");
    }

    public static LocalCredentialVault Default { get; } = new();

    public string Store(CredentialSecret secret, string? existingId = null)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ThrowIfDisposed();
        ValidateSecret(secret);

        if (secret.IsEmpty)
            throw new ArgumentException("At least one credential value is required.", nameof(secret));

        lock (_gate)
        {
            EnsureLoaded();

            var id = NormalizeOrCreateId(existingId);
            var existingIndex = _document!.Entries.FindIndex(item =>
                string.Equals(item.Id, id, StringComparison.Ordinal));
            if (existingIndex < 0 && _document.Entries.Count >= MaxRecords)
                throw new InvalidOperationException($"A maximum of {MaxRecords} credential records is supported.");

            var plaintext = JsonSerializer.SerializeToUtf8Bytes(
                secret,
                SecurityJsonContext.Default.CredentialSecret);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            var associatedData = Encoding.UTF8.GetBytes(id);

            try
            {
                using var aes = new AesGcm(_masterKey!, TagSize);
                aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(associatedData);
            }

            var entry = new CredentialVaultEntry
            {
                Id = id,
                Nonce = Convert.ToBase64String(nonce),
                Ciphertext = Convert.ToBase64String(ciphertext),
                Tag = Convert.ToBase64String(tag),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };

            if (existingIndex >= 0)
                _document.Entries[existingIndex] = entry;
            else
                _document.Entries.Add(entry);

            SaveDocument();
            return id;
        }
    }

    public bool TryRead(string id, out CredentialSecret? secret)
    {
        ThrowIfDisposed();
        secret = null;
        id = NormalizeExistingId(id);

        lock (_gate)
        {
            EnsureLoaded();
            var entry = _document!.Entries.FirstOrDefault(item =>
                string.Equals(item.Id, id, StringComparison.Ordinal));
            if (entry is null)
                return false;

            var nonce = DecodeExact(entry.Nonce, NonceSize, "nonce");
            if (entry.Ciphertext.Length > MaxEncodedCiphertextCharacters)
                throw new InvalidDataException("The credential ciphertext is too large.");
            var ciphertext = Decode(entry.Ciphertext, "ciphertext");
            var tag = DecodeExact(entry.Tag, TagSize, "authentication tag");
            var plaintext = new byte[ciphertext.Length];
            var associatedData = Encoding.UTF8.GetBytes(id);

            try
            {
                using var aes = new AesGcm(_masterKey!, TagSize);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
                secret = JsonSerializer.Deserialize(
                    plaintext,
                    SecurityJsonContext.Default.CredentialSecret)
                    ?? throw new InvalidDataException("The credential record is empty.");
                ValidateSecret(secret);
                return true;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The credential record is invalid.", exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(associatedData);
            }
        }
    }

    public bool Delete(string id)
    {
        ThrowIfDisposed();
        id = NormalizeExistingId(id);

        lock (_gate)
        {
            EnsureLoaded();
            var removed = _document!.Entries.RemoveAll(item =>
                string.Equals(item.Id, id, StringComparison.Ordinal)) > 0;
            if (removed)
                SaveDocument();
            return removed;
        }
    }

    public IReadOnlyList<CredentialVaultMetadata> GetMetadata()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            EnsureLoaded();
            return _document!.Entries
                .Select(entry => new CredentialVaultMetadata(entry.Id, entry.UpdatedAtUtc))
                .OrderByDescending(entry => entry.UpdatedAtUtc)
                .ToArray();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_masterKey is not null)
                CryptographicOperations.ZeroMemory(_masterKey);
            _masterKey = null;
            _document = null;
            _disposed = true;
        }
    }

    private void EnsureLoaded()
    {
        if (_masterKey is not null && _document is not null)
            return;

        Directory.CreateDirectory(_directory);
        _masterKey = LoadOrCreateMasterKey();
        _document = LoadDocument();
    }

    private byte[] LoadOrCreateMasterKey()
    {
        if (File.Exists(_keyPath))
        {
            byte[] protectedKey;
            try
            {
                protectedKey = Convert.FromBase64String(File.ReadAllText(_keyPath, Encoding.UTF8));
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("The credential vault key file is invalid.", exception);
            }

            var key = WindowsDpapi.Unprotect(protectedKey);
            CryptographicOperations.ZeroMemory(protectedKey);
            if (key.Length != MasterKeySize)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new InvalidDataException("The credential vault master key has an invalid length.");
            }
            return key;
        }

        var newKey = RandomNumberGenerator.GetBytes(MasterKeySize);
        var protectedBytes = WindowsDpapi.Protect(newKey);
        try
        {
            WriteTextAtomically(_keyPath, Convert.ToBase64String(protectedBytes));
            return newKey;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(newKey);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private CredentialVaultDocument LoadDocument()
    {
        if (!File.Exists(_vaultPath))
            return new CredentialVaultDocument { Version = DocumentVersion };

        if (new FileInfo(_vaultPath).Length > MaxVaultDocumentBytes)
            throw new InvalidDataException("The credential vault document is too large.");

        try
        {
            var document = JsonSerializer.Deserialize(
                File.ReadAllText(_vaultPath, Encoding.UTF8),
                SecurityJsonContext.Default.CredentialVaultDocument)
                ?? throw new InvalidDataException("The credential vault document is empty.");
            if (document.Version != DocumentVersion)
                throw new InvalidDataException($"Unsupported credential vault version {document.Version}.");
            document.Entries ??= [];
            if (document.Entries.Count > MaxRecords ||
                document.Entries.Any(entry => string.IsNullOrWhiteSpace(entry.Id)) ||
                document.Entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() !=
                document.Entries.Count)
            {
                throw new InvalidDataException("The credential vault contains invalid record identifiers.");
            }
            try
            {
                foreach (var entry in document.Entries)
                    _ = NormalizeExistingId(entry.Id);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The credential vault contains an invalid record identifier.", exception);
            }
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The credential vault document is invalid.", exception);
        }
    }

    private void SaveDocument()
    {
        var json = JsonSerializer.Serialize(
            _document,
            SecurityJsonContext.Default.CredentialVaultDocument);
        WriteTextAtomically(_vaultPath, json);
    }

    private static void WriteTextAtomically(string path, string value)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(value);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                File.Replace(temporaryPath, path, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, path);
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                // Preserve the original write failure.
            }
            throw;
        }
    }

    private static string NormalizeOrCreateId(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? Guid.NewGuid().ToString("N")
            : NormalizeExistingId(id);

    private static string NormalizeExistingId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A credential record id is required.", nameof(id));
        id = id.Trim();
        if (id.Length > 128 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("The credential record id contains unsupported characters.", nameof(id));
        return id;
    }

    private static void ValidateSecret(CredentialSecret secret)
    {
        if (secret.Password.Length > MaxSecretCharacters ||
            secret.PrivateKeyPassphrase.Length > MaxSecretCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(secret), "Credential values are too long.");
        }
    }

    private static byte[] Decode(string value, string field)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"The credential {field} is invalid.", exception);
        }
    }

    private static byte[] DecodeExact(string value, int expectedLength, string field)
    {
        var decoded = Decode(value, field);
        if (decoded.Length == expectedLength)
            return decoded;
        CryptographicOperations.ZeroMemory(decoded);
        throw new InvalidDataException($"The credential {field} has an invalid length.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static class WindowsDpapi
    {
        private const int CryptProtectUiForbidden = 0x1;

        public static byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);
        public static byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

        private static byte[] Transform(byte[] input, bool protect)
        {
            var inputPointer = Marshal.AllocHGlobal(input.Length);
            try
            {
                Marshal.Copy(input, 0, inputPointer, input.Length);
                var inputBlob = new DataBlob { Size = input.Length, Data = inputPointer };
                DataBlob outputBlob;
                IntPtr description = IntPtr.Zero;

                var succeeded = protect
                    ? CryptProtectData(
                        ref inputBlob,
                        "Sutty local credential vault",
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        CryptProtectUiForbidden,
                        out outputBlob)
                    : CryptUnprotectData(
                        ref inputBlob,
                        out description,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        CryptProtectUiForbidden,
                        out outputBlob);

                if (!succeeded)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

                try
                {
                    var output = new byte[outputBlob.Size];
                    Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                    return output;
                }
                finally
                {
                    if (outputBlob.Data != IntPtr.Zero)
                        LocalFree(outputBlob.Data);
                    if (description != IntPtr.Zero)
                        LocalFree(description);
                }
            }
            finally
            {
                if (inputPointer != IntPtr.Zero)
                {
                    for (var offset = 0; offset < input.Length; offset++)
                        Marshal.WriteByte(inputPointer, offset, 0);
                    Marshal.FreeHGlobal(inputPointer);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DataBlob dataIn,
            string? description,
            IntPtr optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            out DataBlob dataOut);

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob dataIn,
            out IntPtr description,
            IntPtr optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
