using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Atrium.Media.Streaming;

/// <summary>The ways Atrium encodes H.264 (DESIGN.md 7.5).</summary>
public enum VideoEncoderKind
{
    Software,
    Nvenc,
    Qsv,
    Vaapi,
    VideoToolbox,
    Amf,
}

/// <summary>What the test encode of one encoder found.</summary>
/// <param name="Error">FFmpeg's complaint, shortened, when it did not work.</param>
public sealed record EncoderTest(VideoEncoderKind Kind, bool Works, string? Error);

/// <summary>
/// Which hardware encoders work on this machine (DESIGN.md 7.5, D-064).
/// An encoder FFmpeg lists may still fail: no device, no driver, no
/// permission in the container. So each candidate of this platform encodes
/// one real second; the first that works is used, software otherwise.
/// Detection runs when the server starts and when an admin asks.
/// </summary>
public sealed class VideoEncoders(MediaOptions options, ILogger<VideoEncoders> logger)
{
    /// <summary>The render device VAAPI uses; the first one on almost every machine.</summary>
    public const string VaapiDevice = "/dev/dri/renderD128";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _detecting = new(1, 1);
    private volatile IReadOnlyList<EncoderTest> _tests = [];
    private volatile string? _lastFailure;

    /// <summary>The encoder streams use: the first hardware one that worked, else software.</summary>
    public VideoEncoderKind Chosen => _tests.FirstOrDefault(t => t.Works && t.Kind != VideoEncoderKind.Software)?.Kind ?? VideoEncoderKind.Software;

    /// <summary>Results of the last detection; empty before the first.</summary>
    public IReadOnlyList<EncoderTest> Tests => _tests;

    public DateTimeOffset? DetectedAt { get; private set; }

    /// <summary>The last time a hardware encoder failed during a stream and software took over, for the admin.</summary>
    public string? LastFailure => _lastFailure;

    /// <summary>The candidates for this platform, in order of preference.</summary>
    public static IReadOnlyList<VideoEncoderKind> Candidates()
    {
        if (OperatingSystem.IsMacOS())
        {
            return [VideoEncoderKind.VideoToolbox];
        }

        if (OperatingSystem.IsWindows())
        {
            return [VideoEncoderKind.Nvenc, VideoEncoderKind.Qsv, VideoEncoderKind.Amf];
        }

        return [VideoEncoderKind.Nvenc, VideoEncoderKind.Qsv, VideoEncoderKind.Vaapi];
    }

    /// <summary>The FFmpeg arguments of a one-second test encode, input to output.</summary>
    public static IReadOnlyList<string> TestArguments(VideoEncoderKind kind)
    {
        var input = new[] { "-nostdin", "-v", "error", "-f", "lavfi", "-i", "testsrc2=duration=1:size=1280x720:rate=30" };
        return kind == VideoEncoderKind.Vaapi
            ? ["-nostdin", "-v", "error", "-vaapi_device", VaapiDevice, .. input[3..], "-vf", "format=nv12,hwupload", "-c:v", Encoder(kind), "-f", "null", "-"]
            : [.. input, "-vf", "format=nv12", "-c:v", Encoder(kind), "-f", "null", "-"];
    }

    /// <summary>FFmpeg's name of the H.264 encoder of a kind.</summary>
    public static string Encoder(VideoEncoderKind kind) => kind switch
    {
        VideoEncoderKind.Nvenc => "h264_nvenc",
        VideoEncoderKind.Qsv => "h264_qsv",
        VideoEncoderKind.Vaapi => "h264_vaapi",
        VideoEncoderKind.VideoToolbox => "h264_videotoolbox",
        VideoEncoderKind.Amf => "h264_amf",
        _ => "libx264",
    };

    public async Task<IReadOnlyList<EncoderTest>> DetectAsync(CancellationToken ct)
    {
        await _detecting.WaitAsync(ct);
        try
        {
            var results = new List<EncoderTest>();
            foreach (var kind in Candidates().Append(VideoEncoderKind.Software))
            {
                results.Add(await TestAsync(kind, ct));
            }

            _tests = results;
            DetectedAt = DateTimeOffset.UtcNow;
            logger.LogInformation(
                "Video encoders on this machine ({Architecture}): {Results}. Streams use {Chosen}.",
                RuntimeInformation.OSArchitecture,
                string.Join(", ", results.Select(r => $"{r.Kind} {(r.Works ? "works" : "no")}")),
                Chosen);
            return results;
        }
        finally
        {
            _detecting.Release();
        }
    }

    /// <summary>
    /// A hardware encoder failed during a stream: software takes over for
    /// it, and the admin is shown why. The encoder stays chosen for others,
    /// since one broken file should not turn off the hardware for everyone.
    /// </summary>
    public void ReportFailure(VideoEncoderKind kind, string error)
    {
        _lastFailure = $"{DateTimeOffset.UtcNow:u} {kind}: {Shorten(error)}";
        logger.LogWarning("The {Encoder} encoder failed during a stream; software took over: {Error}", kind, Shorten(error));
    }

    private async Task<EncoderTest> TestAsync(VideoEncoderKind kind, CancellationToken ct)
    {
        if (kind == VideoEncoderKind.Vaapi && !File.Exists(VaapiDevice))
        {
            return new EncoderTest(kind, false, $"{VaapiDevice} is not there; pass the device to the container.");
        }

        try
        {
            // At normal priority: a test starved by a busy machine would mark a
            // working encoder as broken. It lasts about a second.
            await ToolRunner.RunAsync(options.Ffmpeg, TestArguments(kind), TestTimeout, background: false, maxOutput: 64 * 1024, ct);
            return new EncoderTest(kind, true, null);
        }
        catch (MediaToolException e)
        {
            return new EncoderTest(kind, false, Shorten(e.Message));
        }
    }

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > 300 ? line[..300] : line;
    }
}

/// <summary>Detects the encoders when the server starts, after it is up, so starting is not slowed.</summary>
public sealed class VideoEncoderDetection(VideoEncoders encoders, ILogger<VideoEncoderDetection> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            await encoders.DetectAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            logger.LogWarning(e, "Video encoders could not be detected; streams use software.");
        }
    }
}
