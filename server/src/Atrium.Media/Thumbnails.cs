using System.Globalization;
using Atrium.Core.Files;
using NetVips;

namespace Atrium.Media;

public enum ThumbnailVariant
{
    /// <summary>The grid size, made for every photo and video.</summary>
    Small,

    /// <summary>The viewer size, made when first asked for.</summary>
    Large,

    /// <summary>A few silent seconds of a video, for hovering in the grid.</summary>
    Preview,
}

/// <summary>The file cannot be turned into a picture on this server.</summary>
public sealed class UnsupportedMediaException(string message) : Exception(message);

/// <summary>
/// Makes thumbnails with libvips, which decodes at reduced size while
/// reading, so a large photo needs little memory (D-032). Video frames and
/// formats libvips cannot read come from FFmpeg. Every thumbnail is upright
/// and carries no metadata at all: nothing from the original, a location
/// included, reaches the cache (D-031).
/// </summary>
public sealed class Thumbnailer(MediaOptions options)
{
    private const long MaxFrameBytes = 64L * 1024 * 1024;

    /// <summary>Where a thumbnail of this content lives in the cache. Content keys never change meaning, so neither does the path.</summary>
    public string PathFor(string contentKey, ThumbnailVariant variant)
    {
        var suffix = variant switch
        {
            ThumbnailVariant.Small => "s.webp",
            ThumbnailVariant.Large => "l.webp",
            _ => "p.mp4",
        };
        return Path.Combine(options.ThumbnailDir, contentKey[..2], $"{contentKey}-{suffix}");
    }

    /// <summary>Makes the thumbnail of a local file and stores it under the content key. Returns its path.</summary>
    public async Task<string> MakeAsync(
        string sourcePath, MediaKind kind, string contentKey, ThumbnailVariant variant, bool background, CancellationToken ct)
    {
        var target = PathFor(contentKey, variant);
        if (File.Exists(target))
        {
            return target;
        }

        if (variant == ThumbnailVariant.Preview)
        {
            if (kind != MediaKind.Video)
            {
                throw new UnsupportedMediaException("Only videos have a preview clip.");
            }

            await MakePreviewAsync(sourcePath, target, background, ct);
            return target;
        }

        var size = variant == ThumbnailVariant.Small ? options.SmallSize : options.LargeSize;
        byte[] webp;
        if (kind == MediaKind.Video)
        {
            webp = FromBuffer(await GrabFrameAsync(sourcePath, background, ct), size);
        }
        else
        {
            try
            {
                webp = FromFile(sourcePath, size);
            }
            catch (Exception e) when (e is VipsException || IsLibvipsMissing(e))
            {
                // A format this libvips cannot read, such as HEIC with the
                // NuGet build: FFmpeg decodes the first picture instead.
                webp = FromBuffer(await DecodeWithFfmpegAsync(sourcePath, background, ct), size);
            }
        }

        await StoreAsync(target, webp);
        return target;
    }

    /// <summary>
    /// libvips reads through a stream Atrium opens and closes, never by
    /// path: given a path it may keep the file mapped after it is done,
    /// which on Windows stops other programs from writing the user's file.
    /// </summary>
    private static byte[] FromFile(string path, int size)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920);

        // Thumbnail turns the picture upright by its EXIF orientation.
        using var image = Image.ThumbnailStream(stream, size, height: size, size: Enums.Size.Down, failOn: Enums.FailOn.None);
        return Save(image);
    }

    static Thumbnailer()
    {
        // A cached image would keep its source open after the thumbnail is made.
        try
        {
            Cache.Max = 0;
            Cache.MaxFiles = 0;
        }
        catch (Exception e) when (IsLibvipsMissing(e))
        {
        }
    }

    private static byte[] FromBuffer(byte[] buffer, int size)
    {
        if (buffer.Length == 0)
        {
            throw new UnsupportedMediaException("No picture could be decoded.");
        }

        try
        {
            using var image = Image.ThumbnailBuffer(buffer, size, height: size, size: Enums.Size.Down, failOn: Enums.FailOn.None);
            return Save(image);
        }
        catch (VipsException e)
        {
            throw new UnsupportedMediaException(e.Message);
        }
        catch (Exception e) when (IsLibvipsMissing(e))
        {
            throw new MediaToolException("libvips is not installed.", missing: true);
        }
    }

    private static bool IsLibvipsMissing(Exception e) => e is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException;

    // No metadata is kept: not the EXIF of the original, not its location.
    private static byte[] Save(Image image) => image.WebpsaveBuffer(q: 78, keep: Enums.ForeignKeep.None);

    private async Task<byte[]> GrabFrameAsync(string path, bool background, CancellationToken ct)
    {
        // A second in avoids the black first frame of many clips; a clip
        // shorter than that gets its first frame.
        var frame = await FrameAtAsync(path, "1", background, ct);
        return frame.Length > 0 ? frame : await FrameAtAsync(path, "0", background, ct);
    }

    private Task<byte[]> FrameAtAsync(string path, string seconds, bool background, CancellationToken ct) =>
        Run(
            ["-v", "error", "-ss", seconds, "-i", path, "-frames:v", "1", "-an", "-sn",
                "-vf", "scale='min(1920,iw)':'min(1920,ih)':force_original_aspect_ratio=decrease",
                "-f", "image2pipe", "-vcodec", "png", "pipe:1"],
            background,
            ct);

    private Task<byte[]> DecodeWithFfmpegAsync(string path, bool background, CancellationToken ct) =>
        Run(["-v", "error", "-i", path, "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "pipe:1"], background, ct);

    private async Task<byte[]> Run(IReadOnlyList<string> arguments, bool background, CancellationToken ct)
    {
        try
        {
            return await ToolRunner.RunAsync(options.Ffmpeg, ["-nostdin", "-hide_banner", .. arguments], options.ToolTimeout, background, MaxFrameBytes, ct);
        }
        catch (MediaToolException e) when (!e.Missing)
        {
            throw new UnsupportedMediaException(e.Message);
        }
    }

    /// <summary>Four silent seconds at 360 lines, small enough to start playing at once.</summary>
    private async Task MakePreviewAsync(string path, string target, bool background, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + $".{Guid.NewGuid():N}.tmp.mp4";
        try
        {
            await Run(
                ["-v", "error", "-ss", "1", "-t", "4", "-i", path, "-an", "-sn", "-dn",
                    "-vf", "scale=-2:'min(360,ih)'", "-c:v", "libx264", "-preset", "veryfast", "-crf", "30",
                    "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-map_metadata", "-1", "-y", temp],
                background,
                ct);
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
            {
                throw new UnsupportedMediaException("FFmpeg made no preview.");
            }

            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Written under a temporary name and moved into place, so a reader never sees half a thumbnail.</summary>
    private static async Task StoreAsync(string target, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        await File.WriteAllBytesAsync(temp, content);
        File.Move(temp, target, overwrite: true);
    }
}
