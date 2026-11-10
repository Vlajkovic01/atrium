using Atrium.Core.Identity;
using Atrium.Data;
using Atrium.Identity.Codes;
using Atrium.Identity.Sessions;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Identity.Pairing;

public sealed record StartedPairing(string DeviceCode, string UserCode, DateTimeOffset ExpiresAt);

public enum PairingRedeemStatus
{
    Pending,
    Denied,

    /// <summary>Unknown, expired or already used. The device must start over.</summary>
    Expired,
    Approved,
}

public sealed record PairingRedeemResult(PairingRedeemStatus Status, IssuedSession? Issued);

/// <summary>
/// Sign-in for devices without a keyboard (DESIGN.md 6.4). The device holds
/// a secret device code and shows a short user code; a signed-in user
/// approves the user code; the device then exchanges its device code for a
/// session that belongs to the approver.
/// </summary>
public sealed class PairingService(AtriumDbContext db, SessionService sessions, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    // Starting a pairing needs no sign-in, so the number of open requests
    // one address may hold is capped to keep the table from being flooded.
    public const int MaxOpenPerAddress = 10;

    /// <summary>Null when the address already has too many open requests.</summary>
    public async Task<StartedPairing?> StartAsync(DeviceInfo device, string? ip, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // Expired requests are useless and would keep their user codes taken.
        await db.PairingRequests.Where(p => p.ExpiresAt <= now).ExecuteDeleteAsync(ct);

        if (await db.PairingRequests.CountAsync(p => p.RequestIp == ip, ct) >= MaxOpenPerAddress)
        {
            return null;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var userCode = UserCode.New();
            var deviceCode = SecretCode.NewToken();
            db.PairingRequests.Add(new PairingRequest
            {
                DeviceCodeHash = SecretCode.Hash(deviceCode),
                UserCode = userCode,
                DeviceName = device.Name,
                DeviceKind = device.Kind,
                Platform = device.Platform,
                AppVersion = device.AppVersion,
                RequestIp = ip,
                State = PairingState.Pending,
                CreatedAt = now,
                ExpiresAt = now + Lifetime,
            });

            try
            {
                await db.SaveChangesAsync(ct);
                return new StartedPairing(deviceCode, userCode, now + Lifetime);
            }
            catch (DbUpdateException)
            {
                // The user code is already in use by another open request. Draw again.
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Could not allocate a free pairing code.");
    }

    /// <summary>The open request a signed-in user is about to approve, or null.</summary>
    public async Task<PairingRequest?> FindOpenAsync(string userCode, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await db.PairingRequests.SingleOrDefaultAsync(
            p => p.UserCode == userCode && p.State == PairingState.Pending && p.ExpiresAt > now, ct);
    }

    public Task<bool> ApproveAsync(string userCode, Guid approverUserId, SessionRestrictions restrictions, CancellationToken ct) =>
        DecideAsync(userCode, ct, request =>
        {
            request.State = PairingState.Approved;
            request.ApprovedByUserId = approverUserId;
            request.ReadOnly = restrictions.ReadOnly;
            request.HidePersonalLibrary = restrictions.HidePersonalLibrary;
        });

    public Task<bool> DenyAsync(string userCode, CancellationToken ct) =>
        DecideAsync(userCode, ct, request => request.State = PairingState.Denied);

    private async Task<bool> DecideAsync(string userCode, CancellationToken ct, Action<PairingRequest> decide)
    {
        var request = await FindOpenAsync(userCode, ct);
        if (request is null)
        {
            return false;
        }

        decide(request);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else decided this request a moment ago.
            db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>The device asks whether it has been approved yet.</summary>
    public async Task<PairingRedeemResult> RedeemAsync(string deviceCode, string? ip, CancellationToken ct)
    {
        var expired = new PairingRedeemResult(PairingRedeemStatus.Expired, null);
        var hash = SecretCode.Hash(deviceCode);
        var request = await db.PairingRequests.SingleOrDefaultAsync(p => p.DeviceCodeHash == hash, ct);
        if (request is null || request.ExpiresAt <= time.GetUtcNow())
        {
            return expired;
        }

        switch (request.State)
        {
            case PairingState.Pending:
                return new PairingRedeemResult(PairingRedeemStatus.Pending, null);
            case PairingState.Denied:
                return new PairingRedeemResult(PairingRedeemStatus.Denied, null);
            case PairingState.Redeemed:
                return expired;
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == request.ApprovedByUserId, ct);
        if (user is null || user.Disabled)
        {
            return new PairingRedeemResult(PairingRedeemStatus.Denied, null);
        }

        // Claim the approval first. If two polls arrive together only one
        // gets past this save, so one approval can never yield two sessions.
        request.State = PairingState.Redeemed;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return expired;
        }

        var issued = await sessions.CreateAsync(
            user,
            new DeviceInfo(request.DeviceName, request.DeviceKind, request.Platform, request.AppVersion),
            new SessionRestrictions(request.ReadOnly, request.HidePersonalLibrary),
            ip,
            ct);
        request.SessionId = issued.Session.Id;
        await db.SaveChangesAsync(ct);
        return new PairingRedeemResult(PairingRedeemStatus.Approved, issued);
    }
}

/// <summary>
/// Single-use codes that let a signed-in session sign in another device by
/// QR, without typing the password there (DESIGN.md 6.5).
/// </summary>
public sealed class LinkCodeService(AtriumDbContext db, SessionService sessions, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public async Task<string> CreateAsync(Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await db.LinkCodes.Where(c => c.ExpiresAt <= now).ExecuteDeleteAsync(ct);

        var code = SecretCode.NewToken();
        db.LinkCodes.Add(new LinkCode
        {
            CodeHash = SecretCode.Hash(code),
            UserId = userId,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        });
        await db.SaveChangesAsync(ct);
        return code;
    }

    /// <summary>Null when the code is unknown, expired, already used, or its owner is disabled.</summary>
    public async Task<IssuedSession?> RedeemAsync(string code, DeviceInfo device, string? ip, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var hash = SecretCode.Hash(code);
        var link = await db.LinkCodes.SingleOrDefaultAsync(c => c.CodeHash == hash, ct);
        if (link is null || link.UsedAt is not null || link.ExpiresAt <= now)
        {
            return null;
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == link.UserId, ct);
        if (user is null || user.Disabled)
        {
            return null;
        }

        link.UsedAt = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return null;
        }

        return await sessions.CreateAsync(user, device, SessionRestrictions.None, ip, ct);
    }
}
