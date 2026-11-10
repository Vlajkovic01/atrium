using System.Security.Cryptography;
using Atrium.Identity.Crypto;

namespace Atrium.Identity.Tests;

public sealed class CryptoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void The_key_is_created_once_and_reloaded_unchanged()
    {
        var first = ServerKeys.LoadOrCreate(_dir);
        var second = ServerKeys.LoadOrCreate(_dir);

        Assert.Equal(first.Derive("purpose"), second.Derive("purpose"));
        Assert.Equal(32, new FileInfo(Path.Combine(_dir, ServerKeys.FileName)).Length);
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Different_purposes_and_different_servers_get_different_keys()
    {
        var keys = ServerKeys.LoadOrCreate(_dir);
        var other = ServerKeys.LoadOrCreate(Path.Combine(_dir, "other-server"));

        Assert.NotEqual(keys.Derive("tokens"), keys.Derive("urls"));
        Assert.NotEqual(keys.Derive("tokens"), other.Derive("tokens"));
    }

    [Fact]
    public void A_damaged_key_file_is_reported_and_never_replaced()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, ServerKeys.FileName);
        File.WriteAllBytes(path, [1, 2, 3]);

        var error = Assert.Throws<InvalidDataException>(() => ServerKeys.LoadOrCreate(_dir));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
    }

    [Fact]
    public void The_key_file_is_readable_by_its_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows has no mode bits; the file inherits the directory's access list.
            return;
        }

        ServerKeys.LoadOrCreate(_dir);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(_dir, ServerKeys.FileName)));
    }

    [Fact]
    public void A_protected_secret_round_trips_and_is_not_stored_in_the_clear()
    {
        var protector = new SecretProtector(ServerKeys.LoadOrCreate(_dir));

        var stored = protector.ProtectString("s3-secret-access-key", "storage-location");

        Assert.Equal("s3-secret-access-key", protector.UnprotectString(stored, "storage-location"));
        Assert.DoesNotContain("s3-secret", System.Text.Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_secret_encrypts_differently_each_time()
    {
        var protector = new SecretProtector(ServerKeys.LoadOrCreate(_dir));

        Assert.NotEqual(protector.ProtectString("secret", "p"), protector.ProtectString("secret", "p"));
    }

    [Fact]
    public void A_secret_cannot_be_read_under_another_purpose()
    {
        var protector = new SecretProtector(ServerKeys.LoadOrCreate(_dir));
        var stored = protector.ProtectString("secret", "totp");

        Assert.ThrowsAny<CryptographicException>(() => protector.UnprotectString(stored, "storage-location"));
    }

    [Fact]
    public void A_secret_cannot_be_read_with_another_servers_key()
    {
        var stored = new SecretProtector(ServerKeys.LoadOrCreate(_dir)).ProtectString("secret", "p");
        var other = new SecretProtector(ServerKeys.LoadOrCreate(Path.Combine(_dir, "other-server")));

        Assert.ThrowsAny<CryptographicException>(() => other.UnprotectString(stored, "p"));
    }

    [Fact]
    public void Any_altered_byte_is_detected()
    {
        var protector = new SecretProtector(ServerKeys.LoadOrCreate(_dir));
        var stored = protector.ProtectString("secret", "p");

        for (var i = 0; i < stored.Length; i++)
        {
            var tampered = (byte[])stored.Clone();
            tampered[i] ^= 0x01;
            Assert.ThrowsAny<CryptographicException>(() => protector.UnprotectString(tampered, "p"));
        }
    }

    [Fact]
    public void Truncated_or_empty_values_are_rejected()
    {
        var protector = new SecretProtector(ServerKeys.LoadOrCreate(_dir));

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect([], "p"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(new byte[10], "p"));
    }

    [Fact]
    public void An_empty_secret_round_trips()
    {
        var protector = new SecretProtector(ServerKeys.LoadOrCreate(_dir));

        Assert.Empty(protector.Unprotect(protector.Protect([], "p"), "p"));
    }
}
