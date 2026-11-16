using System.Text.RegularExpressions;
using Atrium.Api.Auth;
using Atrium.Api.Files;
using Atrium.Api.Preferences;
using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Files;
using Atrium.Media.Photos;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Atrium.Api.Endpoints;

/// <param name="Kind"><c>files</c>, <c>photos</c>, <c>trash</c>, <c>devices</c>, <c>account</c>, <c>library</c>, <c>view</c>, <c>folder</c>, <c>album</c>, <c>separator</c> or <c>admin</c>.</param>
/// <param name="Label">The person's own name for it; null shows the usual one.</param>
/// <param name="Name">For a library or a view, its own name.</param>
/// <param name="TargetId">For a library, its top folder; for a view, the view; for a pinned folder or album, that folder or album.</param>
/// <param name="ReadOnly">For a library or pinned folder, whether the person cannot change it.</param>
/// <param name="Icon">The person's own icon, by name; null for the usual one.</param>
public sealed record NavItemView(
    string Key, string Kind, string? Label, string? Name, bool Hidden, string? Group, Guid? TargetId, bool ReadOnly, string? Icon = null);

/// <param name="Custom">Whether the person arranged the menu themselves, rather than using the default.</param>
/// <param name="Density"><c>comfortable</c> or <c>compact</c> (icons only).</param>
/// <param name="AdminPosition"><c>bottom</c> or <c>top</c>.</param>
public sealed record NavigationView(
    IReadOnlyList<NavItemView> Items, IReadOnlyList<NavGroup> Groups, bool Custom, string Density, string AdminPosition);

public sealed record SaveViewRequest(string Name, ViewRulesData Rules);

public sealed record ViewPreviewRequest(ViewRulesData Rules, int? Limit);

/// <param name="Total">How many files the view holds in all.</param>
public sealed record ViewPageView(IReadOnlyList<SearchResultView> Items, string? NextCursor, int Total);

/// <summary>
/// Each person's menu and saved views (D-041). A view shows files wherever
/// they are; it never moves or changes anything. Administration is not part
/// of the layout: an admin always sees it, so nobody can lock themselves out.
/// </summary>
public static partial class NavigationEndpoints
{
    public const string InvalidLayout = "navigation.invalid_layout";
    public const string TooManyViews = "navigation.too_many_views";
    public const string InvalidView = "navigation.invalid_view";

    private static readonly string[] BuiltIns = ["files", "photos", "trash", "devices", "account"];

