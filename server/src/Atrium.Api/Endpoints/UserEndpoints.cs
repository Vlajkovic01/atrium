using System.Globalization;
using Atrium.Api.Auth;
using Atrium.Core.Identity;
using Atrium.Data;
using Atrium.Files;
using Atrium.Files.Users;
using Atrium.Identity.Users;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Endpoints;

/// <summary>
/// What an admin sees about a user: the account, never its content
/// (DESIGN.md 6.3, D-004).
/// </summary>
/// <param name="UsedBytes">The space their files take, counted as for their quota.</param>
/// <param name="LastActiveAt">When any of their devices was last seen; null if never.</param>
public sealed record AdminUserView(
    Guid Id,
    string Name,
    string DisplayName,
    UserRole Role,
    bool Disabled,
    long? QuotaBytes,
    string Locale,
    DateTimeOffset CreatedAt,
    bool TotpEnabled,
    int Version,
    DateTimeOffset? DeletionScheduledAt,
    DateTimeOffset? DeleteAfter,
    long UsedBytes = 0,
    DateTimeOffset? LastActiveAt = null);

public sealed record UserPage(IReadOnlyList<AdminUserView> Items, string? NextCursor);

public sealed record CreateUserRequest(
    string Name, string? DisplayName, string Password, UserRole Role, long? QuotaBytes, string? Locale);

/// <param name="ClearQuota">True removes the quota. A null <c>QuotaBytes</c> alone means "do not change".</param>
public sealed record UpdateUserRequest(string? DisplayName, UserRole? Role, bool? Disabled, long? QuotaBytes, bool ClearQuota);

public sealed record ResetPasswordRequest(string NewPassword);

/// <param name="ConfirmName">The account's name, typed by the admin to show they mean this account.</param>
public sealed record ScheduleDeletionRequest(string ConfirmName);

/// <summary>What deleting the account would remove: counts and sizes only (D-004).</summary>
public sealed record DeletionInfo(int Files, int Folders, long Bytes, DateTimeOffset? DeletionScheduledAt, DateTimeOffset? DeleteAfter);

