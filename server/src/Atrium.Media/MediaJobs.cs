using Atrium.Core.Files;
using Atrium.Core.Jobs;
using Atrium.Core.Media;
using Atrium.Data;
using Atrium.Files;
using Atrium.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Atrium.Media;

public sealed record MediaNodePayload(Guid NodeId);

/// <summary>Reads one file's metadata and makes its grid thumbnail. CPU work, in the background.</summary>
public sealed class MediaExtractHandler(MediaService media, JobQueue queue, AtriumDbContext db) : IJobHandler
{
    public const string Type = "media.extract";

    public static JobRequest Request(Guid nodeId, JobPriority priority = JobPriority.Background) => new(Type)
    {
        Payload = new MediaNodePayload(nodeId),
        Priority = priority,
        ResourceClass = JobResourceClass.Cpu,
        DedupeKey = $"{Type}:{nodeId}",
        MaxAttempts = 3,
    };

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var nodeId = context.GetPayload<MediaNodePayload>().NodeId;
        await media.ProcessAsync(nodeId, background: true, ct);

        // A video also gets a short silent preview, when nothing else is waiting.
        if (await db.Nodes.AnyAsync(n => n.Id == nodeId && n.MediaKind == MediaKind.Video, ct))
        {
            await queue.EnqueueAsync(MediaPreviewHandler.Request(nodeId), ct);
        }
    }
}

/// <summary>Makes a video's preview clip. Idle priority: it is a nicety.</summary>
public sealed class MediaPreviewHandler(MediaService media, AtriumDbContext db) : IJobHandler
{
    public const string Type = "media.preview";

    public static JobRequest Request(Guid nodeId) => new(Type)
    {
        Payload = new MediaNodePayload(nodeId),
        Priority = JobPriority.Idle,
        ResourceClass = JobResourceClass.Cpu,
        DedupeKey = $"{Type}:{nodeId}",
        MaxAttempts = 2,
    };

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var nodeId = context.GetPayload<MediaNodePayload>().NodeId;
        var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == nodeId, ct);
        if (node is not { MediaKind: MediaKind.Video, MissingSince: null })
        {
            return;
        }

        var library = await db.Libraries.AsNoTracking().SingleAsync(l => l.Id == node.LibraryId, ct);
        await media.ThumbnailAsync(node, library, ThumbnailVariant.Preview, background: true, ct);
    }
}

/// <summary>
/// Finds photos and videos whose metadata was never read, or was read by an
/// older version, and queues them. Catches whatever an observer missed, a
/// restored backup and a deleted cache alike; runs hourly.
/// </summary>
public sealed class MediaSweepHandler(AtriumDbContext db, JobQueue queue, MediaOptions options, TimeProvider time, ILogger<MediaSweepHandler> logger) : IJobHandler
{
    public const string Type = "media.sweep";

    /// <summary>At most this many files are queued per run, so the queue stays short.</summary>
    public const int Batch = 2000;

    public static readonly RecurringJob Schedule = new(Type, TimeSpan.FromHours(1), JobPriority.Idle, JobResourceClass.Io);

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        RemoveStaleTemporaryFiles();

        var current = MediaInfo.CurrentExtractorVersion;
        var pending = await db.Nodes.AsNoTracking()
            .Where(n => n.MediaKind != null && n.MissingSince == null && !n.InTrash)
            .Where(n => n.BlobId != null
                ? !db.MediaInfos.Any(m => m.BlobId == n.BlobId && m.ExtractorVersion >= current)
                : !db.MediaInfos.Any(m => m.NodeId == n.Id && m.ExtractorVersion >= current && m.SourceSize == n.Size))
            .OrderByDescending(n => n.CapturedAt)
            .Select(n => n.Id)
            .Take(Batch)
            .ToListAsync(ct);
        foreach (var id in pending)
        {
            await queue.EnqueueAsync(MediaExtractHandler.Request(id), ct);
        }

        if (pending.Count > 0)
        {
            logger.LogInformation("Queued {Count} photos and videos for metadata and thumbnails.", pending.Count);
        }
    }

    /// <summary>Copies made for tools are deleted after use; one left by a crash is removed here.</summary>
    private void RemoveStaleTemporaryFiles()
    {
        if (!Directory.Exists(options.TempDir))
        {
            return;
        }

        var before = time.GetUtcNow().UtcDateTime.AddHours(-6);
        foreach (var file in Directory.EnumerateFiles(options.TempDir))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < before)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>Queues metadata and thumbnails for new and changed photos and videos.</summary>
public sealed class MediaContentObserver(JobQueue queue) : INodeContentObserver
{
    public async Task ContentChangedAsync(Library library, IReadOnlyList<Node> files, CancellationToken ct)
    {
        foreach (var node in files.Where(f => f.MediaKind is not null))
        {
            await queue.EnqueueAsync(MediaExtractHandler.Request(node.Id), ct);
        }
    }
}

public static class MediaSetup
{
    public static IServiceCollection AddAtriumMedia(this IServiceCollection services, MediaOptions options)
    {
        services.TryAddSingleton(options);
        services.AddSingleton<MetadataReader>();
        services.AddSingleton<Thumbnailer>();
        services.AddScoped<MediaFiles>();
        services.AddScoped<MediaService>();
        services.AddScoped<INodeContentObserver, MediaContentObserver>();
        services.AddJobHandler<MediaExtractHandler>(MediaExtractHandler.Type);
        services.AddJobHandler<MediaPreviewHandler>(MediaPreviewHandler.Type);
        services.AddJobHandler<MediaSweepHandler>(MediaSweepHandler.Type);
        services.AddRecurringJob(MediaSweepHandler.Schedule);
        return services;
    }

    /// <summary>
    /// libvips keeps decoded images in a cache and uses a thread per core;
    /// on a small device both are kept low so a thumbnail does not crowd
    /// out everything else.
    /// </summary>
    public static void ConfigureLibvips(HardwareInfo hardware)
    {
        try
        {
            NetVips.Cache.Max = 0;
            NetVips.Cache.MaxFiles = 0;
            NetVips.NetVips.Concurrency = hardware.Profile == HardwareProfile.Small ? 1 : Math.Max(1, hardware.ProcessorCount / 2);
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException)
        {
            // libvips is not installed: photos get no thumbnails, which is
            // logged when the first one is attempted. Nothing else is affected.
        }
    }
}
