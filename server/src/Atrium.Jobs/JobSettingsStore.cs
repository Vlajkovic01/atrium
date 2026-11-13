using System.Text.Json;
using Atrium.Core.Jobs;
using Atrium.Core.Settings;
using Atrium.Data;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Jobs;

/// <summary>
/// Keeps the admin's job limits and pause in the settings table, so they
/// survive a restart, and applies them to <see cref="JobLimits"/>.
/// </summary>
public sealed class JobSettingsStore(AtriumDbContext db, JobLimits limits, TimeProvider time)
{
    public const string LimitsKey = "jobs.limits";
    public const string PauseKey = "jobs.pause";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Applies what is stored. Values that no longer make sense are ignored, never fatal.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var stored = await db.Settings.AsNoTracking()
            .Where(s => s.Key == LimitsKey || s.Key == PauseKey)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        if (stored.TryGetValue(LimitsKey, out var limitsJson) && Read<Dictionary<string, int>>(limitsJson) is { } byName)
        {
            var overrides = new Dictionary<JobResourceClass, int>();
            foreach (var (name, limit) in byName)
            {
                if (Enum.TryParse<JobResourceClass>(name, ignoreCase: true, out var resourceClass) && limit is >= 1 and <= JobLimits.MaxLimit)
                {
                    overrides[resourceClass] = limit;
                }
            }

            limits.SetOverrides(overrides);
        }

        if (stored.TryGetValue(PauseKey, out var pauseJson) && Read<StoredPause>(pauseJson) is { } pause)
        {
            DateTimeOffset? until = pause.UntilMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
            if (until is null || until > time.GetUtcNow())
            {
                limits.PauseBackground(until);
            }
        }
    }

    public async Task SaveLimitsAsync(IReadOnlyDictionary<JobResourceClass, int> overrides, CancellationToken ct)
    {
        limits.SetOverrides(overrides);
        var byName = overrides.ToDictionary(o => o.Key.ToString(), o => o.Value);
        await WriteAsync(LimitsKey, JsonSerializer.Serialize(byName, Json), ct);
    }

    public async Task PauseAsync(DateTimeOffset? until, CancellationToken ct)
    {
        await WriteAsync(PauseKey, JsonSerializer.Serialize(new StoredPause(until?.ToUnixTimeMilliseconds()), Json), ct);
        limits.PauseBackground(until);
    }

    public async Task ResumeAsync(CancellationToken ct)
    {
        await db.Settings.Where(s => s.Key == PauseKey).ExecuteDeleteAsync(ct);
        limits.ResumeBackground();
    }

    private async Task WriteAsync(string key, string value, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var updated = await db.Settings.Where(s => s.Key == key)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, value).SetProperty(x => x.UpdatedAt, now), ct);
        if (updated == 0)
        {
            db.Settings.Add(new Setting { Key = key, Value = value, UpdatedAt = now });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Written by a parallel request; this one wins.
                db.ChangeTracker.Clear();
                await db.Settings.Where(s => s.Key == key)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, value).SetProperty(x => x.UpdatedAt, now), ct);
            }
        }
    }

    private static T? Read<T>(string json)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record StoredPause(long? UntilMs);
}
