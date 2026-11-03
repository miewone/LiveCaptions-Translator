using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace LiveCaptionsTranslator.auth;

/// <summary>Stores credentials atomically under LocalAppData, protected by Windows DPAPI CurrentUser.</summary>
internal sealed class WindowsOAuthStore : IOAuthStore
{
    private readonly string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveCaptionsTranslator", "ChatGptOAuth");
    private string VaultPath => Path.Combine(directory, "accounts.dpapi");

    /// <summary>Serializes rotating-token transactions across app instances. Cancellation bounds lock contention.</summary>
    public async Task<IDisposable> LockAsync(CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(directory, "vault.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, token); }
        }
    }

    /// <summary>Reads and decrypts the vault. Corruption fails closed rather than overwriting saved accounts.</summary>
    public OAuthVault Load()
    {
        if (!File.Exists(VaultPath)) return new OAuthVault();
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(VaultPath), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<OAuthVault>(clear) ?? throw new OAuthException("The credential vault is invalid."); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    /// <summary>Encrypts before writing, flushes, then atomically replaces the previous vault on the same volume.</summary>
    public void Save(OAuthVault vault)
    {
        Directory.CreateDirectory(directory);
        var clear = JsonSerializer.SerializeToUtf8Bytes(vault);
        var temp = VaultPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(encrypted);
                file.Flush(true);
            }
            File.Move(temp, VaultPath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
