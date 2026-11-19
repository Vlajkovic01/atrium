using System.Diagnostics;
using System.Globalization;
using Atrium.Core.Media;
using Atrium.Media.Playback;
using Microsoft.Extensions.Logging;

namespace Atrium.Media.Streaming;

/// <summary>A streamed segment could not be made.</summary>
public sealed class StreamFailedException(string message) : Exception(message);

/// <summary>
/// One person streaming one video (DESIGN.md 7.4): the plan of segments,
/// what is sent, and at most one FFmpeg producing segments. Segments that
/// are finished stay, since every run cuts at the same boundaries; a
/// request for a segment that no run will reach soon starts a run there,
/// which is what makes seeking fast.
/// </summary>
public sealed class StreamSession : IAsyncDisposable
{
    /// <summary>A segment this far ahead of the running FFmpeg is waited for rather than started afresh.</summary>
    public const int WaitAhead = 3;

    /// <summary>A run this many segments ahead of what is asked for stops; a later request starts it again.</summary>
    public const int RunAhead = 20;

    /// <summary>
    /// Finished segments a stream keeps, about twenty minutes of video; past
    /// that, those far behind the player go, so a film watched to the end
    /// does not keep all of itself in the cache.
    /// </summary>
    public const int MaxKept = 200;

    /// <summary>Segments behind the player that stay, for seeking back.</summary>
    public const int KeepBehind = 100;

    private static readonly TimeSpan SegmentWait = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly HashSet<int> _finished = [];
    private readonly ILogger _logger;
    private readonly string _ffmpeg;
    private readonly Func<CancellationToken, Task<string>> _input;
    private Run? _run;
    private int _runs;
    private byte[]? _init;
    private Dictionary<uint, uint>? _timescales;

    public StreamSession(
        string id,
        Guid userId,
        Guid authSessionId,
        Guid nodeId,
        PlaybackDecision decision,
        StreamOutput output,
        IReadOnlyList<Segment> segments,
        MediaStream video,
        MediaStream? audio,
        string directory,
        int startIndex,
        string ffmpeg,
        Func<CancellationToken, Task<string>> input,
        TimeProvider time,
        ILogger logger)
    {
        Id = id;
        UserId = userId;
        AuthSessionId = authSessionId;
        NodeId = nodeId;
        Decision = decision;
        Output = output;
        Segments = segments;
        Video = video;
        Audio = audio;
        Directory = directory;
        StartIndex = Math.Clamp(startIndex, 0, Math.Max(0, segments.Count - 1));
        _ffmpeg = ffmpeg;
        _input = input;
        Time = time;
        _logger = logger;
        LastUsed = time.GetUtcNow();
        System.IO.Directory.CreateDirectory(directory);
    }

    public string Id { get; }

    public Guid UserId { get; }

    public Guid AuthSessionId { get; }

    public Guid NodeId { get; }

    public PlaybackDecision Decision { get; }

    public StreamOutput Output { get; }

    public IReadOnlyList<Segment> Segments { get; }

    public MediaStream Video { get; }

    public MediaStream? Audio { get; }

    public string Directory { get; }

    /// <summary>Where watching starts, for a resume: the init segment is taken from it, so nothing before it is made.</summary>
    public int StartIndex { get; }

    public TimeProvider Time { get; }

    public DateTimeOffset LastUsed { get; private set; }

    /// <summary>Whether this session encodes video, which the limits count apart from copying.</summary>
    public bool Encodes => !Output.CopyVideo;

    public bool Running => _run is { Process.HasExited: false };

    /// <summary>The playlist counts from the first segment; segments are on the file's clock.</summary>
    public long OriginUs => Segments.Count > 0 ? Segments[0].StartUs : 0;

    /// <summary>The init segment: the header of the first segment made, every one has the same.</summary>
    public async Task<byte[]> InitAsync(CancellationToken ct)
    {
        if (_init is null)
        {
            await SegmentFileAsync(_finished.Count > 0 ? _finished.Min() : StartIndex, ct);
        }

        return _init!;
    }

