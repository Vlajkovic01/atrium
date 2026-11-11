using System.Net;
using System.Security.Cryptography;
using System.Text;
using Atrium.Core.Identity;
using Atrium.Core.Settings;
using Atrium.Data;
using Atrium.Identity.Codes;
using Atrium.Identity.Users;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Setup;

public enum SetupAdminError
{
    AlreadyDone,
    TokenRequired,
    TokenInvalid,
}

public sealed record SetupAdminResult(User? User, SetupAdminError? SetupError, UserError? UserError);

/// <summary>
/// First-run setup (DESIGN.md 6.8). Until an admin exists, anyone who can
/// reach the server could claim it, so creating the first admin requires a
/// token that only someone with access to the server's log can read.
/// </summary>
public sealed class SetupService(TimeProvider time, ILogger<SetupService> logger)
{
    public const string CompletedSettingKey = "setup.completed";

    // Creating the first admin is checked and done one request at a time,
    // so two simultaneous requests cannot both find "no admin yet".
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>A fresh token every start. It only matters while there is no admin.</summary>
    public string Token { get; } = SecretCode.NewToken(byteLength: 16);

    public async Task AnnounceAsync(AtriumDbContext db, CancellationToken ct)
    {
        if (await AdminExistsAsync(db, ct))
        {
            return;
        }

        // Deliberately logged: reading this log is the proof of control over
        // the server. The token is useless once the first admin exists.
        logger.LogWarning(
            "Atrium is not set up yet. Open the web interface and enter this setup token to create the first admin: {SetupToken}",
            Token);
    }

    public static Task<bool> AdminExistsAsync(AtriumDbContext db, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Role == UserRole.Admin, ct);

    public static async Task<bool> IsCompletedAsync(AtriumDbContext db, CancellationToken ct) =>
        await db.Settings.AnyAsync(s => s.Key == CompletedSettingKey && s.Value == "true", ct);

    /// <summary>
    /// The token is skipped only for a request made on the server itself,
    /// with no proxy in between. A private network address is not enough:
    /// behind Docker or a reverse proxy every request can look local.
    /// </summary>
    public static bool TokenRequired(HttpContext http)
    {
        var headers = http.Request.Headers;
        var forwarded = headers.ContainsKey("X-Forwarded-For")
            || headers.ContainsKey("Forwarded")
            || headers.ContainsKey("X-Real-IP");
        return forwarded || http.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address);
    }

    public async Task<SetupAdminResult> CreateFirstAdminAsync(
        AtriumDbContext db, UserService users, HttpContext http, NewUser request, string? token, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (await AdminExistsAsync(db, ct))
            {
                return new SetupAdminResult(null, SetupAdminError.AlreadyDone, null);
            }

            if (TokenRequired(http))
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    return new SetupAdminResult(null, SetupAdminError.TokenRequired, null);
                }

                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(token.Trim()), Encoding.UTF8.GetBytes(Token)))
                {
                    return new SetupAdminResult(null, SetupAdminError.TokenInvalid, null);
                }
            }

            var result = await users.CreateAsync(request with { Role = UserRole.Admin }, ct);
            return new SetupAdminResult(result.User, null, result.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkCompletedAsync(AtriumDbContext db, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == CompletedSettingKey, ct);
        if (setting is null)
        {
            db.Settings.Add(new Setting { Key = CompletedSettingKey, Value = "true", UpdatedAt = now });
        }
        else
        {
            setting.Value = "true";
            setting.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}
