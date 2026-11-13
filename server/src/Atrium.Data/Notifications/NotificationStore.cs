using System.Collections.Concurrent;
using System.Text.Json;
using Atrium.Core.Identity;
using Atrium.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Data.Notifications;

/// <param name="Kind">What happened, for the client to word, such as <c>uploads.finished</c>.</param>
/// <param name="Params">Values the wording needs: a count, a folder's name. Never file contents.</param>
/// <param name="Link">Where the client goes when the notification is opened: a path inside the app.</param>
public sealed record Notification(
    Guid Id,
    string Kind,
    IReadOnlyDictionary<string, string> Params,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    string? Link);

/// <summary>
/// Each person's notifications (D-045): kept on the server so every device
/// shows the same list and what is unread. Stored as one settings row per
/// person, the newest <see cref="Max"/> for <see cref="Keep"/>, so it needs
/// no table of its own. Removed with the account.
/// </summary>
public sealed class NotificationStore(AtriumDbContext db, TimeProvider time)
{
    public const int Max = 200;
    public static readonly TimeSpan Keep = TimeSpan.FromDays(90);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // One writer per person at a time: a read, a change and a write of the
    // same row must not interleave with another request's.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    public async Task<IReadOnlyList<Notification>> ListAsync(Guid userId, CancellationToken ct) =>
        Fresh(await ReadAsync(userId, ct));

    public Task AddAsync(Guid userId, string kind, IReadOnlyDictionary<string, string>? values, string? link, CancellationToken ct) =>
        ChangeAsync(userId, list =>
        {
            list.Insert(0, new Notification(Guid.CreateVersion7(), kind, values ?? new Dictionary<string, string>(), time.GetUtcNow(), null, link));
            return true;
        }, ct);

    /// <summary>Tells every active admin, for things only an admin can act on.</summary>
    public async Task AddForAdminsAsync(string kind, IReadOnlyDictionary<string, string>? values, string? link, CancellationToken ct)
    {
        var admins = await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Admin && !u.Disabled)
            .Select(u => u.Id)
            .ToListAsync(ct);
        foreach (var admin in admins)
        {
            await AddAsync(admin, kind, values, link, ct);
        }
    }

    /// <summary>Marks these as read, or all when <paramref name="ids"/> is null.</summary>
    public Task MarkReadAsync(Guid userId, IReadOnlyCollection<Guid>? ids, CancellationToken ct) =>
        ChangeAsync(userId, list =>
        {
            var now = time.GetUtcNow();
            var changed = false;
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].ReadAt is null && (ids is null || ids.Contains(list[i].Id)))
                {
                    list[i] = list[i] with { ReadAt = now };
                    changed = true;
                }
            }

            return changed;
        }, ct);

    /// <summary>Removes one, or all when <paramref name="id"/> is null.</summary>
    public Task RemoveAsync(Guid userId, Guid? id, CancellationToken ct) =>
        ChangeAsync(userId, list => id is null ? list.RemoveAll(_ => true) > 0 : list.RemoveAll(n => n.Id == id) > 0, ct);

    private List<Notification> Fresh(List<Notification> list)
    {
        var oldest = time.GetUtcNow() - Keep;
        return list.Where(n => n.CreatedAt >= oldest).OrderByDescending(n => n.CreatedAt).Take(Max).ToList();
    }

    private async Task ChangeAsync(Guid userId, Func<List<Notification>, bool> change, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var list = Fresh(await ReadAsync(userId, ct));
            if (change(list))
            {
                await WriteAsync(userId, Fresh(list), ct);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<List<Notification>> ReadAsync(Guid userId, CancellationToken ct)
    {
        var key = UserPreferenceKeys.Notifications(userId);
        var value = await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).SingleOrDefaultAsync(ct);
        if (value is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Notification>>(value, Json) ?? [];
        }
        catch (JsonException)
        {
            // An unreadable list starts again empty rather than failing every request.
            return [];
        }
    }

    private async Task WriteAsync(Guid userId, List<Notification> list, CancellationToken ct)
    {
        var key = UserPreferenceKeys.Notifications(userId);
        var json = JsonSerializer.Serialize(list, Json);
        var now = time.GetUtcNow();
        var updated = await db.Settings.Where(s => s.Key == key)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, json).SetProperty(x => x.UpdatedAt, now), ct);
        if (updated == 0)
        {
            db.Settings.Add(new Setting { Key = key, Value = json, UpdatedAt = now });
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }
}
