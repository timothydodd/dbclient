using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace dbclient.Services;

/// <summary>
/// Encrypts stored credentials at rest.
/// <list type="bullet">
/// <item><c>dpapi:</c> + base64 — Windows DPAPI (CurrentUser scope) with app-specific entropy.</item>
/// <item><c>aesgcm:</c> + base64(nonce[12] | tag[16] | ciphertext) — AES-256-GCM, key from PBKDF2-SHA256
///   (600k iterations) over machine-id + username with a per-install random salt in ~/.dbclient/.salt.</item>
/// <item><c>ENC:</c> + base64(iv[16] | ciphertext) — legacy AES-CBC format, decrypt-only for migration.</item>
/// </list>
/// Encrypt throws on failure (plaintext is never written). Decrypt returns "" on failure.
/// </summary>
public static class CredentialProtector
{
    private const string LegacyPrefix = "ENC:";
    private const string DpapiPrefix = "dpapi:";
    private const string AesGcmPrefix = "aesgcm:";

    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("dbclient-credential-v2");
    private static readonly object SaltLock = new();
    private static byte[]? _cachedKey;

    public static bool IsEncrypted(string? value) =>
        value != null && (value.StartsWith(DpapiPrefix, StringComparison.Ordinal)
                          || value.StartsWith(AesGcmPrefix, StringComparison.Ordinal)
                          || value.StartsWith(LegacyPrefix, StringComparison.Ordinal));

    /// <summary>Encrypts <paramref name="plaintext"/>. Throws on failure — never returns plaintext.</summary>
    public static string Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        if (OperatingSystem.IsWindows())
            return DpapiPrefix + Convert.ToBase64String(DpapiProtect(plainBytes));

        var key = GetKey();
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);   // 12
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];                              // 16
        var cipher = new byte[plainBytes.Length];
        using (var gcm = new AesGcm(key, tag.Length))
            gcm.Encrypt(nonce, plainBytes, cipher, tag);

        var blob = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, nonce.Length);
        cipher.CopyTo(blob, nonce.Length + tag.Length);
        return AesGcmPrefix + Convert.ToBase64String(blob);
    }

    /// <summary>
    /// Decrypts a stored value. Returns "" on failure (logged) — never the raw ciphertext.
    /// Unprefixed values are treated as legacy plaintext and returned as-is.
    /// </summary>
    public static string Decrypt(string? stored) => TryDecrypt(stored, out var plain) ? plain : "";

    /// <summary>
    /// Attempts to decrypt. Returns false (and <paramref name="plain"/> = "") if the value was encrypted
    /// but could not be decrypted — the UI should mark the password as needing re-entry.
    /// </summary>
    public static bool TryDecrypt(string? stored, out string plain)
    {
        plain = "";
        if (string.IsNullOrEmpty(stored)) return true;

        // Not encrypted — legacy plaintext (migration path)
        if (!IsEncrypted(stored))
        {
            plain = stored;
            return true;
        }

        try
        {
            if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("DPAPI-protected credential cannot be read on this OS");
                var data = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
                plain = Encoding.UTF8.GetString(DpapiUnprotect(data));
                return true;
            }

            if (stored.StartsWith(AesGcmPrefix, StringComparison.Ordinal))
            {
                var blob = Convert.FromBase64String(stored[AesGcmPrefix.Length..]);
                const int nonceLen = 12, tagLen = 16;
                if (blob.Length < nonceLen + tagLen) throw new CryptographicException("Ciphertext too short");
                var nonce = blob.AsSpan(0, nonceLen);
                var tag = blob.AsSpan(nonceLen, tagLen);
                var cipher = blob.AsSpan(nonceLen + tagLen);
                var plainBytes = new byte[cipher.Length];
                using (var gcm = new AesGcm(GetKey(), tagLen))
                    gcm.Decrypt(nonce, cipher, tag, plainBytes);
                plain = Encoding.UTF8.GetString(plainBytes);
                return true;
            }

            // Legacy ENC: AES-CBC, IV prepended, key = PBKDF2(machine-id, static salt, 100k)
            {
                var data = Convert.FromBase64String(stored[LegacyPrefix.Length..]);
                using var aes = Aes.Create();
                aes.Key = Rfc2898DeriveBytes.Pbkdf2(GetMachineId(),
                    Encoding.UTF8.GetBytes("dbclient-credential-salt"), 100_000, HashAlgorithmName.SHA256, 32);
                aes.IV = data[..16];
                using var decryptor = aes.CreateDecryptor();
                var plainBytes = decryptor.TransformFinalBlock(data, 16, data.Length - 16);
                plain = Encoding.UTF8.GetString(plainBytes);
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Credential decryption failed; password will need re-entry: {ex.GetType().Name}: {ex.Message}");
            plain = "";
            return false;
        }
    }

    // ---- Windows DPAPI --------------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static byte[] DpapiProtect(byte[] data)
        => ProtectedData.Protect(data, DpapiEntropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] DpapiUnprotect(byte[] data)
        => ProtectedData.Unprotect(data, DpapiEntropy, DataProtectionScope.CurrentUser);

    // ---- Non-Windows key derivation -------------------------------------------------------------

    private static byte[] GetKey()
    {
        if (_cachedKey != null) return _cachedKey;
        lock (SaltLock)
        {
            if (_cachedKey != null) return _cachedKey;
            var salt = LoadOrCreateSalt();
            var material = new List<byte>();
            material.AddRange(GetMachineId());
            material.Add(0);
            material.AddRange(Encoding.UTF8.GetBytes(Environment.UserName));
            _cachedKey = Rfc2898DeriveBytes.Pbkdf2(material.ToArray(), salt, 600_000, HashAlgorithmName.SHA256, 32);
            return _cachedKey;
        }
    }

    private static byte[] LoadOrCreateSalt()
    {
        var path = AppPaths.SaltFile;
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length == 32) return existing;
            AppLogger.Warn("Credential salt file has unexpected length; regenerating (stored passwords will need re-entry)");
        }

        var salt = RandomNumberGenerator.GetBytes(32);
        AppPaths.WriteBytesAtomic(path, salt);
        AppLogger.Info("Created new credential salt");
        return salt;
    }

    private static byte[] GetMachineId()
    {
        // Linux machine-id
        try
        {
            const string path = "/etc/machine-id";
            if (File.Exists(path))
                return Encoding.UTF8.GetBytes(File.ReadAllText(path).Trim());
        }
        catch { }

        // macOS
        try
        {
            const string path = "/var/db/SystemKey";
            if (File.Exists(path))
                return File.ReadAllBytes(path);
        }
        catch { }

        // Windows machine GUID from registry
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var guid = GetWindowsMachineGuid();
                if (!string.IsNullOrEmpty(guid))
                    return Encoding.UTF8.GetBytes(guid);
            }
        }
        catch { }

        // Last resort: hostname + username
        return Encoding.UTF8.GetBytes($"{Environment.MachineName}:{Environment.UserName}");
    }

    [SupportedOSPlatform("windows")]
    private static string? GetWindowsMachineGuid()
    {
        using var regKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return regKey?.GetValue("MachineGuid")?.ToString();
    }
}
