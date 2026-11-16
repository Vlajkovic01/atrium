using System.Text.Json;
using Atrium.Core.Settings;
using Atrium.Data;
using Atrium.Media.Photos;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Preferences;

/// <summary>One entry of the menu as a person arranged it.</summary>
/// <param name="Key">
/// <c>files</c>, <c>photos</c>, <c>trash</c>, <c>devices</c>, <c>account</c>,
/// <c>library:&lt;id&gt;</c> or <c>view:&lt;id&gt;</c>.
/// </param>
/// <param name="Label">The person's own name for it; null for the usual one.</param>
/// <param name="Group">The group it is listed under; null for none.</param>
public sealed record NavEntry(string Key, bool Hidden, string? Label, string? Group);

public sealed record NavGroup(string Id, string Name);

/// <summary>The order of the menu, what is hidden, renamed or grouped.</summary>
public sealed record NavigationLayout(IReadOnlyList<NavEntry> Entries, IReadOnlyList<NavGroup> Groups)
{
    public static NavigationLayout Empty { get; } = new([], []);
}

/// <summary>The rules of a view as the client sends and receives them.</summary>
public sealed record ViewRulesData(
    string? Words,
    IReadOnlyList<SearchType>? Types,
    IReadOnlyList<string>? Extensions,
    IReadOnlyList<Guid>? LibraryIds,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int? LastDays,
    long? MinSize,
    long? MaxSize,
    ViewSort? Sort)
{
    public ViewRules ToRules() => new(
        Words,
        Types ?? [],
        Extensions ?? [],
        LibraryIds ?? [],
        From is { } f ? new DateTimeOffset(f.DateTime, TimeSpan.Zero) : null,
        To is { } t ? new DateTimeOffset(t.DateTime, TimeSpan.Zero) : null,
        LastDays,
        MinSize,
        MaxSize,
        Sort ?? ViewSort.Newest);

    /// <summary>Within limits a person can reasonably mean; anything else is refused.</summary>
    public bool IsValid() =>
        (Words is null || Words.Length <= 200)
        && (Types is null || (Types.Count <= 4 && Types.All(Enum.IsDefined)))
        && (Extensions is null || (Extensions.Count <= 20 && Extensions.All(e => e.Trim().TrimStart('.') is { Length: > 0 and <= 16 } x && x.All(char.IsLetterOrDigit))))
        && (LibraryIds is null || LibraryIds.Count <= 100)
        && LastDays is null or (>= 1 and <= 36500)
        && MinSize is null or >= 0
        && MaxSize is null or >= 0
        && (Sort is null || Enum.IsDefined(Sort.Value));
}

/// <summary>A saved view: a name and rules, shown as an entry of the menu.</summary>
public sealed record SavedView(Guid Id, string Name, ViewRulesData Rules);

/// <summary>
/// A person's menu layout and saved views (D-041). Kept as settings rows
/// that only their owner can read or write through the API; removed with
/// the account.
/// </summary>
public sealed class PreferencesStore(AtriumDbContext db, TimeProvider time)
{
    public const string DefaultNavigationKey = "prefs.nav.default";
    public const int MaxViews = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string NavigationKey(Guid userId) => UserPreferenceKeys.Navigation(userId);

    public static string ViewsKey(Guid userId) => UserPreferenceKeys.Views(userId);

    public async Task<NavigationLayout?> NavigationAsync(Guid userId, CancellationToken ct) =>
        await ReadAsync<NavigationLayout>(NavigationKey(userId), ct);

    public async Task<NavigationLayout?> DefaultNavigationAsync(CancellationToken ct) =>
        await ReadAsync<NavigationLayout>(DefaultNavigationKey, ct);

    public Task SaveNavigationAsync(Guid userId, NavigationLayout layout, CancellationToken ct) =>
        WriteAsync(NavigationKey(userId), layout, ct);

    public Task SaveDefaultNavigationAsync(NavigationLayout layout, CancellationToken ct) =>
        WriteAsync(DefaultNavigationKey, layout, ct);

    public Task ResetNavigationAsync(Guid userId, CancellationToken ct) =>
        db.Settings.Where(s => s.Key == NavigationKey(userId)).ExecuteDeleteAsync(ct);

    public async Task<List<SavedView>> ViewsAsync(Guid userId, CancellationToken ct) =>
        await ReadAsync<List<SavedView>>(ViewsKey(userId), ct) ?? [];

    public Task SaveViewsAsync(Guid userId, List<SavedView> views, CancellationToken ct) =>
        WriteAsync(ViewsKey(userId), views, ct);

    private async Task<T?> ReadAsync<T>(string key, CancellationToken ct)
        where T : class
    {
        var value = await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).SingleOrDefaultAsync(ct);
        if (value is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(value, Json);
        }
        catch (JsonException)
        {
            // Unreadable preferences are dropped, never fatal: the defaults apply.
            return null;
        }
    }

    private async Task WriteAsync<T>(string key, T value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, Json);
        var now = time.GetUtcNow();
        var updated = await db.Settings.Where(s => s.Key == key)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, json).SetProperty(x => x.UpdatedAt, now), ct);
        if (updated == 0)
        {
            db.Settings.Add(new Setting { Key = key, Value = json, UpdatedAt = now });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                await db.Settings.Where(s => s.Key == key)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, json).SetProperty(x => x.UpdatedAt, now), ct);
            }
        }
    }
}
