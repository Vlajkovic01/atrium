using NetVips;

namespace Atrium.Media;

/// <summary>
/// <c>Atrium.Api --check-media</c>: proves the media tools work on this
/// machine without starting the server or touching any data. Makes a
/// thumbnail with libvips, asks it for a HEIC decoder, and runs FFmpeg and
/// ffprobe. Prints what it found and exits with 0 when all of it works.
/// </summary>
public static class MediaSelfCheck
{
    public const string Argument = "--check-media";

    public static async Task<int> RunAsync(MediaOptions options, TextWriter output)
    {
        var ok = true;

        try
        {
            output.WriteLine($"libvips {NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)}");
            using var source = (Image.Black(64, 48, bands: 3) + new double[] { 30, 90, 150 }).Cast(Enums.BandFormat.Uchar);
            var png = source.PngsaveBuffer();
            using var thumbnail = Image.ThumbnailBuffer(png, 32, height: 32);
            var webp = thumbnail.WebpsaveBuffer(q: 78, keep: Enums.ForeignKeep.None);
            output.WriteLine($"thumbnail: {thumbnail.Width}x{thumbnail.Height}, {webp.Length} bytes of WebP");
            var heif = NetVips.NetVips.TypeFind("VipsOperation", "heifload") != IntPtr.Zero;
            output.WriteLine($"HEIF/AVIF loader: {(heif ? "yes" : "no")} (HEIC also needs the libde265 plugin, which the image installs)");
        }
        catch (Exception e)
        {
            ok = false;
            output.WriteLine($"libvips: FAILED: {e.GetType().Name}: {e.Message}");
        }

        // libvips and the libraries it loads must leave the runtime's own
        // signal handling alone: .NET turns a null dereference into an
        // exception through it. Configured as the server does, then tried.
        try
        {
            MediaSetup.ConfigureLibvips(new Atrium.Jobs.HardwareInfo(Environment.ProcessorCount, 2L * 1024 * 1024 * 1024));
            output.WriteLine($"managed exceptions after libvips: {(NullDereferenceIsCaught() ? "ok" : "FAILED")}");
        }
        catch (Exception e)
        {
            ok = false;
            output.WriteLine($"libvips settings: FAILED: {e.GetType().Name}: {e.Message}");
        }

        foreach (var tool in new[] { options.Ffmpeg, options.Ffprobe })
        {
            try
            {
                var version = await ToolRunner.RunAsync(tool, ["-version"], TimeSpan.FromSeconds(30), background: false, 1024 * 1024, CancellationToken.None);
                output.WriteLine(System.Text.Encoding.UTF8.GetString(version).Split('\n')[0].Trim());
            }
            catch (MediaToolException e)
            {
                ok = false;
                output.WriteLine($"{tool}: FAILED: {e.Message}");
            }
        }

        output.WriteLine(ok ? "Media tools work." : "Some media tools do not work; see above.");
        return ok ? 0 : 1;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool NullDereferenceIsCaught()
    {
        try
        {
            string? nothing = Environment.GetEnvironmentVariable("ATRIUM_UNSET_" + Guid.NewGuid().ToString("N"));
            return nothing!.Length < 0;
        }
        catch (NullReferenceException)
        {
            return true;
        }
    }
}
