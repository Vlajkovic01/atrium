using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Atrium.Identity.Totp;

/// <summary>
/// Time-based one-time passwords (RFC 6238) with the parameters every
/// authenticator app supports: HMAC-SHA1, six digits, 30-second steps.
/// </summary>
public static class TotpGenerator
{
    public const int SecretLength = 20;
    public const int Digits = 6;
    public const int PeriodSeconds = 30;

    /// <summary>Steps accepted before and after the current one, for clock drift.</summary>
    public const int AllowedDrift = 1;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretLength);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / PeriodSeconds;

    public static string CodeAt(ReadOnlySpan<byte> secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> mac = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(secret, counter, mac);

        // Dynamic truncation, RFC 4226 section 5.3.
        var offset = mac[^1] & 0x0F;
        var binary = BinaryPrimitives.ReadInt32BigEndian(mac.Slice(offset, 4)) & 0x7FFFFFFF;
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Checks a code. Returns the step it matched, to be stored as the last
    /// used step, or null. A code at or before <paramref name="lastUsedStep"/>
    /// is refused, so a code seen over someone's shoulder cannot be replayed
    /// within its validity window.
    /// </summary>
    public static long? Verify(ReadOnlySpan<byte> secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        if (code is null)
        {
            return null;
        }

        var typed = code.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (typed.Length != Digits || !typed.All(char.IsAsciiDigit))
        {
            return null;
        }

        var typedBytes = Encoding.ASCII.GetBytes(typed);
        var current = StepAt(now);
        long? matched = null;
        for (var step = current - AllowedDrift; step <= current + AllowedDrift; step++)
        {
            var expected = Encoding.ASCII.GetBytes(CodeAt(secret, step));

            // Compare every candidate in constant time and keep going, so the
            // response time does not say which step, if any, matched.
            if (CryptographicOperations.FixedTimeEquals(expected, typedBytes) && matched is null)
            {
                matched = step;
            }
        }

        return matched is { } m && (lastUsedStep is null || m > lastUsedStep) ? m : null;
    }

    /// <summary>The otpauth URI that authenticator apps read from a QR code.</summary>
    public static string ProvisioningUri(string issuer, string account, ReadOnlySpan<byte> secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}" +
        $"&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
}

/// <summary>RFC 4648 base32 without padding, the form authenticator apps expect.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            result.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return result.ToString();
    }

    public static bool TryDecode(string? text, out byte[] data)
    {
        data = [];
        if (text is null)
        {
            return false;
        }

        var output = new List<byte>(text.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var raw in text.TrimEnd('='))
        {
            var index = Alphabet.IndexOf(char.ToUpperInvariant(raw), StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        data = [.. output];
        return true;
    }
}
