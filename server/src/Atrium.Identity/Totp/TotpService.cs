using System.Security.Cryptography;
using Atrium.Core.Identity;
using Atrium.Data;
using Atrium.Identity.Codes;
using Atrium.Identity.Crypto;
using Atrium.Identity.SignIn;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Identity.Totp;

/// <summary>
/// Fallback codes for a lost authenticator: ten consonants shown as
/// XXXXX-XXXXX. Each works once and is stored hashed.
/// </summary>
public static class RecoveryCodes
{
    public const int Count = 10;
    private const int Length = 10;

    public static string New()
    {
        var raw = RandomNumberGenerator.GetString(UserCode.Alphabet, Length);
        return $"{raw[..5]}-{raw[5..]}";
    }

    /// <summary>Canonical form of what a person typed, or null if it cannot be a recovery code.</summary>
    public static string? Normalize(string? input)
    {
        if (input is null || input.Length > 32)
        {
            return null;
        }

        var letters = input.Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray();
        return letters.Length == Length && letters.All(c => UserCode.Alphabet.Contains(c, StringComparison.Ordinal))
            ? $"{new string(letters, 0, 5)}-{new string(letters, 5, 5)}"
            : null;
    }
}

public sealed record TotpEnrollment(byte[] Secret);

/// <summary>Turning two-step verification on and off for a user.</summary>
public sealed class TotpService(AtriumDbContext db, SecretProtector protector, TimeProvider time)
{
    /// <summary>
    /// Starts enrollment with a new secret. Nothing is enforced until the
    /// user proves their authenticator works. Null if already enabled.
    /// </summary>
    public async Task<TotpEnrollment?> BeginAsync(Guid userId, CancellationToken ct)
    {
        var existing = await db.TotpCredentials.SingleOrDefaultAsync(t => t.UserId == userId, ct);
        if (existing?.ConfirmedAt is not null)
        {
            return null;
        }

        var secret = TotpGenerator.NewSecret();
        var encrypted = protector.Protect(secret, SignInService.TotpProtectionPurpose);
        if (existing is null)
        {
            db.TotpCredentials.Add(new TotpCredential
            {
                UserId = userId,
                EncryptedSecret = encrypted,
                CreatedAt = time.GetUtcNow(),
            });
        }
        else
        {
            // An abandoned attempt: replace its secret rather than keep a
            // secret that may have been shown on someone else's screen.
            existing.EncryptedSecret = encrypted;
        }

        await db.SaveChangesAsync(ct);
        return new TotpEnrollment(secret);
    }

    /// <summary>
    /// Completes enrollment and returns the recovery codes, which exist in
    /// plain form only in this return value. Null if the code is wrong or
    /// there is nothing to confirm.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ConfirmAsync(Guid userId, string code, CancellationToken ct)
    {
        var credential = await db.TotpCredentials.SingleOrDefaultAsync(t => t.UserId == userId && t.ConfirmedAt == null, ct);
        if (credential is null)
        {
            return null;
        }

        var secret = protector.Unprotect(credential.EncryptedSecret, SignInService.TotpProtectionPurpose);
        var now = time.GetUtcNow();
        if (TotpGenerator.Verify(secret, code, now, lastUsedStep: null) is not { } step)
        {
            return null;
        }

        credential.ConfirmedAt = now;
        credential.LastUsedStep = step;

        await db.RecoveryCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        var codes = Enumerable.Range(0, RecoveryCodes.Count).Select(_ => RecoveryCodes.New()).ToList();
        db.RecoveryCodes.AddRange(codes.Select(c => new RecoveryCode { UserId = userId, CodeHash = SecretCode.Hash(c) }));
        await db.SaveChangesAsync(ct);
        return codes;
    }

    public async Task<bool> IsEnabledAsync(Guid userId, CancellationToken ct) =>
        await db.TotpCredentials.AnyAsync(t => t.UserId == userId && t.ConfirmedAt != null, ct);

    public async Task DisableAsync(Guid userId, CancellationToken ct)
    {
        await db.TotpCredentials.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
        await db.RecoveryCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
    }
}
