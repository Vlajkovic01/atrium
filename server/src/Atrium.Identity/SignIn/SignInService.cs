using Atrium.Core.Identity;
using Atrium.Data;
using Atrium.Identity.Crypto;
using Atrium.Identity.Passwords;
using Atrium.Identity.Totp;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Identity.SignIn;

public enum PasswordSignInStatus
{
    /// <summary>Wrong name, wrong password or disabled account. Callers must not tell these apart to the client.</summary>
    Failed,
    Success,

    /// <summary>The password was right and the account has two-step verification.</summary>
    TotpRequired,
}

public sealed record PasswordSignInResult(PasswordSignInStatus Status, User? User);

public sealed class SignInService(
    AtriumDbContext db,
    PasswordService passwords,
    SecretProtector protector,
    TimeProvider time)
{
    public const string TotpProtectionPurpose = "totp-secret";

    public async Task<PasswordSignInResult> PasswordSignInAsync(string name, string password, CancellationToken ct)
    {
        var failed = new PasswordSignInResult(PasswordSignInStatus.Failed, null);
        if (password.Length > PasswordPolicy.MaxLength)
        {
            return failed;
        }

        var key = UserName.KeyOf(name);
        var user = await db.Users.SingleOrDefaultAsync(u => u.NameKey == key, ct);
        if (user is null)
        {
            // Spend the same time as a real check, so the response does not
            // reveal whether the account exists.
            await passwords.VerifyDecoyAsync(password, ct);
            return failed;
        }

        var verification = await passwords.VerifyAsync(password, user.PasswordHash, ct);
        if (verification == PasswordVerification.Failed || user.Disabled)
        {
            return failed;
        }

        if (verification == PasswordVerification.SuccessRehashNeeded)
        {
            user.PasswordHash = await passwords.HashAsync(password, ct);
            await db.SaveChangesAsync(ct);
        }

        var hasTotp = await db.TotpCredentials.AnyAsync(t => t.UserId == user.Id && t.ConfirmedAt != null, ct);
        return new PasswordSignInResult(hasTotp ? PasswordSignInStatus.TotpRequired : PasswordSignInStatus.Success, user);
    }

    /// <summary>
    /// Second step. Returns the user when the code is right, was not used
    /// before, and the account is still usable.
    /// </summary>
    public async Task<User?> TotpSignInAsync(Guid userId, string code, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        var credential = await db.TotpCredentials.SingleOrDefaultAsync(t => t.UserId == userId && t.ConfirmedAt != null, ct);
        if (user is null || user.Disabled || credential is null)
        {
            return null;
        }

        var secret = protector.Unprotect(credential.EncryptedSecret, TotpProtectionPurpose);
        var step = TotpGenerator.Verify(secret, code, time.GetUtcNow(), credential.LastUsedStep);
        if (step is null)
        {
            return null;
        }

        credential.LastUsedStep = step;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The same code arrived twice at once; only one request may win.
            return null;
        }

        return user;
    }
}
