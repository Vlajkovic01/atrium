using System.Security.Cryptography;
using System.Text.Json;
using Atrium.Core.Files;
using Atrium.Core.Jobs;
using Atrium.Core.Settings;
using Atrium.Data;
using Atrium.Files.Storage;
using Atrium.Jobs;
using Atrium.Storage;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Files.Blobs;

public enum IntegrityProblem
{
    /// <summary>The database has the blob; the storage does not.</summary>
    Missing,

    /// <summary>The content no longer hashes to its name: it was damaged on storage.</summary>
    Damaged,

    /// <summary>An object under the blob folder that the database does not know.</summary>
    Unknown,

    /// <summary>The location could not be reached or did not prove its identity.</summary>
    LocationUnavailable,
}

/// <summary>One finding. Blob keys are content hashes, never file names (D-004).</summary>
public sealed record IntegrityIssue(IntegrityProblem Problem, Guid LocationId, string? Key, Guid? BlobId);

public sealed record IntegrityReport(
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long BlobsChecked,
    long BytesChecked,
    long Missing,
    long Damaged,
    long Unknown,
    IReadOnlyList<IntegrityIssue> Issues);

/// <summary>
/// Reads every blob back, hashes it and compares it with its name, then
/// looks for objects on storage that the database does not know
/// (DESIGN.md 4.5). It only reports: it never repairs or deletes anything,
/// because the right repair depends on what went wrong. Progress is saved
/// as a checkpoint, so a restart continues where it stopped.
/// </summary>
public sealed class ScrubHandler(
    AtriumDbContext db, StorageProviders providers, TimeProvider time, Atrium.Data.Notifications.NotificationStore notifications) : IJobHandler
{
    public const string Type = "storage.scrub";
    public const string ReportSettingKey = "integrity.last_report";

    /// <summary>At most this many findings are listed; the counts are always complete.</summary>
    public const int MaxListedIssues = 200;

    public static readonly RecurringJob Schedule = new(Type, TimeSpan.FromDays(7), JobPriority.Idle, JobResourceClass.Io);

    private const int BatchSize = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var state = context.Checkpoint is { } saved
            ? JsonSerializer.Deserialize<State>(saved, Json)!
            : new State(time.GetUtcNow(), Guid.Empty, 0, 0, 0, 0, 0, []);
        var total = await db.Blobs.LongCountAsync(ct);
        var unreachable = new HashSet<Guid>();

        while (true)
        {
            var after = state.AfterBlobId;
            var batch = await db.Blobs.AsNoTracking()
                .Where(b => b.Id.CompareTo(after) > 0)
                .OrderBy(b => b.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var blob in batch)
            {
                state = await CheckAsync(blob, state, unreachable, ct);
            }

            state = state with { AfterBlobId = batch[^1].Id };
            await context.SaveCheckpointAsync(JsonSerializer.Serialize(state, Json), ct);
            await context.ReportProgressAsync(total == 0 ? 0.9 : Math.Min(0.9, 0.9 * state.BlobsChecked / total), ct);
        }

        state = await FindUnknownAsync(state, unreachable, ct);
        var report = new IntegrityReport(
            state.StartedAt, time.GetUtcNow(), state.BlobsChecked, state.BytesChecked, state.Missing, state.Damaged, state.Unknown, state.Issues);
        await SaveReportAsync(report, ct);

        // Damage is found while a backup can still help, so admins hear of it at once (D-045).
        if (report.Missing + report.Damaged > 0)
        {
            await notifications.AddForAdminsAsync(
                "integrity.problems",
                new Dictionary<string, string> { ["count"] = (report.Missing + report.Damaged).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                "/admin/backups",
                ct);
        }
    }

    private async Task<State> CheckAsync(Blob blob, State state, HashSet<Guid> unreachable, CancellationToken ct)
    {
        if (unreachable.Contains(blob.StorageLocationId))
        {
            return state;
        }

        IStorageProvider provider;
        try
        {
            provider = await providers.OpenAsync(db, blob.StorageLocationId, ct);
        }
        catch (LocationUnavailableException)
        {
            unreachable.Add(blob.StorageLocationId);
            return state.Add(new IntegrityIssue(IntegrityProblem.LocationUnavailable, blob.StorageLocationId, null, null));
        }

        try
        {
            await using var stream = await provider.OpenReadAsync(blob.Key, null, ct);
            using var hashing = new HashingStream(stream);
            await hashing.CopyToAsync(Stream.Null, ct);
            var checkedState = state with { BlobsChecked = state.BlobsChecked + 1, BytesChecked = state.BytesChecked + hashing.BytesRead };
            return hashing.BytesRead == blob.Size && CryptographicOperations.FixedTimeEquals(hashing.GetHash(), blob.Sha256)
                ? checkedState
                : (checkedState with { Damaged = checkedState.Damaged + 1 })
                    .Add(new IntegrityIssue(IntegrityProblem.Damaged, blob.StorageLocationId, blob.Key, blob.Id));
        }
        catch (StorageObjectNotFoundException)
        {
            return (state with { BlobsChecked = state.BlobsChecked + 1, Missing = state.Missing + 1 })
                .Add(new IntegrityIssue(IntegrityProblem.Missing, blob.StorageLocationId, blob.Key, blob.Id));
        }
    }

    private async Task<State> FindUnknownAsync(State state, HashSet<Guid> unreachable, CancellationToken ct)
    {
        var locations = await db.StorageLocations.AsNoTracking().Select(l => l.Id).ToListAsync(ct);
        foreach (var locationId in locations.Where(l => !unreachable.Contains(l)))
        {
            IStorageProvider provider;
            try
            {
                provider = await providers.OpenAsync(db, locationId, ct);
            }
            catch (LocationUnavailableException)
            {
                state = state.Add(new IntegrityIssue(IntegrityProblem.LocationUnavailable, locationId, null, null));
                continue;
            }

            var pending = new List<(string Key, byte[] Hash)>();
            async Task<State> FlushAsync(State current)
            {
                var hashes = pending.Select(p => p.Hash).ToList();
                var known = (await db.Blobs.AsNoTracking()
                        .Where(b => b.StorageLocationId == locationId && hashes.Contains(b.Sha256))
                        .Select(b => b.Sha256)
                        .ToListAsync(ct))
                    .Select(Convert.ToHexStringLower)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var (key, hash) in pending.Where(p => !known.Contains(Convert.ToHexStringLower(p.Hash))))
                {
                    current = (current with { Unknown = current.Unknown + 1 })
                        .Add(new IntegrityIssue(IntegrityProblem.Unknown, locationId, key, null));
                }

                pending.Clear();
                return current;
            }

            await foreach (var item in provider.ListAsync("atrium/blobs/", ct))
            {
                var name = item.Key[(item.Key.LastIndexOf('/') + 1)..];
                byte[] hash;
                try
                {
                    hash = name.Length == 64 ? Convert.FromHexString(name) : [];
                }
                catch (FormatException)
                {
                    hash = [];
                }

                if (hash.Length != 32 || Blob.KeyFor(hash) != item.Key)
                {
                    state = (state with { Unknown = state.Unknown + 1 }).Add(new IntegrityIssue(IntegrityProblem.Unknown, locationId, item.Key, null));
                    continue;
                }

                pending.Add((item.Key, hash));
                if (pending.Count >= 500)
                {
                    state = await FlushAsync(state);
                }
            }

            state = await FlushAsync(state);
        }

        return state;
    }

    private async Task SaveReportAsync(IntegrityReport report, CancellationToken ct)
    {
        var value = JsonSerializer.Serialize(report, Json);
        var now = time.GetUtcNow();
        var updated = await db.Settings.Where(s => s.Key == ReportSettingKey)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, value).SetProperty(x => x.UpdatedAt, now), ct);
        if (updated == 0)
        {
            db.Settings.Add(new Setting { Key = ReportSettingKey, Value = value, UpdatedAt = now });
            await db.SaveChangesAsync(ct);
        }
    }

    public static async Task<IntegrityReport?> LastReportAsync(AtriumDbContext db, CancellationToken ct)
    {
        var setting = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == ReportSettingKey, ct);
        return setting is null ? null : JsonSerializer.Deserialize<IntegrityReport>(setting.Value, Json);
    }

    private sealed record State(
        DateTimeOffset StartedAt,
        Guid AfterBlobId,
        long BlobsChecked,
        long BytesChecked,
        long Missing,
        long Damaged,
        long Unknown,
        List<IntegrityIssue> Issues)
    {
        public State Add(IntegrityIssue issue)
        {
            if (Issues.Count < MaxListedIssues)
            {
                Issues.Add(issue);
            }

            return this;
        }
    }
}
