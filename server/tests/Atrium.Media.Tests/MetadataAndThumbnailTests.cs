using System.Text;
using Atrium.Core.Files;
using NetVips;

namespace Atrium.Media.Tests;

public sealed class MetadataAndThumbnailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    public MetadataAndThumbnailTests() => Directory.CreateDirectory(_dir);

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

    private MediaOptions Options(TimeZoneInfo? zone = null) => new()
    {
        CacheDir = Path.Combine(_dir, "cache"),
        TimeZone = zone ?? TimeZoneInfo.Utc,
    };

    private string Save(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void Exif_gives_the_local_capture_time_its_offset_the_camera_and_the_orientation()
    {
        var path = Save("photo.jpg", Samples.JpegWithExif(64, 48, "2024:05:01 18:30:00", "+02:00", orientation: 6));

        var facts = MetadataReader.ReadPhoto(path);

        Assert.Equal(MediaKind.Photo, facts.Kind);
        Assert.Equal(new DateTime(2024, 5, 1, 18, 30, 0), facts.CapturedLocal);
        Assert.Equal(120, facts.OffsetMinutes);
        Assert.Equal(6, facts.Orientation);
        Assert.Equal("TestCam", facts.CameraMake);
        Assert.Equal("Model 1", facts.CameraModel);
        Assert.Equal(64, facts.Width);
        Assert.Equal(48, facts.Height);

        // The local clock time is kept as it was, whatever the server's zone.
        var reader = new MetadataReader(Options(TimeZoneInfo.FindSystemTimeZoneById("America/New_York")));
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 18, 30, 0, TimeSpan.Zero), reader.CapturedAt(facts, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void A_screenshot_without_exif_is_dated_by_its_file_time_in_the_servers_zone()
    {
        var path = Save("screen.png", Samples.Png(32, 20));
        var facts = MetadataReader.ReadPhoto(path);
        var reader = new MetadataReader(Options(TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade")));

        // 23:30 UTC in winter is 00:30 the next day in Belgrade.
        var captured = reader.CapturedAt(facts, new DateTimeOffset(2025, 1, 10, 23, 30, 0, TimeSpan.Zero));

        Assert.Null(facts.CapturedLocal);
        Assert.Equal(32, facts.Width);
        Assert.Equal(new DateTimeOffset(2025, 1, 11, 0, 30, 0, TimeSpan.Zero), captured);
    }

    [Fact]
    public void A_phone_video_is_read_from_ffprobe_with_its_local_time_and_turned_size_and_without_its_location()
    {
        const string probe = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "hevc", "width": 1920, "height": 1080,
                  "side_data_list": [ { "side_data_type": "Display Matrix", "rotation": -90 } ] },
                { "codec_type": "audio", "codec_name": "aac" }
              ],
              "format": {
                "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "12.345",
                "tags": {
                  "creation_time": "2024-08-01T08:00:00.000000Z",
                  "com.apple.quicktime.creationdate": "2024-08-01T10:00:00+0200",
                  "com.apple.quicktime.location.ISO6709": "+44.8170+020.4570/",
                  "com.apple.quicktime.make": "Apple",
                  "com.apple.quicktime.model": "iPhone 15"
                }
              }
            }
            """;

        var facts = MetadataReader.ParseProbe(Encoding.UTF8.GetBytes(probe));

        Assert.Equal(MediaKind.Video, facts.Kind);
        Assert.Equal((1080, 1920), (facts.Width, facts.Height));
        Assert.Equal(12345, facts.DurationMs);
        Assert.Equal("hevc", facts.VideoCodec);
        Assert.Equal("aac", facts.AudioCodec);
        Assert.Equal(new DateTime(2024, 8, 1, 10, 0, 0), facts.CapturedLocal);
        Assert.Equal(120, facts.OffsetMinutes);
        Assert.Equal("iPhone 15", facts.CameraModel);

        // Nothing that was read holds the location.
        Assert.DoesNotContain(facts.ToString(), "44.8", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_thumbnail_is_upright_small_and_carries_no_metadata()
    {
        // Stored 800 wide and 400 high, to be shown turned a quarter: upright it is 400 wide and 800 high.
        var path = Save("turned.jpg", Samples.JpegWithExif(800, 400, orientation: 6));
        var thumbnails = new Thumbnailer(Options());

        var made = await thumbnails.MakeAsync(path, MediaKind.Photo, new string('a', 64), ThumbnailVariant.Small, background: true, CancellationToken.None);

        using var thumbnail = Image.NewFromFile(made);
        Assert.Equal(200, thumbnail.Width);
        Assert.Equal(400, thumbnail.Height);
        Assert.DoesNotContain(thumbnail.GetFields(), f => f.StartsWith("exif", StringComparison.Ordinal) || f.StartsWith("xmp", StringComparison.Ordinal));
        Assert.Equal(thumbnails.PathFor(new string('a', 64), ThumbnailVariant.Small), made);
    }

    [Fact]
    public async Task A_video_gets_a_thumbnail_from_a_frame_and_a_short_preview()
    {
        var video = await Samples.Mp4Async(_dir);
        var options = Options();
        var thumbnails = new Thumbnailer(options);
        var key = new string('b', 64);

        var small = await thumbnails.MakeAsync(video, MediaKind.Video, key, ThumbnailVariant.Small, background: true, CancellationToken.None);
        var preview = await thumbnails.MakeAsync(video, MediaKind.Video, key, ThumbnailVariant.Preview, background: true, CancellationToken.None);

        using var frame = Image.NewFromFile(small);
        Assert.Equal(320, frame.Width);
        Assert.True(new FileInfo(preview).Length > 0);

        var facts = await new MetadataReader(options).ReadAsync(video, MediaKind.Video, background: true, CancellationToken.None);
        Assert.Equal(new DateTimeOffset(2023, 7, 4, 9, 15, 0, TimeSpan.Zero), facts.CapturedInstant);
        Assert.Equal("h264", facts.VideoCodec);
        Assert.InRange(facts.DurationMs ?? 0, 1900, 2100);
    }

    [Fact]
    public async Task A_file_that_is_not_a_picture_is_reported_as_unsupported()
    {
        var path = Save("broken.jpg", "this is not a picture"u8.ToArray());

        await Assert.ThrowsAsync<UnsupportedMediaException>(() =>
            new Thumbnailer(Options()).MakeAsync(path, MediaKind.Photo, new string('c', 64), ThumbnailVariant.Small, true, CancellationToken.None));
    }
}