    /// <summary>Segment <paramref name="index"/>, moved to its time in the playlist.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The plan has no such segment.</exception>
    /// <exception cref="StreamFailedException">FFmpeg could not make it.</exception>
    public async Task<byte[]> SegmentAsync(int index, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Segments.Count);
        var path = await SegmentFileAsync(index, ct);
        var file = await File.ReadAllBytesAsync(path, ct);
        return Fmp4.Media(file, _timescales!, Segments[index].StartUs - OriginUs);
    }

    /// <summary>Stops FFmpeg; finished segments stay for a later request.</summary>
    public async Task StopAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await StopRunAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "A stream's segments could not be deleted; the cache sweep removes them later.");
        }

        _lock.Dispose();
    }

    private async Task<string> SegmentFileAsync(int index, CancellationToken ct)
    {
        LastUsed = Time.GetUtcNow();
        var path = Path.Combine(Directory, $"{index.ToString(CultureInfo.InvariantCulture)}.mp4");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(SegmentWait);
        await _lock.WaitAsync(ct);
        try
        {
            CollectFinished();
            if (!_finished.Contains(index))
            {
                var reaches = _run is { Process.HasExited: false } run && index >= run.First && index <= Math.Max(run.First, run.Last + 1) + WaitAhead;
                if (!reaches)
                {
                    await StartRunAsync(index, ct);
                }
            }

            while (!_finished.Contains(index))
            {
                if (_run is not { } current || current.Process.HasExited)
                {
                    CollectFinished();
                    if (_finished.Contains(index))
                    {
                        break;
                    }

                    throw new StreamFailedException($"FFmpeg stopped before segment {index}: {_run?.Errors.Trim()}");
                }

                _lock.Release();
                try
                {
                    await Task.Delay(100, deadline.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new StreamFailedException($"Segment {index} took longer than {SegmentWait.TotalSeconds} seconds.");
                }
                finally
                {
                    await _lock.WaitAsync(CancellationToken.None);
                }

                CollectFinished();
            }

            if (_init is null)
            {
                var header = Fmp4.Init(await File.ReadAllBytesAsync(path, ct));
                _timescales = Fmp4.Timescales(header);
                _init = header;
            }

            if (_finished.Count > MaxKept)
            {
                foreach (var old in _finished.Where(n => n < index - KeepBehind || n > index + MaxKept).ToList())
                {
                    TryDelete(Path.Combine(Directory, $"{old.ToString(CultureInfo.InvariantCulture)}.mp4"));
                    _finished.Remove(old);
                }
            }

            // Far ahead of the player: stop, and start again when it catches up.
            if (_run is { } ahead && ahead.Last - index > RunAhead)
            {
                await StopRunAsync();
            }

            return path;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The bytes this stream keeps on disk.</summary>
    public long SizeOnDisk()
    {
        try
        {
            return new DirectoryInfo(Directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Moves the segments the running FFmpeg has finished into the session's folder.</summary>
    private void CollectFinished()
    {
        if (_run is not { } run)
        {
            return;
        }

        var list = Path.Combine(run.Directory, "segments.csv");
        string[] lines;
        try
        {
            using var stream = new FileStream(list, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        foreach (var line in lines)
        {
            var name = line.Split(',')[0];
            if (!int.TryParse(Path.GetFileNameWithoutExtension(name), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index < 0 || index >= Segments.Count || _finished.Contains(index))
            {
                continue;
            }

            var target = Path.Combine(Directory, $"{index.ToString(CultureInfo.InvariantCulture)}.mp4");
            File.Move(Path.Combine(run.Directory, $"{index.ToString(CultureInfo.InvariantCulture)}.mp4"), target, overwrite: true);
            _finished.Add(index);
            run.Last = Math.Max(run.Last, index);
        }
    }

    private async Task StartRunAsync(int first, CancellationToken ct)
    {
        await StopRunAsync();
        var directory = Path.Combine(Directory, $"run{(++_runs).ToString(CultureInfo.InvariantCulture)}");
        System.IO.Directory.CreateDirectory(directory);
        var input = await _input(ct);
        var start = new ProcessStartInfo(_ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in Output.Arguments(input, Segments, first, directory))
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start) ?? throw new StreamFailedException("FFmpeg did not start.");
        var run = new Run(process, first, directory);
        process.ErrorDataReceived += (_, e) => run.Error(e.Data);
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        _run = run;
        _logger.LogInformation("Streaming {Method} from segment {First} with run {Run}.", Decision.Method, first, _runs);
    }

    private async Task StopRunAsync()
    {
        if (_run is not { } run)
        {
            return;
        }

        CollectFinished();
        _run = null;
        if (!run.Process.HasExited)
        {
            try
            {
                run.Process.Kill(entireProcessTree: true);
                await run.Process.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
            }
        }

        run.Process.Dispose();
        try
        {
            System.IO.Directory.Delete(run.Directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class Run(Process process, int first, string directory)
    {
        private readonly System.Text.StringBuilder _errors = new();

        public Process Process { get; } = process;

        public int First { get; } = first;

        public string Directory { get; } = directory;

        /// <summary>The last segment this run finished; one before the first until it finishes one.</summary>
        public int Last { get; set; } = first - 1;

        public string Errors
        {
            get
            {
                lock (_errors)
                {
                    return _errors.ToString();
                }
            }
        }

        public void Error(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_errors)
            {
                if (_errors.Length < 4096)
                {
                    _errors.AppendLine(line);
                }
            }
        }
    }
}
