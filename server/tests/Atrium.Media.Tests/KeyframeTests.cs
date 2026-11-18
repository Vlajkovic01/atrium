using Atrium.Files;
using Atrium.Media.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atrium.Media.Tests;

/// <summary>Keyframes read from the indexes containers carry, checked against ffprobe reading every packet (DESIGN.md 7.4).</summary>
public sealed class KeyframeTests : IDisposable
{
    private static readonly string[] H264 =
        ["-c:v", "libx264", "-preset", "ultrafast", "-bf", "2", "-g", "48", "-keyint_min", "48", "-sc_threshold", "0", "-pix_fmt", "yuv420p"];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    public KeyframeTests() => Directory.CreateDirectory(_dir);

    public static TheoryData<string, string, string[]> Indexed => new()
    {
        { "mp4", "mp4", H264 },
        { "mp4", "mp4", [.. H264, "-movflags", "+faststart"] },
        { "mov", "mov", H264 },
        { "mkv", "mkv", H264 },
        { "webm", "webm", ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-g", "48", "-keyint_min", "48"] },
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [MemberData(nameof(Indexed))]
    public async Task The_index_in_the_container_gives_the_keyframes_ffprobe_finds_in_every_packet(string extension, string container, string[] output)
    {
        var path = await VideoAsync(extension, output);
        var index = Index();

        var read = await index.GetAsync(Source(path), container, localPath: null, scan: false, CancellationToken.None);
        var scanned = await index.ScanAsync(path, CancellationToken.None);

        AssertEveryTwoSeconds(read);
        Assert.Equal(scanned!.Count, read!.Count);
        Assert.All(read.Zip(scanned), pair => Assert.InRange(pair.First - pair.Second, -1_000, 1_000));
    }

    [Theory]
    [InlineData("mp4", "mp4", "-movflags", "+frag_keyframe+empty_moov")]
    [InlineData("ts", "mpegts", "-f", "mpegts")]
    public async Task Without_an_index_the_file_is_scanned_only_when_allowed_and_local(string extension, string container, string flag, string value)
    {
        var path = await VideoAsync(extension, [.. H264, flag, value]);
        var index = Index();

        Assert.Null(await index.GetAsync(Source(path), container, localPath: path, scan: false, CancellationToken.None));
        Assert.Null(await index.GetAsync(Source(path), container, localPath: null, scan: true, CancellationToken.None));
        AssertEveryTwoSeconds(await index.GetAsync(Source(path), container, localPath: path, scan: true, CancellationToken.None));
    }

    [Fact]
    public async Task An_index_is_kept_in_the_cache_by_content_and_a_damaged_one_is_made_again()
    {
        var path = await VideoAsync("mkv", H264);
        var index = Index();

        await index.GetAsync(Source(path), "mkv", null, false, CancellationToken.None);
        var kept = Assert.Single(Directory.GetFiles(index.Directory, "*.json", SearchOption.AllDirectories));
        await File.WriteAllTextAsync(kept, "not json");

        AssertEveryTwoSeconds(await index.GetAsync(Source(path), "mkv", null, false, CancellationToken.None));
        Assert.StartsWith("[", await File.ReadAllTextAsync(kept), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Random_bytes_are_not_taken_for_an_index()
    {
        var path = Path.Combine(_dir, "noise.bin");
        var noise = new byte[200_000];
        new Random(7).NextBytes(noise);
        await File.WriteAllBytesAsync(path, noise);

        Assert.Null(await Index().GetAsync(Source(path), "mkv", null, false, CancellationToken.None));
        Assert.Null(await Index().GetAsync(Source(path), "mp4", null, false, CancellationToken.None));
    }

    private static ContentSource Source(string path)
    {
        var info = new FileInfo(path);
        return new ContentSource(info.Length, Path.GetFileName(path), info.LastWriteTimeUtc, (range, _) =>
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            stream.Position = range?.Offset ?? 0;
            return Task.FromResult<Stream>(stream);
        });
    }

    /// <summary>Five keyframes two seconds apart, within a frame, from wherever the file's clock starts.</summary>
    private static void AssertEveryTwoSeconds(IReadOnlyList<long>? times)
    {
        Assert.NotNull(times);
        Assert.Equal(5, times.Count);
        for (var i = 0; i < times.Count; i++)
        {
            Assert.InRange(times[i] - times[0], (i * 2_000_000) - 42_000, (i * 2_000_000) + 42_000);
        }
    }

    [Theory]
    [InlineData("mp4", "-movflags", "+faststart")]
    [InlineData("mkv", "-f", "matroska")]
    public async Task An_indexed_file_starts_its_clock_at_zero(string extension, string flag, string value)
    {
        var path = await VideoAsync(extension, [.. H264, flag, value]);

        var times = await Index().GetAsync(Source(path), extension, null, false, CancellationToken.None);

        Assert.Equal(0, times![0]);
    }

    private KeyframeIndex Index() => new(new MediaOptions { CacheDir = Path.Combine(_dir, "cache") }, NullLogger<KeyframeIndex>.Instance);

    /// <summary>Nine seconds at 24 frames a second, a keyframe every two seconds and B-frames between.</summary>
    private async Task<string> VideoAsync(string extension, string[] output)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.{extension}");
        await ToolRunner.RunAsync(
            "ffmpeg",
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc=duration=9:size=160x90:rate=24", .. output, "-y", path],
            TimeSpan.FromMinutes(2),
            background: false,
            maxOutput: 4096,
            CancellationToken.None);
        return path;
    }
}

public sealed class RangeReaderTests
{
    [Fact]
    public async Task Small_reads_near_each_other_cost_one_request_and_the_end_of_the_file_is_respected()
    {
        var content = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();
        var opened = 0;
        var source = new ContentSource(content.Length, "k", DateTimeOffset.UnixEpoch, (range, _) =>
        {
            opened++;
            var offset = (int)range!.Value.Offset;
            var length = (int)(range.Value.Length ?? (content.Length - offset));
            return Task.FromResult<Stream>(new MemoryStream(content, offset, length));
        });
        var reader = new RangeReader(source);

        var first = await reader.ReadAsync(10, 8, CancellationToken.None);
        var near = await reader.ReadAsync(1_000, 16, CancellationToken.None);
        var tail = await reader.ReadAsync(199_990, 100, CancellationToken.None);

        Assert.Equal(content[10..18], first);
        Assert.Equal(content[1_000..1_016], near);
        Assert.Equal(content[199_990..], tail);
        Assert.Equal(2, opened);
        Assert.Empty(await reader.ReadAsync(content.Length, 4, CancellationToken.None));
    }
}

public sealed class SegmentPlanTests
{
    [Fact]
    public void Fixed_segments_have_the_target_length_and_the_last_ends_with_the_video()
    {
        var segments = SegmentPlan.Fixed(20_000_000, 6_000_000);

        Assert.Equal(
            [(0, 0L, 6_000_000L), (1, 6_000_000L, 6_000_000L), (2, 12_000_000L, 6_000_000L), (3, 18_000_000L, 2_000_000L)],
            segments.Select(s => (s.Index, s.StartUs, s.DurationUs)));
    }

    [Fact]
    public void Copied_video_is_cut_at_the_first_keyframe_after_the_target_length()
    {
        long[] keyframes = [0, 2_000_000, 4_000_000, 7_000_000, 8_000_000, 13_500_000, 14_000_000, 19_000_000];

        var segments = SegmentPlan.AtKeyframes(keyframes, 21_000_000, 6_000_000);

        // From 13.5 a cut needs a keyframe at 19.5 or later; the video ends first.
        Assert.Equal(
            [(0L, 7_000_000L), (7_000_000L, 13_500_000L), (13_500_000L, 21_000_000L)],
            segments.Select(s => (s.StartUs, s.EndUs)));
        Assert.Equal([0, 1, 2], segments.Select(s => s.Index));
    }

    [Fact]
    public void Video_without_keyframes_after_the_first_is_one_segment()
    {
        var segments = SegmentPlan.AtKeyframes([0], 90_000_000, 6_000_000);

        Assert.Equal((0L, 90_000_000L), (Assert.Single(segments).StartUs, segments[0].EndUs));
    }

    [Fact]
    public void A_clock_that_does_not_start_at_zero_is_kept_and_keyframes_past_the_end_are_ignored()
    {
        // An MPEG-TS clock starting at 1.4 seconds, 12.5 seconds long.
        var segments = SegmentPlan.AtKeyframes([1_400_000, 7_400_000, 13_400_000, 30_000_000], 12_500_000, 6_000_000);

        Assert.Equal(
            [(1_400_000L, 7_400_000L), (7_400_000L, 13_400_000L), (13_400_000L, 13_900_000L)],
            segments.Select(s => (s.StartUs, s.EndUs)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5_999_999, 0)]
    [InlineData(6_000_000, 1)]
    [InlineData(19_000_000, 3)]
    [InlineData(99_000_000, 3)]
    public void The_segment_of_a_position_is_found(long position, int index)
    {
        Assert.Equal(index, SegmentPlan.At(SegmentPlan.Fixed(20_000_000, 6_000_000), position)!.Index);
    }
}