public static class UserEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder routes)
    {
        var users = routes.MapGroup("/users")
            .WithTags("Users")
            .RequireAuthorization(AuthSetup.AdminPolicy, AuthSetup.FullSessionPolicy);

        users.MapGet("/", ListAsync).WithName("ListUsers");

        users.MapPost("/", CreateAsync)
            .WithName("CreateUser")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        users.MapGet("/{id:guid}", GetAsync).WithName("GetUser").ProducesProblem(StatusCodes.Status404NotFound);

        users.MapPatch("/{id:guid}", UpdateAsync)
            .WithName("UpdateUser")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        users.MapGet("/{id:guid}/deletion", DeletionInfoAsync).WithName("GetUserDeletion").ProducesProblem(StatusCodes.Status404NotFound);
        users.MapPost("/{id:guid}/deletion", ScheduleDeletionAsync)
            .WithName("ScheduleUserDeletion")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        users.MapDelete("/{id:guid}/deletion", CancelDeletionAsync)
            .WithName("CancelUserDeletion")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        users.MapPost("/{id:guid}/reset-password", ResetPasswordAsync)
            .WithName("ResetUserPassword")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    private static async Task<UserPage> ListAsync(AtriumDbContext db, string? cursor, int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrEmpty(cursor))
        {
            query = query.Where(u => string.Compare(u.NameKey, cursor) > 0);
        }

        // One extra row tells whether there is a next page.
        var rows = await query.OrderBy(u => u.NameKey).Take(take + 1).ToListAsync(ct);
        var page = rows.Take(take).ToList();
        var ids = page.Select(u => u.Id).ToList();
        var withTotp = await db.TotpCredentials
            .Where(t => ids.Contains(t.UserId) && t.ConfirmedAt != null)
            .Select(t => t.UserId)
            .ToListAsync(ct);

        var used = await UsageService.UsedByOwnersAsync(db, ids, ct);
        var seen = (await db.Sessions.AsNoTracking()
                .Where(s => ids.Contains(s.UserId))
                .Select(s => new { s.UserId, s.LastSeenAt })
                .ToListAsync(ct))
            .GroupBy(s => s.UserId)
            .ToDictionary(g => g.Key, g => g.Max(s => s.LastSeenAt));

        return new UserPage(
            [.. page.Select(u => ToView(u, withTotp.Contains(u.Id)) with
            {
                UsedBytes = used.GetValueOrDefault(u.Id),
                LastActiveAt = seen.TryGetValue(u.Id, out var at) ? at : null,
            })],
            rows.Count > take ? page[^1].NameKey : null);
    }

    private static async Task<Results<Created<AdminUserView>, ProblemHttpResult>> CreateAsync(
        CreateUserRequest request, UserService users, CancellationToken ct)
    {
        if (request.Name is null || request.Password is null || !Enum.IsDefined(request.Role))
        {
            return ApiProblem.Invalid();
        }

        var result = await users.CreateAsync(
            new NewUser(request.Name, request.DisplayName, request.Password, request.Role, request.QuotaBytes, request.Locale), ct);
        return result.User is null
            ? ApiProblem.From(result.Error!.Value)
            : TypedResults.Created($"/api/v1/users/{result.User.Id}", ToView(result.User, totpEnabled: false));
    }

    private static async Task<Results<Ok<AdminUserView>, ProblemHttpResult>> GetAsync(
        Guid id, HttpContext http, AtriumDbContext db, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return ApiProblem.Missing();
        }

        SetETag(http, user);
        return TypedResults.Ok(ToView(user, await HasTotpAsync(db, id, ct)));
    }

    private static async Task<Results<Ok<AdminUserView>, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateUserRequest request,
        // Declared as a parameter so it is part of the API contract and the
        // generated clients know to send it.
        [FromHeader(Name = "If-Match")] string? ifMatch,
        HttpContext http,
        UserService users,
        AtriumDbContext db,
        CancellationToken ct)
    {
        if (request.Role is { } role && !Enum.IsDefined(role))
        {
            return ApiProblem.Invalid();
        }

        // The admin must say which version they looked at, so two admins
        // editing the same user cannot silently undo each other.
        if (!TryReadIfMatch(ifMatch, out var version))
        {
            return ApiProblem.Create(StatusCodes.Status428PreconditionRequired, ApiProblem.PreconditionRequired);
        }

        var result = await users.AdminUpdateAsync(
            id, version, new AdminUserChange(request.DisplayName, request.Role, request.Disabled, request.QuotaBytes, request.ClearQuota), ct);
        if (result.User is null)
        {
            return ApiProblem.From(result.Error!.Value);
        }

        SetETag(http, result.User);
        return TypedResults.Ok(ToView(result.User, await HasTotpAsync(db, id, ct)));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ResetPasswordAsync(
        Guid id,
        ResetPasswordRequest request,
        HttpContext http,
        UserService users,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var adminId = http.User.UserId()!.Value;
        if (request.NewPassword is null || id == adminId)
        {
            // Admins change their own password like everyone else, by
            // proving they know the current one.
            return ApiProblem.Invalid();
        }

        var result = await users.AdminResetPasswordAsync(id, request.NewPassword, ct);
        if (result.Error is { } error)
        {
            return ApiProblem.From(error);
        }

        // Until the activity log exists (Phase 8) the server log is the
        // record of this event. Identifiers only, never the password.
        loggers.CreateLogger("Atrium.Audit").LogWarning(
            "Admin {AdminId} reset the password of user {UserId}.", adminId, id);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<DeletionInfo>, ProblemHttpResult>> DeletionInfoAsync(
        Guid id, AtriumDbContext db, UserDeletionService deletion, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null || await deletion.ImpactAsync(id, ct) is not { } impact)
        {
            return ApiProblem.Missing();
        }

        return TypedResults.Ok(new DeletionInfo(impact.Files, impact.Folders, impact.Bytes, user.DeletionScheduledAt, user.DeleteAfter));
    }

    private static async Task<Results<Ok<AdminUserView>, ProblemHttpResult>> ScheduleDeletionAsync(
        Guid id, ScheduleDeletionRequest request, HttpContext http, UserDeletionService deletion, AtriumDbContext db,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var adminId = http.User.UserId()!.Value;
        var (user, error) = await deletion.ScheduleAsync(adminId, id, request.ConfirmName, ct);
        if (user is null)
        {
            return Deletion(error!.Value);
        }

        loggers.CreateLogger("Atrium.Audit").LogWarning(
            "Admin {AdminId} scheduled user {UserId} for deletion after {DeleteAfter}.", adminId, id, user.DeleteAfter);
        return TypedResults.Ok(ToView(user, await HasTotpAsync(db, id, ct)));
    }

    private static async Task<Results<Ok<AdminUserView>, ProblemHttpResult>> CancelDeletionAsync(
        Guid id, HttpContext http, UserDeletionService deletion, AtriumDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        var (user, error) = await deletion.CancelAsync(id, ct);
        if (user is null)
        {
            return Deletion(error!.Value);
        }

        loggers.CreateLogger("Atrium.Audit").LogWarning(
            "Admin {AdminId} cancelled the deletion of user {UserId}.", http.User.UserId(), id);
        return TypedResults.Ok(ToView(user, await HasTotpAsync(db, id, ct)));
    }

    private static ProblemHttpResult Deletion(DeletionError error) => error switch
    {
        DeletionError.NotFound => ApiProblem.Missing(),
        DeletionError.Self => ApiProblem.Create(StatusCodes.Status409Conflict, ApiProblem.UserCannotDeleteSelf),
        DeletionError.ConfirmationMismatch => ApiProblem.Create(StatusCodes.Status400BadRequest, ApiProblem.UserConfirmationMismatch),
        DeletionError.LastAdmin => ApiProblem.Create(StatusCodes.Status409Conflict, ApiProblem.UserLastAdmin),
        DeletionError.AlreadyScheduled => ApiProblem.Create(StatusCodes.Status409Conflict, ApiProblem.UserDeletionScheduled),
        _ => ApiProblem.Create(StatusCodes.Status409Conflict, ApiProblem.UserNotScheduled),
    };

    private static Task<bool> HasTotpAsync(AtriumDbContext db, Guid userId, CancellationToken ct) =>
        db.TotpCredentials.AnyAsync(t => t.UserId == userId && t.ConfirmedAt != null, ct);

    private static void SetETag(HttpContext http, User user) =>
        http.Response.Headers.ETag = $"\"{user.Version.ToString(CultureInfo.InvariantCulture)}\"";

    private static bool TryReadIfMatch(string? header, out int version)
    {
        version = 0;
        var raw = header?.Trim().Trim('"');
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    private static AdminUserView ToView(User user, bool totpEnabled) => new(
        user.Id, user.Name, user.DisplayName, user.Role, user.Disabled, user.QuotaBytes,
        user.Locale, user.CreatedAt, totpEnabled, user.Version, user.DeletionScheduledAt, user.DeleteAfter);
}
