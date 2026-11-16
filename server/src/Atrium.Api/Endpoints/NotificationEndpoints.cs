using System.Text.RegularExpressions;
using Atrium.Api.Auth;
using Atrium.Data.Notifications;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Atrium.Api.Endpoints;

/// <param name="Unread">How many are not read yet, for the badge.</param>
public sealed record NotificationList(IReadOnlyList<Notification> Items, int Unread);

/// <param name="Kind">Only kinds a client may report about itself: <c>uploads.finished</c> or <c>uploads.failed</c>.</param>
public sealed record NewNotification(string Kind, IReadOnlyDictionary<string, string>? Params, string? Link);

/// <param name="Ids">These, or every notification when null.</param>
public sealed record MarkNotificationsRead(IReadOnlyList<Guid>? Ids);

/// <summary>
/// The person's notifications (D-045). The server adds its own (a job that
/// failed, a folder gone offline, damage found); a client may add only
/// what it saw itself, such as uploads that finished.
/// </summary>
public static partial class NotificationEndpoints
{
    private static readonly HashSet<string> ClientKinds = new(StringComparer.Ordinal) { "uploads.finished", "uploads.failed" };

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder routes)
    {
        var me = routes.MapGroup("/me/notifications").WithTags("Notifications").RequireAuthorization();

        me.MapGet("/", ListAsync).WithName("ListNotifications");
        me.MapPost("/", AddAsync).WithName("AddNotification").ProducesProblem(StatusCodes.Status400BadRequest);
        me.MapPost("/read", async (MarkNotificationsRead request, HttpContext http, NotificationStore store, CancellationToken ct) =>
            {
                await store.MarkReadAsync(http.User.UserId()!.Value, request.Ids, ct);
                return await ListAsync(http, store, ct);
            })
            .WithName("MarkNotificationsRead");
        me.MapDelete("/{id:guid}", async (Guid id, HttpContext http, NotificationStore store, CancellationToken ct) =>
            {
                await store.RemoveAsync(http.User.UserId()!.Value, id, ct);
                return await ListAsync(http, store, ct);
            })
            .WithName("RemoveNotification");
        me.MapDelete("/", async (HttpContext http, NotificationStore store, CancellationToken ct) =>
            {
                await store.RemoveAsync(http.User.UserId()!.Value, null, ct);
                return await ListAsync(http, store, ct);
            })
            .WithName("ClearNotifications");
        return routes;
    }

    private static async Task<NotificationList> ListAsync(HttpContext http, NotificationStore store, CancellationToken ct)
    {
        var items = await store.ListAsync(http.User.UserId()!.Value, ct);
        return new NotificationList(items, items.Count(n => n.ReadAt is null));
    }

    private static async Task<Results<Ok<NotificationList>, ProblemHttpResult>> AddAsync(
        NewNotification request, HttpContext http, NotificationStore store, CancellationToken ct)
    {
        var values = request.Params ?? new Dictionary<string, string>();
        if (request.Kind is null || !ClientKinds.Contains(request.Kind)
            || values.Count > 5 || values.Any(v => v.Key.Length > 32 || v.Value is null || v.Value.Length > 200)
            || (request.Link is not null && !AppPath().IsMatch(request.Link)))
        {
            return ApiProblem.Invalid();
        }

        await store.AddAsync(http.User.UserId()!.Value, request.Kind, values, request.Link, ct);
        return TypedResults.Ok(await ListAsync(http, store, ct));
    }

    /// <summary>A path inside the web app, never another site.</summary>
    [GeneratedRegex("^/[A-Za-z0-9/_-]{0,200}$")]
    private static partial Regex AppPath();
}