    public static IEndpointRouteBuilder MapNavigationEndpoints(this IEndpointRouteBuilder routes)
    {
        var me = routes.MapGroup("/me").WithTags("Navigation");
        me.MapGet("/navigation", GetAsync).WithName("GetNavigation");
        me.MapGet("/views", async (HttpContext http, PreferencesStore store, CancellationToken ct) =>
            TypedResults.Ok(await store.ViewsAsync(http.User.UserId()!.Value, ct))).WithName("ListViews");
        me.MapGet("/views/{id:guid}/items", ViewItemsAsync).WithName("GetViewItems")
            .ProducesProblem(StatusCodes.Status404NotFound);
        me.MapPost("/views/preview", PreviewAsync).WithName("PreviewView")
            .WithDescription("What a view with these rules would show, without saving it.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        var changes = me.MapGroup(string.Empty).RequireAuthorization(AuthSetup.FullSessionPolicy);
        changes.MapPut("/navigation", SaveAsync).WithName("SaveNavigation")
            .WithDescription("Replaces the person's menu layout: order, hidden entries, own names and groups.")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        changes.MapDelete("/navigation", ResetAsync).WithName("ResetNavigation")
            .WithDescription("Back to the default menu.");
        changes.MapPost("/views", CreateViewAsync).WithName("CreateView")
            .ProducesProblem(StatusCodes.Status400BadRequest);
        changes.MapPut("/views/{id:guid}", UpdateViewAsync).WithName("UpdateView")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        changes.MapDelete("/views/{id:guid}", DeleteViewAsync).WithName("DeleteView")
            .ProducesProblem(StatusCodes.Status404NotFound);

        var admin = routes.MapGroup("/admin/navigation-default")
            .WithTags("Administration")
            .RequireAuthorization(AuthSetup.AdminPolicy, AuthSetup.FullSessionPolicy);
        admin.MapGet("/", async (PreferencesStore store, CancellationToken ct) =>
            TypedResults.Ok(await store.DefaultNavigationAsync(ct) ?? NavigationLayout.Empty)).WithName("GetDefaultNavigation");
        admin.MapPut("/", async Task<Results<Ok<NavigationLayout>, ProblemHttpResult>> (NavigationLayout layout, PreferencesStore store, CancellationToken ct) =>
            {
                if (!IsValid(layout))
                {
                    return ApiProblem.Create(StatusCodes.Status400BadRequest, InvalidLayout);
                }

                await store.SaveDefaultNavigationAsync(layout, ct);
                return TypedResults.Ok(layout);
            })
            .WithName("SaveDefaultNavigation")
            .WithDescription("The menu new people start with, and everyone who has not arranged their own.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return routes;
    }

    private static async Task<Ok<NavigationView>> GetAsync(
        HttpContext http, PreferencesStore store, NodeService nodes, CancellationToken ct) =>
        TypedResults.Ok(await ResolveAsync(http, store, nodes, ct));

    private static async Task<Results<Ok<NavigationView>, ProblemHttpResult>> SaveAsync(
        NavigationLayout layout, HttpContext http, PreferencesStore store, NodeService nodes, CancellationToken ct)
    {
        if (!IsValid(layout))
        {
            return ApiProblem.Create(StatusCodes.Status400BadRequest, InvalidLayout);
        }

        await store.SaveNavigationAsync(http.User.UserId()!.Value, layout, ct);
        return TypedResults.Ok(await ResolveAsync(http, store, nodes, ct));
    }

    private static async Task<Ok<NavigationView>> ResetAsync(HttpContext http, PreferencesStore store, NodeService nodes, CancellationToken ct)
    {
        await store.ResetNavigationAsync(http.User.UserId()!.Value, ct);
        return TypedResults.Ok(await ResolveAsync(http, store, nodes, ct));
    }

    private static async Task<Results<Created<SavedView>, ProblemHttpResult>> CreateViewAsync(
        SaveViewRequest request, HttpContext http, PreferencesStore store, CancellationToken ct)
    {
        if (Normalize(request.Name) is not { } name || request.Rules is null || !request.Rules.IsValid())
        {
            return ApiProblem.Create(StatusCodes.Status400BadRequest, InvalidView);
        }

        var userId = http.User.UserId()!.Value;
        var views = await store.ViewsAsync(userId, ct);
        if (views.Count >= PreferencesStore.MaxViews)
        {
            return ApiProblem.Create(StatusCodes.Status400BadRequest, TooManyViews, new() { ["max"] = PreferencesStore.MaxViews });
        }

        var view = new SavedView(Guid.CreateVersion7(), name, request.Rules);
        views.Add(view);
        await store.SaveViewsAsync(userId, views, ct);
        return TypedResults.Created($"/api/v1/me/views/{view.Id}", view);
    }

    private static async Task<Results<Ok<SavedView>, ProblemHttpResult>> UpdateViewAsync(
        Guid id, SaveViewRequest request, HttpContext http, PreferencesStore store, CancellationToken ct)
    {
        if (Normalize(request.Name) is not { } name || request.Rules is null || !request.Rules.IsValid())
        {
            return ApiProblem.Create(StatusCodes.Status400BadRequest, InvalidView);
        }

        var userId = http.User.UserId()!.Value;
        var views = await store.ViewsAsync(userId, ct);
        var index = views.FindIndex(v => v.Id == id);
        if (index < 0)
        {
            return ApiProblem.Missing();
        }

        views[index] = new SavedView(id, name, request.Rules);
        await store.SaveViewsAsync(userId, views, ct);
        return TypedResults.Ok(views[index]);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteViewAsync(
        Guid id, HttpContext http, PreferencesStore store, CancellationToken ct)
    {
        var userId = http.User.UserId()!.Value;
        var views = await store.ViewsAsync(userId, ct);
        if (views.RemoveAll(v => v.Id == id) == 0)
        {
            return ApiProblem.Missing();
        }

        await store.SaveViewsAsync(userId, views, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<ViewPageView>, ProblemHttpResult>> ViewItemsAsync(
        Guid id, string? cursor, int? limit, HttpContext http, PreferencesStore store, PhotoService photos, SignedUrls signer, CancellationToken ct)
    {
        if (limit is < 1 or > PhotoService.MaxLimit)
        {
            return ApiProblem.Invalid();
        }

        var view = (await store.ViewsAsync(http.User.UserId()!.Value, ct)).SingleOrDefault(v => v.Id == id);
        if (view is null)
        {
            return ApiProblem.Missing();
        }

        return TypedResults.Ok(await PageAsync(http, photos, signer, view.Rules, cursor, limit ?? 100, ct));
    }

    private static async Task<Results<Ok<ViewPageView>, ProblemHttpResult>> PreviewAsync(
        ViewPreviewRequest request, HttpContext http, PhotoService photos, SignedUrls signer, CancellationToken ct)
    {
        if (request.Rules is null || !request.Rules.IsValid() || request.Limit is < 1 or > PhotoService.MaxLimit)
        {
            return ApiProblem.Create(StatusCodes.Status400BadRequest, InvalidView);
        }

        return TypedResults.Ok(await PageAsync(http, photos, signer, request.Rules, null, request.Limit ?? 24, ct));
    }

    private static async Task<ViewPageView> PageAsync(
        HttpContext http, PhotoService photos, SignedUrls signer, ViewRulesData rules, string? cursor, int limit, CancellationToken ct)
    {
        var (page, total) = await photos.ViewAsync(http.User.Actor(), rules.ToRules(), cursor, limit, ct);
        var sessionId = http.User.SessionId()!.Value;
        return new ViewPageView(page.Items.Select(i => PhotoEndpoints.ToResult(i, signer, sessionId)).ToList(), page.NextCursor, total);
    }

    /// <summary>
    /// The menu as the person sees it now: their arrangement (or the
    /// default), with entries for libraries and views that no longer exist
    /// dropped and new ones added where they belong.
    /// </summary>
    private static async Task<NavigationView> ResolveAsync(HttpContext http, PreferencesStore store, NodeService nodes, CancellationToken ct)
    {
        var photos = http.RequestServices.GetRequiredService<PhotoService>();
        var actor = http.User.Actor();
        var own = await store.NavigationAsync(actor.UserId, ct);
        var layout = own ?? await store.DefaultNavigationAsync(ct) ?? NavigationLayout.Empty;

        var available = new List<NavItemView>();
        var libraries = new List<NavItemView>();
        foreach (var library in (await nodes.ReadableLibrariesAsync(actor, ct)).Where(l => l.Kind != LibraryKind.Personal).OrderBy(l => l.Name))
        {
            var grant = await nodes.GrantAsync(actor, library, ct);
            libraries.Add(new NavItemView($"library:{library.Id:N}", "library", null, library.Name, false, null, library.RootNodeId, !NodeService.CanWrite(actor, library, grant)));
        }

        var views = (await store.ViewsAsync(actor.UserId, ct))
            .Select(v => new NavItemView($"view:{v.Id:N}", "view", null, v.Name, false, null, v.Id, false))
            .ToList();
        // Administration is an entry like the others, to be moved and
        // grouped, only for an admin who put it in the main menu (D-043);
        // otherwise it is in the menu under their name. It cannot be hidden:
        // an admin must always be able to reach it.
        var adminInMenu = layout.AdminPosition == "top" && http.User.IsInRole(nameof(UserRole.Admin));
        if (adminInMenu)
        {
            available.Add(BuiltIn("admin"));
        }

        available.Add(BuiltIn("files"));
        available.AddRange(libraries);
        available.Add(BuiltIn("photos"));
        available.AddRange(views);
        available.AddRange(BuiltIns.Skip(2).Select(BuiltIn));

        var byKey = available.ToDictionary(i => i.Key, StringComparer.Ordinal);
        var groups = layout.Groups.ToList();
        var groupIds = groups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        var result = new List<NavItemView>();
        foreach (var entry in layout.Entries)
        {
            var item = byKey.Remove(entry.Key, out var known) ? known : await PinnedAsync(entry.Key, actor, nodes, photos, ct);
            if (item is not null)
            {
                result.Add(item with
                {
                    Hidden = entry.Hidden && item.Kind != "admin",
                    Label = entry.Label,
                    Group = entry.Group is { } g && groupIds.Contains(g) ? g : null,
                    Icon = entry.Icon,
                });
            }
        }

        // Anything new (a library just shared, a view just made) appears in its usual place.
        foreach (var item in available.Where(i => byKey.ContainsKey(i.Key)))
        {
            var after = DefaultNeighbour(available, item, result);
            result.Insert(after, item);
        }

        return new NavigationView(
            result,
            groups,
            own is not null,
            layout.Density == "compact" ? "compact" : "comfortable",
            layout.AdminPosition == "top" ? "top" : "bottom");
    }

    /// <summary>
    /// A pinned folder or album, or a separator line. A pin to something the
    /// person can no longer see (gone, in the trash, access withdrawn) is
    /// left out, never shown as a broken entry.
    /// </summary>
    private static async Task<NavItemView?> PinnedAsync(string key, FileActor actor, NodeService nodes, PhotoService photos, CancellationToken ct)
    {
        var colon = key.IndexOf(':');
        if (colon < 0 || !Guid.TryParseExact(key[(colon + 1)..], "N", out var id))
        {
            return null;
        }

        switch (key[..colon])
        {
            case "separator":
                return new NavItemView(key, "separator", null, null, false, null, null, false);
            case "folder":
                return await nodes.FindAsync(actor, id, ct) is { Node.IsFolder: true, InTrash: false } found
                    ? new NavItemView(key, "folder", null, found.Node.Name, false, null, id, !found.CanWrite)
                    : null;
            case "album":
                return await photos.AlbumAsync(actor, id, ct) is { } album
                    ? new NavItemView(key, "album", null, album.Name, false, null, id, false)
                    : null;
            default:
                return null;
        }
    }

    /// <summary>Where a new entry goes: after the entry that comes before it in the default order, or at the end.</summary>
    private static int DefaultNeighbour(List<NavItemView> defaults, NavItemView item, List<NavItemView> result)
    {
        for (var i = defaults.IndexOf(item) - 1; i >= 0; i--)
        {
            var at = result.FindIndex(r => r.Key == defaults[i].Key);
            if (at >= 0)
            {
                return at + 1;
            }
        }

        return result.Count;
    }

    private static NavItemView BuiltIn(string key) => new(key, key, null, null, false, null, null, false);

    private static bool IsValid(NavigationLayout? layout)
    {
        if (layout?.Entries is null || layout.Groups is null || layout.Entries.Count > 500 || layout.Groups.Count > 20
            || layout.Density is not (null or "comfortable" or "compact")
            || layout.AdminPosition is not (null or "top" or "bottom"))
        {
            return false;
        }

        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in layout.Groups)
        {
            if (group.Id is not { Length: > 0 and <= 64 } || Normalize(group.Name, 64) is null || !groupIds.Add(group.Id))
            {
                return false;
            }
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        return layout.Entries.All(e =>
            e.Key is not null
            && EntryKey().IsMatch(e.Key)
            && keys.Add(e.Key)
            && (e.Label is null || Normalize(e.Label, 64) is not null)
            && (e.Icon is null || IconName().IsMatch(e.Icon))
            && (e.Group is null || groupIds.Contains(e.Group)));
    }

    private static string? Normalize(string? name, int max = 128)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > max || trimmed.Any(char.IsControl) ? null : trimmed;
    }

    [GeneratedRegex("^(admin|files|photos|trash|devices|account|(library|view|folder|album|separator):[0-9a-f]{32})$")]
    private static partial Regex EntryKey();

    [GeneratedRegex("^[a-zA-Z]{1,32}$")]
    private static partial Regex IconName();
}
