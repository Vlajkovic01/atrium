using Atrium.Core.Identity;
using Atrium.Data;
using Atrium.Identity.Passwords;
using Atrium.Identity.Sessions;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Identity.Users;

public enum UserError
{
    NotFound,
    InvalidName,
    NameTaken,
    InvalidDisplayName,
    WeakPassword,
    WrongPassword,
    InvalidLocale,
    InvalidQuota,

    /// <summary>The change would leave the server without an active admin.</summary>
    LastAdmin,

    /// <summary>The user was changed by someone else since it was read.</summary>
    VersionConflict,

    /// <summary>The account is scheduled for deletion; it cannot be enabled until the deletion is cancelled.</summary>
    DeletionScheduled,
}

public sealed record UserResult(User? User, UserError? Error)
{
    public static UserResult Ok(User user) => new(user, null);

    public static UserResult Fail(UserError error) => new(null, error);
}

public sealed record NewUser(string Name, string? DisplayName, string Password, UserRole Role, long? QuotaBytes, string? Locale);

/// <summary>Fields an admin may change. Null means "leave as it is".</summary>
public sealed record AdminUserChange(string? DisplayName, UserRole? Role, bool? Disabled, long? QuotaBytes, bool ClearQuota);

public sealed class UserService(
    AtriumDbContext db,
    PasswordService passwords,
    SessionService sessions,
    TimeProvider time)
{
    public const int MaxDisplayNameLength = 128;

    public async Task<UserResult> CreateAsync(NewUser request, CancellationToken ct)
    {
        if (!UserName.TryNormalize(request.Name, out var name, out var key))
        {
            return UserResult.Fail(UserError.InvalidName);
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? name : request.DisplayName.Trim();
        if (displayName.Length > MaxDisplayNameLength)
        {
            return UserResult.Fail(UserError.InvalidDisplayName);
        }

        if (!PasswordPolicy.IsAcceptable(request.Password))
        {
            return UserResult.Fail(UserError.WeakPassword);
        }

        if (request.QuotaBytes is < 0)
        {
            return UserResult.Fail(UserError.InvalidQuota);
        }

        var locale = request.Locale ?? SupportedLocales.Default;
        if (!SupportedLocales.IsSupported(locale))
        {
            return UserResult.Fail(UserError.InvalidLocale);
        }

        if (await db.Users.AnyAsync(u => u.NameKey == key, ct))
        {
            return UserResult.Fail(UserError.NameTaken);
        }

        var now = time.GetUtcNow();
        var user = new User
        {
            Name = name,
            NameKey = key,
            DisplayName = displayName,
            PasswordHash = await passwords.HashAsync(request.Password, ct),
            Role = request.Role,
            Locale = locale,
            QuotaBytes = request.QuotaBytes,
            CreatedAt = now,
            PasswordChangedAt = now,
        };
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two requests created the same name at once; the unique index decided.
            db.ChangeTracker.Clear();
            return UserResult.Fail(UserError.NameTaken);
        }

        return UserResult.Ok(user);
    }

    public async Task<UserResult> UpdateProfileAsync(Guid userId, string? displayName, string? locale, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return UserResult.Fail(UserError.NotFound);
        }

        if (displayName is not null)
        {
            var trimmed = displayName.Trim();
            if (trimmed.Length is 0 or > MaxDisplayNameLength)
            {
                return UserResult.Fail(UserError.InvalidDisplayName);
            }

            user.DisplayName = trimmed;
        }

        if (locale is not null)
        {
            if (!SupportedLocales.IsSupported(locale))
            {
                return UserResult.Fail(UserError.InvalidLocale);
            }

            user.Locale = locale;
        }

        return await SaveAsync(user, ct);
    }

    /// <summary>
    /// The user changes their own password. Every other session is signed
    /// out, so someone who knew the old password loses access.
    /// </summary>
    public async Task<UserResult> ChangePasswordAsync(
        Guid userId, string currentPassword, string newPassword, Guid? keepSessionId, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return UserResult.Fail(UserError.NotFound);
        }

        if (currentPassword.Length > PasswordPolicy.MaxLength
            || await passwords.VerifyAsync(currentPassword, user.PasswordHash, ct) == PasswordVerification.Failed)
        {
            return UserResult.Fail(UserError.WrongPassword);
        }

        if (!PasswordPolicy.IsAcceptable(newPassword))
        {
            return UserResult.Fail(UserError.WeakPassword);
        }

        user.PasswordHash = await passwords.HashAsync(newPassword, ct);
        user.PasswordChangedAt = time.GetUtcNow();
        user.PasswordResetByAdmin = false;
        var result = await SaveAsync(user, ct);
        if (result.Error is null)
        {
            await sessions.RevokeAllAsync(userId, keepSessionId, ct);
        }

        return result;
    }

    /// <summary>Confirms the user knows their password before a sensitive change.</summary>
    public async Task<bool> VerifyPasswordAsync(Guid userId, string? password, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        return user is not null
            && password is { Length: > 0 and <= PasswordPolicy.MaxLength }
            && await passwords.VerifyAsync(password, user.PasswordHash, ct) != PasswordVerification.Failed;
    }

    public async Task<UserResult> AdminUpdateAsync(Guid userId, int expectedVersion, AdminUserChange change, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return UserResult.Fail(UserError.NotFound);
        }

        if (user.Version != expectedVersion)
        {
            return UserResult.Fail(UserError.VersionConflict);
        }

        var wasActiveAdmin = user is { Role: UserRole.Admin, Disabled: false };
        var role = change.Role ?? user.Role;
        var disabled = change.Disabled ?? user.Disabled;
        if (!disabled && user.DeletionScheduledAt is not null)
        {
            // Enabling an account whose files are about to be removed would
            // let someone work in it until the deletion runs.
            return UserResult.Fail(UserError.DeletionScheduled);
        }

        var staysActiveAdmin = role == UserRole.Admin && !disabled;
        if (wasActiveAdmin && !staysActiveAdmin
            && !await db.Users.AnyAsync(u => u.Id != userId && u.Role == UserRole.Admin && !u.Disabled, ct))
        {
            // Nobody could administer the server any more, and there is no
            // way back in short of editing the database by hand.
            return UserResult.Fail(UserError.LastAdmin);
        }

        if (change.DisplayName is not null)
        {
            var trimmed = change.DisplayName.Trim();
            if (trimmed.Length is 0 or > MaxDisplayNameLength)
            {
                return UserResult.Fail(UserError.InvalidDisplayName);
            }

            user.DisplayName = trimmed;
        }

        if (change.ClearQuota)
        {
            user.QuotaBytes = null;
        }
        else if (change.QuotaBytes is { } quota)
        {
            if (quota < 0)
            {
                return UserResult.Fail(UserError.InvalidQuota);
            }

            user.QuotaBytes = quota;
        }

        // Access tokens carry the role, and a disabled user must lose access
        // now, so either change signs the user out everywhere.
        var mustSignOut = role != user.Role || (disabled && !user.Disabled);
        user.Role = role;
        user.Disabled = disabled;

        var result = await SaveAsync(user, ct);
        if (result.Error is null && mustSignOut)
        {
            await sessions.RevokeAllAsync(userId, exceptSessionId: null, ct);
        }

        return result;
    }

    /// <summary>
    /// An admin sets a new password for someone else. The user is signed out
    /// everywhere and is told at next sign-in (DESIGN.md 6.3), because this
    /// is the one way an admin could get into another user's account.
    /// </summary>
    public async Task<UserResult> AdminResetPasswordAsync(Guid userId, string newPassword, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return UserResult.Fail(UserError.NotFound);
        }

        if (!PasswordPolicy.IsAcceptable(newPassword))
        {
            return UserResult.Fail(UserError.WeakPassword);
        }

        user.PasswordHash = await passwords.HashAsync(newPassword, ct);
        user.PasswordChangedAt = time.GetUtcNow();
        user.PasswordResetByAdmin = true;
        var result = await SaveAsync(user, ct);
        if (result.Error is null)
        {
            await sessions.RevokeAllAsync(userId, exceptSessionId: null, ct);
        }

        return result;
    }

    private async Task<UserResult> SaveAsync(User user, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return UserResult.Ok(user);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return UserResult.Fail(UserError.VersionConflict);
        }
    }
}
