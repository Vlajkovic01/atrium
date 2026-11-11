using System.Collections.Concurrent;

namespace Atrium.Identity.Sessions;

/// <summary>
/// Slows down guessing (DESIGN.md 6.1). Each key (an address, an account,
/// a pairing code) gets a number of free failures; after that every further
/// failure blocks the key for a period that doubles, up to a cap. In memory:
/// there is one server process, and losing the counters on restart only
/// forgives attempts.
/// </summary>
public sealed class AttemptLimiter(TimeProvider time)
{
    private static readonly TimeSpan FirstBlock = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBlock = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(1);
    private const int MaxEntries = 50_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>How much longer the key is blocked, or null if an attempt may proceed.</summary>
    public TimeSpan? BlockedFor(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        var remaining = entry.BlockedUntil - time.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    public void RecordFailure(string key, int freeAttempts)
    {
        var now = time.GetUtcNow();
        _entries.AddOrUpdate(
            key,
            _ => Next(new Entry(0, DateTimeOffset.MinValue, now), freeAttempts, now),
            (_, existing) => Next(now - existing.LastFailure > ForgetAfter ? new Entry(0, DateTimeOffset.MinValue, now) : existing, freeAttempts, now));

        if (_entries.Count > MaxEntries)
        {
            Prune(now);
        }
    }

    public void RecordSuccess(string key) => _entries.TryRemove(key, out _);

    private static Entry Next(Entry entry, int freeAttempts, DateTimeOffset now)
    {
        var failures = entry.Failures + 1;
        var over = failures - freeAttempts;
        if (over <= 0)
        {
            return new Entry(failures, DateTimeOffset.MinValue, now);
        }

        // 30 s, 1 min, 2 min, ... capped. The shift is bounded so it cannot overflow.
        var block = TimeSpan.FromTicks(Math.Min(MaxBlock.Ticks, FirstBlock.Ticks << Math.Min(over - 1, 10)));
        return new Entry(failures, now + block, now);
    }

    // Under a flood of distinct keys, keep only what still matters: keys that
    // are blocked right now. Forgetting a few counters is better than
    // letting an attacker grow this map without bound.
    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries)
        {
            if (entry.BlockedUntil <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private sealed record Entry(int Failures, DateTimeOffset BlockedUntil, DateTimeOffset LastFailure);
}
