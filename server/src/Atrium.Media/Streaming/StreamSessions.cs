using System.Collections.Concurrent;
using System.Security.Cryptography;
using Atrium.Core.Media;
using Atrium.Jobs;
using Atrium.Media.Playback;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Atrium.Media.Streaming;

/// <summary>Limits on streaming, all configurable (DESIGN.md 7.4).</summary>
/// <param name="MaxEncodes">Streams whose video is encoded again, at once on the server.</param>
/// <param name="MaxCopies">Streams whose video is copied (remux, audio transcode), at once on the server.</param>
/// <param name="MaxPerUser">Streams one person has at once.</param>
/// <param name="MaxCacheBytes">What all streams together keep in the cache location.</param>
public sealed record StreamLimits(int MaxEncodes, int MaxCopies, int MaxPerUser, long MaxCacheBytes = StreamLimits.DefaultCacheBytes)
{
    public const string MaxEncodesKey = "ATRIUM_MAX_TRANSCODES";

    public const string CacheKey = "ATRIUM_STREAM_CACHE_GB";

    public const long DefaultCacheBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>
    /// By the machine (D-003): a small device encodes one video at a time,
    /// larger ones one per four cores. Copying costs little and gets more.
    /// </summary>
    public static StreamLimits For(HardwareInfo hardware)
    {
        var encodes = hardware.Profile == HardwareProfile.Small ? 1 : Math.Max(1, hardware.ProcessorCount / 4);
        if (int.TryParse(Environment.GetEnvironmentVariable(MaxEncodesKey), out var configured) && configured >= 0)
        {
            encodes = configured;
        }

        var cache = double.TryParse(Environment.GetEnvironmentVariable(CacheKey), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gb) && gb > 0
            ? (long)(gb * 1024 * 1024 * 1024)
            : DefaultCacheBytes;
        return new StreamLimits(encodes, Math.Max(4, encodes * 4), 3, cache);
    }
}

/// <summary>Why a stream was refused before it started.</summary>
public enum StreamRefusal
{
    /// <summary>The server encodes or copies as many streams as its limits allow.</summary>
    ServerBusy,

    /// <summary>The person has as many streams as one person may.</summary>
    TooManyForUser,
}

/// <summary>
/// The streams in progress (DESIGN.md 7.4, D-062). In memory: a restart
/// ends them, and players ask again. Each is named by a random identifier
/// that is its capability in stream URLs, kept only for the session that
/// opened it.
/// </summary>
public sealed class StreamSessions(MediaOptions options, StreamLimits limits, TimeProvider time, ILoggerFactory loggers) : IAsyncDisposable
{
    /// <summary>A stream nobody asked anything of for this long stops its FFmpeg.</summary>
    public static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(60);

    /// <summary>A stream nobody asked anything of for this long is forgotten with its segments.</summary>
    public static readonly TimeSpan IdleEnd = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, StreamSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _opening = new(1, 1);

    public string Directory => Path.Combine(options.CacheDir, "hls");

    public StreamLimits Limits => limits;

    public StreamSession? Find(string id) => _sessions.TryGetValue(id, out var session) ? session : null;

    public IReadOnlyCollection<StreamSession> All => _sessions.Values.ToList();

    /// <summary>Opens a stream, or says why not, before any FFmpeg starts.</summary>
    /// <param name="input">Gives FFmpeg's input when a run starts: a local path or a loopback URL.</param>
    public async Task<(StreamSession? Session, StreamRefusal? Refusal)> OpenAsync(
        Guid userId,
        Guid authSessionId,
        Guid nodeId,
        PlaybackDecision decision,
        StreamOutput output,
        IReadOnlyList<Segment> segments,
        MediaStream video,
        MediaStream? audio,
        int startIndex,
        Func<CancellationToken, Task<string>> input)
    {
        await _opening.WaitAsync();
        try
        {
            var open = _sessions.Values.ToList();
            if (open.Count(s => s.UserId == userId) >= limits.MaxPerUser)
            {
                // The oldest of the person's streams makes room: usually a player closed without saying so.
                var oldest = open.Where(s => s.UserId == userId).MinBy(s => s.LastUsed)!;
                if (time.GetUtcNow() - oldest.LastUsed < IdleStop)
                {
                    return (null, StreamRefusal.TooManyForUser);
                }

                await CloseAsync(oldest.Id);
                open.Remove(oldest);
            }

            // Over the cache's limit, idle streams give way, oldest first.
            var size = open.Sum(s => s.SizeOnDisk());
            foreach (var idle in open.Where(s => time.GetUtcNow() - s.LastUsed >= IdleStop).OrderBy(s => s.LastUsed).ToList())
            {
                if (size <= limits.MaxCacheBytes)
                {
                    break;
                }

                size -= idle.SizeOnDisk();
                await CloseAsync(idle.Id);
                open.Remove(idle);
            }

            if (size > limits.MaxCacheBytes)
            {
                return (null, StreamRefusal.ServerBusy);
            }

            var encodes = !output.CopyVideo;
            var alike = open.Count(s => s.Encodes == encodes);
            if (alike >= (encodes ? limits.MaxEncodes : limits.MaxCopies))
            {
                return (null, StreamRefusal.ServerBusy);
            }

            var id = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(18));
            var session = new StreamSession(
                id,
                userId,
                authSessionId,
                nodeId,
                decision,
                output,
                segments,
                video,
                audio,
                Path.Combine(Directory, id),
                startIndex,
                options.Ffmpeg,
                input,
                time,
                loggers.CreateLogger<StreamSession>());
            _sessions[id] = session;
            return (session, null);
        }
        finally
        {
            _opening.Release();
        }
    }

    public async Task<bool> CloseAsync(string id)
    {
        if (!_sessions.TryRemove(id, out var session))
        {
            return false;
        }

        await session.DisposeAsync();
        return true;
    }

    /// <summary>Stops FFmpeg of idle streams and forgets the long idle ones; run every few seconds.</summary>
    public async Task SweepAsync()
    {
        var now = time.GetUtcNow();
        foreach (var session in _sessions.Values.ToList())
        {
            var idle = now - session.LastUsed;
            if (idle >= IdleEnd)
            {
                await CloseAsync(session.Id);
            }
            else if (idle >= IdleStop && session.Running)
            {
                await session.StopAsync();
            }
        }
    }

    /// <summary>Segments of a stream from before a restart belong to no stream; they are derived data and go.</summary>
    public void RemoveLeftovers()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return;
        }

        foreach (var folder in System.IO.Directory.GetDirectories(Directory))
        {
            if (!_sessions.ContainsKey(Path.GetFileName(folder)))
            {
                try
                {
                    System.IO.Directory.Delete(folder, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList())
        {
            await CloseAsync(id);
        }

        _opening.Dispose();
    }
}

/// <summary>Sweeps idle streams every few seconds and clears what an earlier run of the server left.</summary>
public sealed class StreamSweeper(StreamSessions sessions, ILogger<StreamSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        sessions.RemoveLeftovers();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await sessions.SweepAsync();
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
                logger.LogWarning(e, "Sweeping idle streams failed; trying again shortly.");
            }
        }
    }
}
