using System.Security.Cryptography;
using System.Text;

namespace dbclient.Services;

public static class CredentialProtector
{
    private const string Prefix = "ENC:";

    public static string Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";

        try
        {
            var key = DeriveKey();
            using var aes = Aes.Create();
            aes.Key = key;
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

            // Prepend IV to ciphertext
            var result = new byte[aes.IV.Length + cipherBytes.Length];
            aes.IV.CopyTo(result, 0);
            cipherBytes.CopyTo(result, aes.IV.Length);

            return Prefix + Convert.ToBase64String(result);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Credential encryption failed, storing plaintext: {ex.Message}");
            return plaintext;
        }
    }

    public static string Decrypt(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return "";

        // Not encrypted — return as-is (migration path)
        if (!ciphertext.StartsWith(Prefix)) return ciphertext;

        try
        {
            var data = Convert.FromBase64String(ciphertext[Prefix.Length..]);
            var key = DeriveKey();

            using var aes = Aes.Create();
            aes.Key = key;

            // Extract IV from first 16 bytes
            var iv = new byte[16];
            Array.Copy(data, 0, iv, 0, 16);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            var cipherBytes = new byte[data.Length - 16];
            Array.Copy(data, 16, cipherBytes, 0, cipherBytes.Length);

            var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Credential decryption failed, returning raw value: {ex.Message}");
            return ciphertext[Prefix.Length..];
        }
    }

    public static bool IsEncrypted(string? value) => value?.StartsWith(Prefix) == true;

    private static byte[] DeriveKey()
    {
        var machineId = GetMachineId();
        var salt = Encoding.UTF8.GetBytes("dbclient-credential-salt");
        return Rfc2898DeriveBytes.Pbkdf2(machineId, salt, 100_000, HashAlgorithmName.SHA256, 32);
    }

    private static byte[] GetMachineId()
    {
        // Try Linux machine-id first
        try
        {
            var path = "/etc/machine-id";
            if (File.Exists(path))
                return Encoding.UTF8.GetBytes(File.ReadAllText(path).Trim());
        }
        catch { }

        // Try macOS hardware UUID
        try
        {
            var path = "/var/db/SystemKey";
            if (File.Exists(path))
                return File.ReadAllBytes(path);
        }
        catch { }

        // Fallback: Windows machine GUID from registry, or hostname + username
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var regKey = Microsoft.Win32.Registry.LocalMachine?.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                var guid = regKey?.GetValue("MachineGuid")?.ToString();
                if (!string.IsNullOrEmpty(guid))
                    return Encoding.UTF8.GetBytes(guid);
            }
        }
        catch { }

        // Last resort: use hostname + username (not ideal but better than nothing)
        return Encoding.UTF8.GetBytes($"{Environment.MachineName}:{Environment.UserName}");
    }
}
