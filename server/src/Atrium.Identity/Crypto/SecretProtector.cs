using System.Security.Cryptography;
using System.Text;

namespace Atrium.Identity.Crypto;

/// <summary>
/// Encrypts secrets stored in the database (storage credentials, TOTP
/// secrets, provider API keys) with AES-256-GCM. The purpose is bound into
/// the ciphertext, so a value encrypted for one column cannot be moved into
/// another and still decrypt.
/// </summary>
public sealed class SecretProtector
{
    private const byte FormatVersion = 1;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int HeaderLength = 1 + NonceLength + TagLength;

    private readonly byte[] _key;

    public SecretProtector(ServerKeys keys) => _key = keys.Derive("atrium/secret-protection/v1");

    public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose)
    {
        var result = new byte[HeaderLength + plaintext.Length];
        result[0] = FormatVersion;
        var nonce = result.AsSpan(1, NonceLength);
        var tag = result.AsSpan(1 + NonceLength, TagLength);
        var ciphertext = result.AsSpan(HeaderLength);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(purpose));
        return result;
    }

    /// <exception cref="CryptographicException">The value was altered, was protected for another purpose, or with another key.</exception>
    public byte[] Unprotect(ReadOnlySpan<byte> protectedValue, string purpose)
    {
        if (protectedValue.Length < HeaderLength || protectedValue[0] != FormatVersion)
        {
            throw new CryptographicException("Protected value has an unknown format.");
        }

        var nonce = protectedValue.Slice(1, NonceLength);
        var tag = protectedValue.Slice(1 + NonceLength, TagLength);
        var ciphertext = protectedValue[HeaderLength..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, TagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(purpose));
        return plaintext;
    }

    public byte[] ProtectString(string plaintext, string purpose) =>
        Protect(Encoding.UTF8.GetBytes(plaintext), purpose);

    public string UnprotectString(ReadOnlySpan<byte> protectedValue, string purpose) =>
        Encoding.UTF8.GetString(Unprotect(protectedValue, purpose));
}
