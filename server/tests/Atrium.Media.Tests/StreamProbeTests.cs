using System.Text;
using Atrium.Core.Media;
using Atrium.Media.Playback;

namespace Atrium.Media.Tests;

/// <summary>What a playback decision needs, read from ffprobe (DESIGN.md 7.2, 7.3).</summary>
public sealed class StreamProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

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
    public async Task A_real_film_is_read_with_every_track_its_bit_depth_and_HDR()
    {
        var path = await Samples.HdrMkvAsync(_dir);
        var reader = new MetadataReader(new MediaOptions { CacheDir = Path.Combine(_dir, "cache") });

        var facts = await reader.ReadAsync(path, Atrium.Core.Files.MediaKind.Video, background: false, CancellationToken.None);
        var streams = facts.Streams!;

        var video = Assert.Single(streams, s => s.Type == MediaStreamType.Video);
        Assert.Equal(("hevc", "Main 10", 10, 320, 180), (video.Codec, video.Profile, video.BitDepth, video.Width, video.Height));
        Assert.Equal(HdrFormat.Hdr10, video.Hdr);
        Assert.Equal(24, video.FrameRate);

        var audio = streams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        Assert.Equal([("aac", 2, "eng", true), ("ac3", 6, "srp", false)], audio.Select(a => (a.Codec!, a.Channels!.Value, a.Language!, a.IsDefault)));

        var subtitle = Assert.Single(streams, s => s.Type == MediaStreamType.Subtitle);
        Assert.Equal(("subrip", "srp", true), (subtitle.Codec, subtitle.Language, subtitle.IsForced));

        Assert.Equal("mkv", PlaybackDecider.Container(facts.Format, "film.mkv", streams));
        Assert.True(facts.BitRate > 0);
    }

    [Fact]
    public void Dolby_Vision_is_read_with_its_profile_and_base_layer()
    {
        const string probe = """
            {
              "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "hevc", "profile": "Main 10", "level": 153,
                  "width": 3840, "height": 2160, "pix_fmt": "yuv420p10le", "color_transfer": "smpte2084",
                  "avg_frame_rate": "24000/1001", "disposition": { "default": 1 },
                  "side_data_list": [ { "side_data_type": "DOVI configuration record", "dv_profile": 8,
                    "dv_bl_signal_compatibility_id": 1 } ] },
                { "index": 1, "codec_type": "audio", "codec_name": "truehd", "channels": 8,
                  "tags": { "language": "und", "title": "Atmos" } },
                { "index": 2, "codec_type": "video", "codec_name": "mjpeg", "disposition": { "attached_pic": 1 } },
                { "index": 3, "codec_type": "attachment", "codec_name": "ttf" }
              ],
              "format": { "format_name": "matroska,webm", "duration": "5400.0", "bit_rate": "52000000" }
            }
            """;

        var facts = MetadataReader.ParseProbe(Encoding.UTF8.GetBytes(probe));

        Assert.Equal(52_000_000, facts.BitRate);
        Assert.Equal(2, facts.Streams!.Count);
        var video = facts.Streams[0];
        Assert.Equal((HdrFormat.DolbyVision, 8, HdrFormat.Hdr10), (video.Hdr, video.DolbyVisionProfile, video.DolbyVisionBaseLayer));
        Assert.Equal((153, 10, 23.976), (video.Level!.Value, video.BitDepth!.Value, video.FrameRate!.Value));
        Assert.True(video.IsDefault);

        var audio = facts.Streams[1];
        Assert.Equal((8, null, "Atmos"), (audio.Channels!.Value, audio.Language, audio.Title));
    }

    [Theory]
    [InlineData("arib-std-b67", "yuv420p10le", HdrFormat.Hlg, 10)]
    [InlineData("bt709", "yuv420p", HdrFormat.None, 8)]
    [InlineData(null, "p010le", HdrFormat.None, 10)]
    [InlineData(null, "yuv422p12le", HdrFormat.None, 12)]
    public void Dynamic_range_and_bit_depth_come_from_the_transfer_and_pixel_format(
        string? transfer, string pixels, HdrFormat hdr, int depth)
    {
        var transferField = transfer is null ? string.Empty : $"\"color_transfer\": \"{transfer}\",";
        var probe = $$"""
            { "streams": [ { "codec_type": "video", "codec_name": "hevc", {{transferField}} "pix_fmt": "{{pixels}}" } ],
              "format": { "format_name": "mpegts" } }
            """;

        var video = Assert.Single(MetadataReader.ParseProbe(Encoding.UTF8.GetBytes(probe)).Streams!);

        Assert.Equal((hdr, depth), (video.Hdr, video.BitDepth!.Value));
    }
}
