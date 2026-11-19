using System.Text;
using Atrium.Media.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atrium.Media.Tests;

/// <summary>Pictures for the seek bar, made from keyframes by a real FFmpeg (D-065).</summary>
public sealed class TrickplayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    public TrickplayTests() => Directory.CreateDirectory(_dir);

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
    public async Task A_picture_every_ten_seconds_on_one_sheet_of_ten_by_ten()
    {
        var trickplay = new Trickplay(new MediaOptions { CacheDir = Path.Combine(_dir, "cache") }, NullLogger<Trickplay>.Instance);
        var video = await VideoAsync(seconds: 95, fps: 5);

        var info = await trickplay.MakeAsync("key-1", video, 95_000, 160, 90, CancellationToken.None);

        Assert.Equal(new TrickplayInfo(10_000, 240, 136, 10, 10, 10, 1), info);
        Assert.Equal(info, trickplay.Find("key-1"));
        var sheet = trickplay.SheetPath("key-1", 0)!;
        Assert.Equal("2400x1360", await SizeAsync(sheet));
        Assert.Null(trickplay.SheetPath("key-1", 1));
        Assert.Null(trickplay.Find("another"));
    }

    [Fact]
    public async Task A_long_video_fills_more_sheets_and_a_short_one_gets_none()
    {
        var trickplay = new Trickplay(new MediaOptions { CacheDir = Path.Combine(_dir, "cache") }, NullLogger<Trickplay>.Instance);
        var video = await VideoAsync(seconds: 1050, fps: 1);

        var info = await trickplay.MakeAsync("long", video, 1_050_000, 160, 90, CancellationToken.None);

        Assert.Equal((2, 105), (info!.Sheets, info.Count));
        Assert.NotNull(trickplay.SheetPath("long", 1));
        Assert.Null(await trickplay.MakeAsync("short", video, 59_000, 160, 90, CancellationToken.None));
        Assert.Null(trickplay.Find("short"));
    }

    private async Task<string> VideoAsync(int seconds, int fps)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.mp4");
        await ToolRunner.RunAsync(
            "ffmpeg",
            ["-nostdin", "-v", "error", "-f", "lavfi", "-i", $"testsrc=duration={seconds}:size=160x90:rate={fps}",
                "-c:v", "libx264", "-preset", "ultrafast", "-g", fps.ToString(System.Globalization.CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p", "-y", path],
            TimeSpan.FromMinutes(2),
            false,
            4096,
            CancellationToken.None);
        return path;
    }

    private static async Task<string> SizeAsync(string path) =>
        Encoding.UTF8.GetString(await ToolRunner.RunAsync(
            "ffprobe", ["-v", "error", "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", path], TimeSpan.FromMinutes(1), false, 4096, CancellationToken.None)).Trim();
}
