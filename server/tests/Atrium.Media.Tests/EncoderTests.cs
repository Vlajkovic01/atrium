using System.Globalization;
using System.Text;
using Atrium.Core.Media;
using Atrium.Media.Playback;
using Atrium.Media.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atrium.Media.Tests;

/// <summary>
/// Video encoders (DESIGN.md 7.5, D-064). What a machine has decides what
/// runs: on the development laptop Intel Quick Sync is used for real; on
/// CI, which has no GPU, every hardware candidate fails its test and
/// software is chosen, which these tests then check instead.
/// </summary>
public sealed class EncoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    public EncoderTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public void Each_platform_tries_its_own_hardware_and_every_test_is_a_real_encode()
    {
        var candidates = VideoEncoders.Candidates();

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal([VideoEncoderKind.Nvenc, VideoEncoderKind.Qsv, VideoEncoderKind.Amf], candidates);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Equal([VideoEncoderKind.Nvenc, VideoEncoderKind.Qsv, VideoEncoderKind.Vaapi], candidates);
        }

        var vaapi = VideoEncoders.TestArguments(VideoEncoderKind.Vaapi);
        Assert.Equal(VideoEncoders.VaapiDevice, vaapi[vaapi.ToList().IndexOf("-vaapi_device") + 1]);
        Assert.Contains("format=nv12,hwupload", vaapi);
        Assert.Contains("h264_nvenc", VideoEncoders.TestArguments(VideoEncoderKind.Nvenc));
    }

    [Theory]
    [InlineData(VideoEncoderKind.Software, "libx264", "format=yuv420p")]
    [InlineData(VideoEncoderKind.Nvenc, "h264_nvenc", "format=yuv420p")]
    [InlineData(VideoEncoderKind.Qsv, "h264_qsv", "format=nv12")]
    [InlineData(VideoEncoderKind.Vaapi, "h264_vaapi", "format=nv12,hwupload")]
    [InlineData(VideoEncoderKind.Amf, "h264_amf", "format=nv12")]
    public void Encoded_video_names_its_encoder_and_the_frames_it_takes(VideoEncoderKind kind, string encoder, string frames)
    {
        var output = new StreamOutput(0, null, CopyVideo: false, CopyAudio: false, 4_000_000, 720, ToneMap: false, kind);

        var args = output.Arguments("in.mkv", SegmentPlan.Fixed(20_000_000, 6_000_000), 0, "out").ToList();

        Assert.Equal(encoder, args[args.IndexOf("-c:v") + 1]);
        Assert.EndsWith(frames, args[args.IndexOf("-vf") + 1], StringComparison.Ordinal);
        Assert.Contains("-force_key_frames", args);
        Assert.Equal(kind == VideoEncoderKind.Vaapi, args.Contains("-vaapi_device"));
    }

    [Fact]
    public async Task Detection_finds_what_works_here_and_software_always_works()
    {
        var encoders = new VideoEncoders(new MediaOptions { CacheDir = _dir }, NullLogger<VideoEncoders>.Instance);

        var tests = await encoders.DetectAsync(CancellationToken.None);

        Assert.Equal(VideoEncoders.Candidates().Append(VideoEncoderKind.Software), tests.Select(t => t.Kind));
        Assert.True(tests[^1].Works);
        Assert.All(tests.Where(t => !t.Works), t => Assert.False(string.IsNullOrWhiteSpace(t.Error)));
        Assert.Equal(tests.FirstOrDefault(t => t.Works)!.Kind, encoders.Chosen);
    }

    [Fact]
    public async Task The_chosen_encoder_makes_six_second_segments_that_join_up()
    {
        var encoders = new VideoEncoders(new MediaOptions { CacheDir = _dir }, NullLogger<VideoEncoders>.Instance);
        await encoders.DetectAsync(CancellationToken.None);

        var (session, plan) = await SessionAsync(encoders.Chosen, null);
        await using (session)
        {
            var parts = new List<byte[]> { await session.InitAsync(CancellationToken.None) };
            for (var n = 0; n < plan.Count; n++)
            {
                var segment = await session.SegmentAsync(n, CancellationToken.None);
                parts.Add(segment);

                // Each segment alone starts where the playlist says: the encoder made a keyframe there.
                var (_, start, _) = await ProbeAsync([parts[0], segment]);
                Assert.InRange(start, (n * 6.0) - 0.05, (n * 6.0) + 0.1);
            }

            var (duration, _, errors) = await ProbeAsync(parts);
            Assert.InRange(duration, 19.8, 20.3);
            Assert.Equal(string.Empty, errors);
        }
    }

    [Fact]
    public async Task A_hardware_encoder_that_fails_during_a_stream_gives_way_to_software()
    {
        // VideoToolbox exists only on Apple machines, where this does not run.
        Assert.False(OperatingSystem.IsMacOS());
        (VideoEncoderKind Kind, string Error)? failed = null;

        var (session, _) = await SessionAsync(VideoEncoderKind.VideoToolbox, (kind, error) => failed = (kind, error));
        await using (session)
        {
            var segment = await session.SegmentAsync(1, CancellationToken.None);

            Assert.NotEmpty(segment);
            Assert.Equal(VideoEncoderKind.Software, session.Output.Encoder);
            Assert.Equal(VideoEncoderKind.VideoToolbox, failed!.Value.Kind);
        }
    }

    private async Task<(StreamSession Session, IReadOnlyList<Segment> Plan)> SessionAsync(
        VideoEncoderKind encoder, Action<VideoEncoderKind, string>? failed)
    {
        var source = Path.Combine(_dir, $"{Guid.NewGuid():N}.mp4");
        await ToolRunner.RunAsync(
            "ffmpeg",
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc=duration=20:size=320x180:rate=24", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-y", source],
            TimeSpan.FromMinutes(2),
            false,
            4096,
            CancellationToken.None);
        var video = new MediaStream(0, MediaStreamType.Video, "h264", "High", 13, 8, 320, 180, 24);
        var plan = SegmentPlan.Fixed(20_000_000, SegmentPlan.TargetUs);
        var output = new StreamOutput(0, null, CopyVideo: false, CopyAudio: false, 1_000_000, 180, ToneMap: false, encoder);
        var session = new StreamSession(
            "test",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new PlaybackDecision(PlaybackMethod.Transcode, [PlaybackReasons.VideoCodec], 0, null),
            output,
            plan,
            video,
            null,
            Path.Combine(_dir, "stream-" + Guid.NewGuid().ToString("N")),
            0,
            "ffmpeg",
            _ => Task.FromResult(source),
            TimeProvider.System,
            NullLogger.Instance,
            failed);
        return (session, plan);
    }

    private async Task<(double Duration, double VideoStart, string Errors)> ProbeAsync(IEnumerable<byte[]> parts)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, parts.SelectMany(p => p).ToArray());
        var probe = Encoding.UTF8.GetString(await ToolRunner.RunAsync(
            "ffprobe",
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "format=duration:stream=start_time", "-of", "default=nw=1", path],
            TimeSpan.FromMinutes(1),
            false,
            65536,
            CancellationToken.None));
        double Value(string key) => double.Parse(
            probe.Split('\n').First(l => l.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..].Trim(), CultureInfo.InvariantCulture);
        var errors = Encoding.UTF8.GetString(await ToolRunner.RunAsync(
            "ffmpeg", ["-nostdin", "-v", "error", "-i", path, "-f", "null", "-"], TimeSpan.FromMinutes(1), false, 65536, CancellationToken.None));
        return (Value("duration"), Value("start_time"), errors.Trim());
    }
}
