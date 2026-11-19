using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atrium.Core.Files;
using Atrium.Core.Jobs;
using Atrium.Core.Media;
using Atrium.Data;
using Atrium.Files;
using Atrium.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atrium.Media.Streaming;

/// <summary>
/// Where a tool can read a node's content when it is not on local disk: a
/// URL on this machine, lasting while <c>alive</c> says so (D-063). The API
/// provides it; without one, remote files are skipped.
/// </summary>
public interface IToolInputs
{
    string? UrlFor(Guid nodeId, Func<bool> alive);
}

/// <summary>
/// One grid of thumbnails for the seek bar: <see cref="Count"/> pictures,
/// one per <see cref="IntervalMs"/>, laid out <see cref="Columns"/> by
/// <see cref="Rows"/> on each sheet, left to right and top to bottom.
/// </summary>
public sealed record TrickplayInfo(int IntervalMs, int Width, int Height, int Columns, int Rows, int Count, int Sheets);

/// <summary>
/// Pictures for the seek bar (DESIGN.md 7.4, UX player): a small picture
/// every ten seconds, made in the background from the keyframes only, so a
/// two-hour film is read once without decoding most of it. Kept in the
/// cache location by content, like thumbnails, and made again when gone.
/// </summary>
public sealed class Trickplay(MediaOptions options, ILogger<Trickplay> logger)
{
    public const int IntervalMs = 10_000;
    public const int Width = 240;
    public const int Columns = 10;
    public const int Rows = 10;

    /// <summary>Shorter videos get none: their whole length fits the bar.</summary>
    public const long MinDurationMs = 60_000;

    private static readonly TimeSpan Timeout = TimeSpan.FromHours(2);

    public string Directory => Path.Combine(options.CacheDir, "trickplay");

    public TrickplayInfo? Find(string contentKey)
    {
        var index = Path.Combine(FolderOf(contentKey), "index.json");
        try
        {
            return File.Exists(index) ? JsonSerializer.Deserialize<TrickplayInfo>(File.ReadAllBytes(index)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The picture sheet <paramref name="sheet"/>, when it was made.</summary>
    public string? SheetPath(string contentKey, int sheet)
    {
        var info = Find(contentKey);
        if (info is null || sheet < 0 || sheet >= info.Sheets)
        {
            return null;
        }

        var path = Path.Combine(FolderOf(contentKey), $"{sheet.ToString(CultureInfo.InvariantCulture)}.jpg");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Makes the sheets of a video, replacing nothing until all of them are there.</summary>
    public async Task<TrickplayInfo?> MakeAsync(string contentKey, string input, long durationMs, int? width, int? height, CancellationToken ct)
    {
        if (durationMs < MinDurationMs)
        {
            return null;
        }

        var folder = FolderOf(contentKey);
        var temporary = folder + "." + Guid.NewGuid().ToString("N") + ".tmp";
        System.IO.Directory.CreateDirectory(temporary);
        try
        {
            var pictureHeight = width is > 0 && height is > 0 ? Math.Max(2, (int)Math.Round(Width * (double)height.Value / width.Value / 2) * 2) : 136;
            var args = new List<string> { "-nostdin", "-v", "error", "-y", "-skip_frame", "nokey" };
            if (input.StartsWith("http://", StringComparison.Ordinal) || input.StartsWith("https://", StringComparison.Ordinal))
            {
                args.AddRange(["-reconnect", "1", "-reconnect_on_network_error", "1", "-reconnect_delay_max", "5"]);
            }

            // The first keyframe at or after each ten seconds; tiles fill sheet by sheet.
            args.AddRange([
                "-i", input, "-map", "0:v:0", "-an", "-sn", "-dn",
                "-vf", string.Create(CultureInfo.InvariantCulture, $"select='gte(t\\,selected_n*{IntervalMs / 1000})',scale={Width}:{pictureHeight},tile={Columns}x{Rows}"),
                "-fps_mode", "vfr", "-q:v", "5", "-start_number", "0",
                Path.Combine(temporary, "%d.jpg"),
            ]);
            await ToolRunner.RunAsync(options.Ffmpeg, args, Timeout, background: true, maxOutput: 64 * 1024, ct);

            var sheets = System.IO.Directory.GetFiles(temporary, "*.jpg").Length;
            if (sheets == 0)
            {
                return null;
            }

            var count = (int)Math.Min((long)sheets * Columns * Rows, (durationMs + IntervalMs - 1) / IntervalMs);
            var info = new TrickplayInfo(IntervalMs, Width, pictureHeight, Columns, Rows, count, sheets);
            await File.WriteAllBytesAsync(Path.Combine(temporary, "index.json"), JsonSerializer.SerializeToUtf8Bytes(info), ct);

            if (System.IO.Directory.Exists(folder))
            {
                System.IO.Directory.Delete(folder, recursive: true);
            }

            System.IO.Directory.Move(temporary, folder);
            return info;
        }
        finally
        {
            if (System.IO.Directory.Exists(temporary))
            {
                try
                {
                    System.IO.Directory.Delete(temporary, recursive: true);
                }
                catch (IOException e)
                {
                    logger.LogDebug(e, "A temporary folder of seek pictures stays until the next sweep.");
                }
            }
        }
    }

    private string FolderOf(string contentKey)
    {
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(contentKey)));
        return Path.Combine(Directory, name[..2], name);
    }
}

/// <summary>Makes a video's seek pictures. Idle priority, after its preview: a nicety, never in anyone's way.</summary>
public sealed class MediaTrickplayHandler(
    AtriumDbContext db, MediaFiles files, Trickplay trickplay, IEnumerable<IToolInputs> inputs) : IJobHandler
{
    public const string Type = "media.trickplay";

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

        var info = node.BlobId is { } blobId
            ? await db.MediaInfos.AsNoTracking().SingleOrDefaultAsync(m => m.BlobId == blobId, ct)
            : await db.MediaInfos.AsNoTracking().SingleOrDefaultAsync(m => m.NodeId == node.Id, ct);
        if (info?.DurationMs is not { } duration || duration < Trickplay.MinDurationMs)
        {
            return;
        }

        var library = await db.Libraries.AsNoTracking().SingleAsync(l => l.Id == node.LibraryId, ct);
        var (source, localPath) = await files.LocateAsync(node, library, ct);
        if (trickplay.Find(source.Key) is not null)
        {
            return;
        }

        var done = false;
        var input = localPath ?? inputs.Select(i => i.UrlFor(node.Id, () => !done)).FirstOrDefault(u => u is not null);
        if (input is null)
        {
            return;
        }

        try
        {
            await trickplay.MakeAsync(source.Key, input, duration, info.Width, info.Height, ct);
        }
        finally
        {
            done = true;
        }
    }
}
