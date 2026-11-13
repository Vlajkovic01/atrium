using System.Globalization;
using Atrium.Core.Files;

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
public sealed class Thumbnailer(MediaOptions options, IImageEncoder encoder)
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

    /// <summary>
    /// Makes the thumbnail of a local file and stores it under the content
    /// key. Returns its path. A job and a person's request often want the
    /// same thumbnail at once: one makes it, the other waits and uses it.
    /// </summary>
    public async Task<string> MakeAsync(
        string sourcePath, MediaKind kind, string contentKey, ThumbnailVariant variant, bool background, CancellationToken ct)
    {
        var target = PathFor(contentKey, variant);
        if (File.Exists(target))
        {
            return target;
        }

        var gate = Making.GetOrAdd(target, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return File.Exists(target) ? target : await MakeLockedAsync(sourcePath, kind, target, variant, background, ct);
        }
        finally
        {
            gate.Release();
            if (gate.CurrentCount == 1)
            {
                Making.TryRemove(new KeyValuePair<string, SemaphoreSlim>(target, gate));
            }
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Making = new(StringComparer.Ordinal);

    private async Task<string> MakeLockedAsync(
        string sourcePath, MediaKind kind, string target, ThumbnailVariant variant, bool background, CancellationToken ct)
    {
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
            webp = await FromPictureAsync(await GrabFrameAsync(sourcePath, background, ct), size, ct);
        }
        else
        {
            try
            {
                webp = await encoder.ThumbnailAsync(sourcePath, size, ct);
            }
            catch (UnsupportedMediaException)
            {
                // A format this libvips cannot read, such as HEIC with the
                // NuGet build: FFmpeg decodes the first picture instead.
                webp = await FromPictureAsync(await DecodeWithFfmpegAsync(sourcePath, background, ct), size, ct);
            }
        }

        await StoreAsync(target, webp);
        return target;
    }

    /// <summary>A picture FFmpeg decoded, as PNG, handed to the encoder through a temporary file.</summary>
    private async Task<byte[]> FromPictureAsync(byte[] png, int size, CancellationToken ct)
    {
        if (png.Length == 0)
        {
            throw new UnsupportedMediaException("No picture could be decoded.");
        }

        Directory.CreateDirectory(options.TempDir);
        var temp = Path.Combine(options.TempDir, $"{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(temp, png, ct);
            return await encoder.ThumbnailAsync(temp, size, ct);
        }
        finally
        {
            File.Delete(temp);
        }
    }
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

            Place(temp, target);
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
        Place(temp, target);
    }

    /// <summary>
    /// Moves a finished file into place, never over an existing one: its name
    /// is its content, so one that is already there is the same thumbnail,
    /// and it may be being sent to someone right now.
    /// </summary>
    private static void Place(string temp, string target)
    {
        try
        {
            File.Move(temp, target, overwrite: false);
        }
        catch (IOException) when (File.Exists(target))
        {
            File.Delete(temp);
        }
    }
}
