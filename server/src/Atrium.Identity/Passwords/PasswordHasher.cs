using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using NSec.Cryptography;

namespace Atrium.Identity.Passwords;

/// <summary>Argon2id cost. Memory is in kibibytes.</summary>
public sealed record Argon2Settings(int MemoryKib, int Passes)
{
    /// <summary>64 MiB, 3 passes.</summary>
    public static Argon2Settings Default { get; } = new(64 * 1024, 3);

    /// <summary>19 MiB, 2 passes: the OWASP minimum, for the small hardware profile.</summary>
    public static Argon2Settings LowMemory { get; } = new(19 * 1024, 2);
}

public enum PasswordVerification
{
    Failed,
    Success,

    /// <summary>Correct, but hashed with other settings than the current ones; hash again and store.</summary>
    SuccessRehashNeeded,
}

public static class PasswordPolicy
{
    public const int MinLength = 8;

    /// <summary>Upper bound so a huge "password" cannot be used to burn CPU.</summary>
    public const int MaxLength = 256;

    public static bool IsAcceptable(string? password) =>
        password is { Length: >= MinLength and <= MaxLength };
}

/// <summary>
/// Argon2id password hashing (DESIGN.md 6.1). Hashes are stored in the
/// standard PHC text form, which carries its own parameters and salt, so
/// the cost can be raised later and old hashes still verify.
/// </summary>
public sealed class PasswordHasher(Argon2Settings settings)
{
    private const int SaltLength = 16;
    private const int HashLength = 32;
    private const string Prefix = "$argon2id$v=19$";

    // Limits applied to stored hashes before computing, so a corrupted or
    // planted row cannot make the server allocate gigabytes.
    private const int MaxMemoryKib = 1024 * 1024;
    private const int MaxPasses = 16;

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Derive(password, salt, settings.MemoryKib, settings.Passes, HashLength);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}m={settings.MemoryKib},t={settings.Passes},p=1${ToBase64(salt)}${ToBase64(hash)}");
    }

    public PasswordVerification Verify(string password, string encodedHash)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!TryParse(encodedHash, out var memoryKib, out var passes, out var salt, out var expected))
        {
            return PasswordVerification.Failed;
        }

        var actual = Derive(password, salt, memoryKib, passes, expected.Length);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return PasswordVerification.Failed;
        }

        return memoryKib == settings.MemoryKib && passes == settings.Passes && expected.Length == HashLength
            ? PasswordVerification.Success
            : PasswordVerification.SuccessRehashNeeded;
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKib, int passes, int length)
    {
        var parameters = new Argon2Parameters
        {
            DegreeOfParallelism = 1,
            MemorySize = memoryKib,
            NumberOfPasses = passes,
        };
        return PasswordBasedKeyDerivationAlgorithm.Argon2id(parameters).DeriveBytes(password, salt, length);
    }

    private static bool TryParse(string? encoded, out int memoryKib, out int passes, out byte[] salt, out byte[] hash)
    {
        memoryKib = 0;
        passes = 0;
        salt = [];
        hash = [];
        if (encoded is null || !encoded.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        // m=65536,t=3,p=1 $ salt $ hash
        var parts = encoded[Prefix.Length..].Split('$');
        if (parts.Length != 3)
        {
            return false;
        }

        var costs = parts[0].Split(',');
        if (costs.Length != 3
            || !TryReadCost(costs[0], "m=", out memoryKib)
            || !TryReadCost(costs[1], "t=", out passes)
            || costs[2] != "p=1"
            || memoryKib is < 8 or > MaxMemoryKib
            || passes is < 1 or > MaxPasses)
        {
            return false;
        }

        // libsodium accepts exactly one salt length. A hash with another one
        // was not made by this server and cannot be verified by it.
        return TryFromBase64(parts[1], out salt) && salt.Length == SaltLength
            && TryFromBase64(parts[2], out hash) && hash.Length is >= 16 and <= 64;
    }

    private static bool TryReadCost(string text, string prefix, out int value)
    {
        value = 0;
        return text.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(text.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    // The PHC format uses standard base64 without padding.
    private static string ToBase64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static bool TryFromBase64(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length == 0 || text.Contains('=', StringComparison.Ordinal))
        {
            return false;
        }

        var padded = text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');
        if (!Base64.IsValid(padded))
        {
            return false;
        }

        bytes = Convert.FromBase64String(padded);
        return true;
    }
}

/// <summary>
/// Runs password hashing off the request threads and never more than two at
/// a time. Each Argon2id run holds tens of megabytes, so without a limit a
/// burst of sign-in attempts could exhaust memory on a small device.
/// </summary>
public sealed class PasswordService(PasswordHasher hasher) : IDisposable
{
    private const int MaxConcurrent = 2;
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);

    // Verified when the account does not exist, so that an unknown name
    // takes as long as a wrong password and reveals nothing by timing.
    private readonly Lazy<string> _decoyHash = new(() => hasher.Hash(Guid.NewGuid().ToString("N")));

    public async Task<string> HashAsync(string password, CancellationToken ct)
    {
        await _slots.WaitAsync(ct);
        try
        {
            return await Task.Run(() => hasher.Hash(password), ct);
        }
        finally
        {
            _slots.Release();
        }
    }

    public async Task<PasswordVerification> VerifyAsync(string password, string encodedHash, CancellationToken ct)
    {
        await _slots.WaitAsync(ct);
        try
        {
            return await Task.Run(() => hasher.Verify(password, encodedHash), ct);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>Spends the same effort as a real verification and always fails.</summary>
    public async Task VerifyDecoyAsync(string password, CancellationToken ct) =>
        await VerifyAsync(password, _decoyHash.Value, ct);

    public void Dispose() => _slots.Dispose();
}
