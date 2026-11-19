using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atrium.Files;
using Microsoft.Extensions.Logging;

namespace Atrium.Media.Streaming;

/// <summary>
/// Where a video's keyframes are, which copying its video into HLS needs
/// (DESIGN.md 7.4). MP4, QuickTime, Matroska and WebM carry an index that
/// is read without reading the media, from any storage location. Other
/// containers are scanned by ffprobe, which reads the whole file, so only
/// when asked to and only from a local path. Answers are kept in the cache
/// location, named by the content, so a changed file is indexed again and
/// the cache can be deleted at any time.
/// </summary>
public sealed class KeyframeIndex(MediaOptions options, ILogger<KeyframeIndex> logger)
{
    /// <summary>Largest ffprobe answer accepted for a scan; a two-hour film's packets are a few megabytes.</summary>
    private const long MaxScanOutput = 256L * 1024 * 1024;

    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(30);

    public string Directory => Path.Combine(options.CacheDir, "keyframes");

    /// <param name="container">The container as clients name it (mp4, mov, mkv, webm...).</param>
    /// <param name="localPath">Where ffprobe can read the file for a scan: a local path or a loopback URL.</param>
    /// <param name="scan">Whether a container without an index may be scanned now, reading the whole file.</param>
    /// <returns>
    /// Keyframe times in microseconds, ascending, on the file's own clock,
    /// as FFmpeg reads the timestamps: an MP4's edit list is applied, and an
    /// MPEG-TS clock may start at 1.4 seconds. FFmpeg is later asked to seek
    /// to exactly these times. Null when not known.
    /// </returns>
    public async Task<IReadOnlyList<long>?> GetAsync(
        ContentSource source, string container, string? localPath, bool scan, CancellationToken ct)
    {
        var path = CachePath(source.Key);
        if (Load(path) is { } cached)
        {
            return cached;
        }

        IReadOnlyList<long>? times = null;
        try
        {
            var reader = new RangeReader(source);
            times = container switch
            {
                "mp4" or "mov" => await Mp4Keyframes.ReadAsync(reader, ct),
                "mkv" or "webm" => await MatroskaKeyframes.ReadAsync(reader, ct),
                _ => null,
            };
        }
        catch (IOException e)
        {
            logger.LogWarning(e, "The index of a {Container} file could not be read.", container);
        }

        if (times is null && scan && localPath is not null)
        {
            times = await ScanAsync(localPath, ct);
        }

        if (times is { Count: > 0 })
        {
            Save(path, times);
            return times;
        }

        return null;
    }

    /// <summary>
    /// The keyframes ffprobe finds by reading every packet of the video.
    /// </summary>
    public async Task<IReadOnlyList<long>?> ScanAsync(string path, CancellationToken ct)
    {
        byte[] output;
        try
        {
            output = await ToolRunner.RunAsync(
                options.Ffprobe,
                ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", "--", path],
                ScanTimeout,
                background: true,
                MaxScanOutput,
                ct);
        }
        catch (Exception e) when (e is MediaToolException or IOException or TimeoutException)
        {
            logger.LogWarning(e, "ffprobe could not scan a video for keyframes.");
            return null;
        }

        var times = new List<long>();
        foreach (var line in Encoding.UTF8.GetString(output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length >= 2 && parts[1].StartsWith('K')
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                times.Add((long)Math.Round(seconds * 1_000_000));
            }
        }

        times.Sort();
        return times.Count > 0 ? times : null;
    }

    /// <summary>The cache file of a content key. The key is hashed, so nothing in it can reach outside the folder.</summary>
    private string CachePath(string key)
    {
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(Directory, name[..2], name + ".json");
    }

    private static IReadOnlyList<long>? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<long>>(File.ReadAllBytes(path)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Derived data: one that cannot be read is made again.
            return null;
        }
    }

    private void Save(string path, IReadOnlyList<long> times)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(times));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "A keyframe index could not be kept in the cache.");
        }
    }
}
