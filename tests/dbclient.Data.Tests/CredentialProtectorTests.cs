using dbclient.Services;

namespace dbclient.Data.Tests;

[Collection("AppPaths")]
public class CredentialProtectorTests
{
    [Theory]
    [InlineData("dpapi:abc", true)]
    [InlineData("aesgcm:abc", true)]
    [InlineData("ENC:abc", true)]
    [InlineData("enc:abc", false)]
    [InlineData("plain", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsEncrypted_prefix_detection(string? value, bool expected)
        => Assert.Equal(expected, CredentialProtector.IsEncrypted(value));

    [Fact]
    public void Unprefixed_value_is_returned_unchanged()
    {
        Assert.Equal("legacy-plain", CredentialProtector.Decrypt("legacy-plain"));
        Assert.True(CredentialProtector.TryDecrypt("legacy-plain", out var p));
        Assert.Equal("legacy-plain", p);
    }

    [Fact]
    public void Empty_round_trips_to_empty()
    {
        Assert.Equal("", CredentialProtector.Encrypt(""));
        Assert.Equal("", CredentialProtector.Encrypt(null));
        Assert.Equal("", CredentialProtector.Decrypt(null));
        Assert.True(CredentialProtector.TryDecrypt("", out var p));
        Assert.Equal("", p);
    }

    [Theory]
    [InlineData("aesgcm:not-base64!!")]
    [InlineData("aesgcm:AAAA")]                    // too short
    [InlineData("aesgcm:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // bad tag
    [InlineData("ENC:garbage")]
    [InlineData("dpapi:garbage")]
    public void Garbage_decrypts_to_empty_and_TryDecrypt_false(string stored)
    {
        TestEnvironment.RequireRedirectedRoot(); // key derivation reads the salt under AppPaths.Root
        Assert.Equal("", CredentialProtector.Decrypt(stored));
        Assert.False(CredentialProtector.TryDecrypt(stored, out var p));
        Assert.Equal("", p);
    }

    [Fact]
    public void Round_trip()
    {
        TestEnvironment.RequireRedirectedRoot();
        const string secret = "p@ss wörd ' \" 🔑";
        var stored = CredentialProtector.Encrypt(secret);

        Assert.True(CredentialProtector.IsEncrypted(stored));
        Assert.DoesNotContain(secret, stored);
        Assert.StartsWith(OperatingSystem.IsWindows() ? "dpapi:" : "aesgcm:", stored);

        Assert.Equal(secret, CredentialProtector.Decrypt(stored));
        Assert.True(CredentialProtector.TryDecrypt(stored, out var plain));
        Assert.Equal(secret, plain);

        // Salt was created under the redirected root, not the real profile.
        Assert.True(File.Exists(AppPaths.SaltFile));
        Assert.Equal(32, File.ReadAllBytes(AppPaths.SaltFile).Length);
    }

    [Fact]
    public void Encrypt_is_nondeterministic()
    {
        TestEnvironment.RequireRedirectedRoot();
        Assert.NotEqual(CredentialProtector.Encrypt("x"), CredentialProtector.Encrypt("x"));
    }

    [Fact]
    public void Tampered_ciphertext_fails()
    {
        TestEnvironment.RequireRedirectedRoot();
        Assert.SkipWhen(OperatingSystem.IsWindows(), "AES-GCM path only");
        var stored = CredentialProtector.Encrypt("hello");
        var blob = Convert.FromBase64String(stored["aesgcm:".Length..]);
        blob[^1] ^= 0xFF;
        var tampered = "aesgcm:" + Convert.ToBase64String(blob);
        Assert.False(CredentialProtector.TryDecrypt(tampered, out var p));
        Assert.Equal("", p);
    }
}
