using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using NetVips;

namespace Atrium.Media;

/// <summary>Turns a picture file into an upright WebP thumbnail without metadata.</summary>
public interface IImageEncoder
{
    /// <exception cref="UnsupportedMediaException">The file is not a picture this libvips can read.</exception>
    /// <exception cref="MediaToolException">libvips is missing, or the decoder process failed.</exception>
    Task<byte[]> ThumbnailAsync(string path, int size, CancellationToken ct);
}

/// <summary>
/// libvips in the calling process. Used by the decoder process itself, by
/// the command-line check and by tests; never by the server (D-039).
/// </summary>
public sealed class InProcessImageEncoder : IImageEncoder
{
    static InProcessImageEncoder()
    {
        // A cached image would keep its source open after the thumbnail is made.
        try
        {
            Cache.Max = 0;
            Cache.MaxFiles = 0;
        }
        catch (Exception e) when (IsLibvipsMissing(e))
        {
        }
    }

    public Task<byte[]> ThumbnailAsync(string path, int size, CancellationToken ct) => Task.FromResult(Thumbnail(path, size));

    /// <summary>
    /// libvips reads through a stream opened with full sharing and closed at
    /// once, never by path: given a path it may keep the file mapped after
    /// it is done, which on Windows stops other programs writing the file.
    /// </summary>
    public static byte[] Thumbnail(string path, int size)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920);

            // Thumbnail turns the picture upright by its EXIF orientation.
            using var image = Image.ThumbnailStream(stream, size, height: size, size: Enums.Size.Down, failOn: Enums.FailOn.None);

            // No metadata is kept: not the EXIF of the original, not its location (D-031).
            return image.WebpsaveBuffer(q: 78, keep: Enums.ForeignKeep.None);
        }
        catch (VipsException e)
        {
            throw new UnsupportedMediaException(e.Message);
        }
        catch (Exception e) when (IsLibvipsMissing(e))
        {
            throw new MediaToolException("libvips is not installed.", missing: true);
        }
    }

    public static void Configure(int concurrency)
    {
        try
        {
            NetVips.NetVips.Concurrency = Math.Max(1, concurrency);
        }
        catch (Exception e) when (IsLibvipsMissing(e))
        {
        }
    }

    public static bool IsLibvipsMissing(Exception e) => e is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException;
}

/// <summary>One request to the decoder process, and its answer, each one line of JSON.</summary>
internal sealed record ImageRequest(string Path, int Size);

internal sealed record ImageResponse(bool Ok, string? Webp, bool Unsupported, bool Missing, string? Error);

/// <summary>
/// <c>Atrium.Api --image-worker</c>: the process that runs libvips for the
/// server. It reads requests from standard input until it closes, which
/// also happens when the server goes away, and writes one answer per
/// request. A decoder that crashes takes down only this process.
/// </summary>
public static class ImageWorker
{
    public const string Argument = "--image-worker";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(TextReader input, TextWriter output, int concurrency)
    {
        InProcessImageEncoder.Configure(concurrency);
        while (await input.ReadLineAsync() is { } line)
        {
            ImageResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<ImageRequest>(line, Json)
                    ?? throw new InvalidOperationException("An empty request.");
                var webp = InProcessImageEncoder.Thumbnail(request.Path, request.Size);
                response = new ImageResponse(true, Convert.ToBase64String(webp), false, false, null);
            }
            catch (UnsupportedMediaException e)
            {
                response = new ImageResponse(false, null, true, false, e.Message);
            }
            catch (MediaToolException e)
            {
                response = new ImageResponse(false, null, false, e.Missing, e.Message);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                response = new ImageResponse(false, null, true, false, e.Message);
            }

            await output.WriteLineAsync(JsonSerializer.Serialize(response, Json));
            await output.FlushAsync();
        }

        return 0;
    }

    /// <summary>
    /// How to start the decoder process from the running server: the same
    /// program with <see cref="Argument"/>. Null when this process is not
    /// the server (tests host it inside their own process).
    /// </summary>
    public static ProcessStartInfo? StartInfoForThisServer(int concurrency)
    {
        var entry = Assembly.GetEntryAssembly();
        var host = Environment.ProcessPath;
        if (entry?.GetName().Name != "Atrium.Api" || host is null)
        {
            return null;
        }

        var start = new ProcessStartInfo(host)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(entry.Location);
        }

        start.ArgumentList.Add(Argument);
        start.ArgumentList.Add(concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return start;
    }
}

/// <summary>
/// Sends thumbnails to decoder processes (D-039). A few run side by side;
/// one that crashes, hangs past the time limit or answers nonsense is
/// killed and replaced, and the server carries on.
/// </summary>
public sealed class WorkerImageEncoder : IImageEncoder, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ProcessStartInfo _start;
    private readonly TimeSpan _timeout;
    private readonly Channel<Worker?> _idle;

    public WorkerImageEncoder(ProcessStartInfo start, int processes, TimeSpan timeout)
    {
        _start = start;
        _timeout = timeout;

        // Free slots hold null until a process is needed in them.
        _idle = Channel.CreateUnbounded<Worker?>();
        for (var i = 0; i < Math.Max(1, processes); i++)
        {
            _idle.Writer.TryWrite(null);
        }
    }

    public async Task<byte[]> ThumbnailAsync(string path, int size, CancellationToken ct)
    {
        var worker = await _idle.Reader.ReadAsync(ct);
        try
        {
            worker ??= Worker.Start(_start);
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(_timeout);
            var response = await worker.AskAsync(new ImageRequest(path, size), timer.Token);
            if (response.Ok && response.Webp is { } webp)
            {
                return Convert.FromBase64String(webp);
            }

            if (response.Missing)
            {
                throw new MediaToolException(response.Error ?? "libvips is not installed.", missing: true);
            }

            throw new UnsupportedMediaException(response.Error ?? "The picture could not be decoded.");
        }
        catch (Exception e) when (e is IOException or JsonException or OperationCanceledException or InvalidOperationException or FormatException
            && worker is not null)
        {
            // Crashed, hung or confused: this process is done for.
            worker?.Dispose();
            worker = null;
            ct.ThrowIfCancellationRequested();
            throw new MediaToolException($"The image decoder stopped: {e.GetType().Name}.");
        }
        finally
        {
            _idle.Writer.TryWrite(worker);
        }
    }

    public void Dispose()
    {
        while (_idle.Reader.TryRead(out var worker))
        {
            worker?.Dispose();
        }
    }

    private sealed class Worker : IDisposable
    {
        private readonly Process _process;

        private Worker(Process process) => _process = process;

        public static Worker Start(ProcessStartInfo start)
        {
            try
            {
                return new Worker(Process.Start(start) ?? throw new MediaToolException("The image decoder did not start."));
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                throw new MediaToolException($"The image decoder did not start: {e.Message}");
            }
        }

        public async Task<ImageResponse> AskAsync(ImageRequest request, CancellationToken ct)
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Json).AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);
            var line = await _process.StandardOutput.ReadLineAsync(ct)
                ?? throw new IOException("The image decoder ended.");
            return JsonSerializer.Deserialize<ImageResponse>(line, Json) ?? throw new JsonException("An empty answer.");
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
        }
    }
}
