using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Atrium.Files.Uploads;

/// <summary>
/// The SHA-256 of each active upload, computed as its chunks pass through,
/// so the finished content need not be read again. Kept in memory only: if
/// a restart loses it, or a chunk fails halfway, the content is read back
/// once at the end instead. Correctness never depends on it.
/// </summary>
public sealed class UploadRunningHashes
{
    private readonly ConcurrentDictionary<Guid, State> _states = new();

    /// <summary>The hash to extend with the chunk at <paramref name="offset"/>, or null when it cannot be continued.</summary>
    public IncrementalHash? Continue(Guid uploadId, long offset)
    {
        if (_states.TryGetValue(uploadId, out var state))
        {
            if (state.Offset == offset)
            {
                return state.Hash;
            }

            Forget(uploadId);
            return null;
        }

        return offset == 0 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
    }

    /// <summary>Records that the hash now covers everything up to <paramref name="offset"/>.</summary>
    public void Advance(Guid uploadId, long offset, IncrementalHash? hash)
    {
        if (hash is null)
        {
            return;
        }

        _states[uploadId] = new State(offset, hash);
    }

    /// <summary>The final hash if it covers exactly <paramref name="length"/> bytes; null otherwise.</summary>
    public byte[]? Take(Guid uploadId, long length)
    {
        if (length == 0)
        {
            Forget(uploadId);
            return SHA256.HashData(ReadOnlySpan<byte>.Empty);
        }

        if (!_states.TryRemove(uploadId, out var state))
        {
            return null;
        }

        using (state.Hash)
        {
            return state.Offset == length ? state.Hash.GetHashAndReset() : null;
        }
    }

    public void Forget(Guid uploadId)
    {
        if (_states.TryRemove(uploadId, out var state))
        {
            state.Hash.Dispose();
        }
    }

    private sealed record State(long Offset, IncrementalHash Hash);
}

/// <summary>Passes a stream through while feeding what it reads to up to two hashes.</summary>
internal sealed class TeeHashStream(Stream inner, IncrementalHash? first, IncrementalHash? second) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        Feed(buffer[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        Feed(buffer.Span[..read]);
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void Feed(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        first?.AppendData(data);
        second?.AppendData(data);
    }
}
