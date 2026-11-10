using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Atrium.Identity.Codes;

/// <summary>
/// Random bearer secrets: refresh tokens, pairing device codes, link codes.
/// They carry 256 bits of randomness, so a plain SHA-256 is the right way
/// to store them; a slow password hash would add cost and no security.
/// </summary>
public static class SecretCode
{
    public const int HashLength = 32;

    public static string NewToken(int byteLength = 32) =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(byteLength));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}

/// <summary>
/// The short code a device shows on screen during pairing (DESIGN.md 6.4).
/// Eight consonants: no vowels, so no accidental words, and no digits, so
/// nothing that reads two ways on a TV across the room.
/// </summary>
public static class UserCode
{
    public const string Alphabet = "BCDFGHJKLMNPQRSTVWXZ";
    public const int Length = 8;

    public static string New() =>
        Format(RandomNumberGenerator.GetString(Alphabet, Length));

    /// <summary>
    /// Accepts what a person might type: any case, with or without the dash
    /// or spaces. Returns the canonical XXXX-XXXX form, or null if it cannot
    /// be a code.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (input is null || input.Length > 32)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[Length];
        var count = 0;
        foreach (var raw in input)
        {
            if (raw is '-' or ' ')
            {
                continue;
            }

            var c = char.ToUpperInvariant(raw);
            if (count == Length || !Alphabet.Contains(c, StringComparison.Ordinal))
            {
                return null;
            }

            buffer[count++] = c;
        }

        return count == Length ? Format(buffer) : null;
    }

    private static string Format(ReadOnlySpan<char> code) => $"{code[..4]}-{code[4..]}";
}
