using System.Diagnostics;
using System.Text;

namespace Atrium.Media;

/// <summary>Where the media tools and the cache are.</summary>
public sealed class MediaOptions
{
    public const string FfmpegKey = "ATRIUM_FFMPEG";
    public const string FfprobeKey = "ATRIUM_FFPROBE";

    /// <summary>The cache location (DESIGN.md 3.4): everything here can be deleted and is made again.</summary>
    public required string CacheDir { get; init; }

    public string Ffmpeg { get; init; } = Environment.GetEnvironmentVariable(FfmpegKey) is { Length: > 0 } path ? path : "ffmpeg";

    public string Ffprobe { get; init; } = Environment.GetEnvironmentVariable(FfprobeKey) is { Length: > 0 } path ? path : "ffprobe";

    /// <summary>
    /// The zone a file's modification time is read in when a photo records
    /// no capture time (D-034). The container's TZ.
    /// </summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>Longest edge of the grid thumbnail; made for every photo and video.</summary>
    public int SmallSize { get; init; } = 400;

    /// <summary>Longest edge of the viewer image; made when first asked for.</summary>
    public int LargeSize { get; init; } = 1920;

    /// <summary>A probe or a frame grab that takes longer than this is stopped.</summary>
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public string ThumbnailDir => Path.Combine(CacheDir, "thumbs");

    public string TempDir => Path.Combine(CacheDir, "tmp");
}

/// <summary>A media tool failed or was not there. The message never contains the file's name.</summary>
public sealed class MediaToolException(string message, bool missing = false) : Exception(message)
{
    /// <summary>The tool is not installed.</summary>
    public bool Missing { get; } = missing;
}

/// <summary>
/// Runs FFmpeg and ffprobe. Arguments are passed as a list, never through a
/// shell, so no file name can become a command. Background work runs at
/// reduced process priority so the device stays responsive (DESIGN.md 5.4).
/// </summary>
public static class ToolRunner
{
    public static async Task<byte[]> RunAsync(
        string tool, IReadOnlyList<string> arguments, TimeSpan timeout, bool background, long maxOutput, CancellationToken ct)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new MediaToolException($"{tool} did not start.", missing: true);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new MediaToolException($"{tool} is not installed.", missing: true);
        }

        using (process)
        {
            if (background)
            {
                try
                {
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
                {
                    // It may already have finished, or the platform refuses; the work is the same.
                }
            }

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(timeout);
            var stderr = new StringBuilder();
            var errorTask = Task.Run(async () =>
            {
                var buffer = new char[4096];
                int read;
                while ((read = await process.StandardError.ReadAsync(buffer, timer.Token)) > 0)
                {
                    if (stderr.Length < 4000)
                    {
                        stderr.Append(buffer, 0, read);
                    }
                }
            }, CancellationToken.None);

            using var output = new MemoryStream();
            try
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, timer.Token)) > 0)
                {
                    if (output.Length + read > maxOutput)
                    {
                        throw new MediaToolException($"{tool} produced more output than expected.");
                    }

                    output.Write(buffer, 0, read);
                }

                await process.WaitForExitAsync(timer.Token);
                await errorTask.WaitAsync(timer.Token);
            }
            catch (Exception) when (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                ct.ThrowIfCancellationRequested();
                throw new MediaToolException($"{tool} did not finish in time.");
            }

            if (process.ExitCode != 0)
            {
                // The last line is the useful one; it says what was wrong, not with which file.
                var lines = stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                throw new MediaToolException($"{tool} failed ({process.ExitCode}): {(lines.Length > 0 ? lines[^1] : "no detail")}");
            }

            return output.ToArray();
        }
    }
}
