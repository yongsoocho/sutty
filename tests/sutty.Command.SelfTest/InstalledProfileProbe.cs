using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using sutty.Command;
using sutty.Core.Security;

internal static class InstalledProfileProbe
{
    private sealed record Receipt(string HostId, string CredentialId, string Digest);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder family);

    internal static void Run(string phase, string expectedFamily, string receiptPath, string outcomePath)
    {
        var status = "pass";
        try
        {
            var family = new StringBuilder(256);
            var length = (uint)family.Capacity;
            if (!OperatingSystem.IsWindows() || GetCurrentPackageFamilyName(ref length, family) != 0 ||
                family.ToString() != expectedFamily)
                throw new InvalidOperationException("Missing test package identity.");

            using var vault = new LocalCredentialVault();
            if (phase == "seed")
            {
                if (File.Exists(receiptPath) || HostProfileStore.GetAll().Count != 0 || vault.GetMetadata().Count != 0)
                    throw new InvalidOperationException("The test needs an empty profile.");
                var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                var credentialId = vault.Store(new CredentialSecret(password));
                var host = HostProfileStore.Save(new HostProfileDraft
                {
                    DisplayName = "MSIX retention fixture", Host = "retention.invalid", Username = "fixture",
                    CredentialId = credentialId,
                });
                File.WriteAllText(receiptPath, JsonSerializer.Serialize(new Receipt(host.Id, credentialId, Digest(password))));
            }
            else if (phase is "upgrade" or "rollback")
            {
                var expected = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(receiptPath))
                    ?? throw new InvalidDataException("Missing fixture receipt.");
                var host = HostProfileStore.GetById(expected.HostId);
                if (host?.Host != "retention.invalid" || host.Username != "fixture" || host.CredentialId != expected.CredentialId ||
                    !vault.TryRead(expected.CredentialId, out var credential) || credential is null ||
                    Digest(credential.Password) != expected.Digest)
                    throw new InvalidDataException("The original host and credential must remain usable.");
                if (phase == "rollback")
                {
                    HostProfileStore.Delete(host.Id);
                    vault.Delete(expected.CredentialId);
                }
            }
            else throw new ArgumentException("Unknown test phase.");
        }
        catch (Exception error)
        {
            // The runner needs a result category only. Never persist store contents or a credential.
            status = error.GetType().Name;
            Environment.ExitCode = 1;
        }
        var temporary = outcomePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { status, pid = Environment.ProcessId }));
        File.Move(temporary, outcomePath);
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
