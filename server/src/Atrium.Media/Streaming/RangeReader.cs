using Atrium.Files;
using Atrium.Storage;

namespace Atrium.Media.Streaming;

/// <summary>
/// Reads parts of a file at any offset, from any storage location, for
/// reading a container's index without reading the file. Reads go out in
/// blocks of at least <see cref="BlockSize"/> bytes and the last block is
/// kept, so walking many small headers costs few requests to S3.
/// </summary>
public sealed class RangeReader(ContentSource source)
{
    public const int BlockSize = 64 * 1024;

    private long _blockOffset = -1;
    private byte[] _block = [];

    public long Size => source.Size;

    /// <summary>
    /// Up to <paramref name="count"/> bytes from <paramref name="offset"/>;
    /// fewer only at the end of the file.
    /// </summary>
    public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset >= Size || count == 0)
        {
            return [];
        }

        count = (int)Math.Min(count, Size - offset);
        if (_blockOffset >= 0 && offset >= _blockOffset && offset + count <= _blockOffset + _block.Length)
        {
            return _block.AsSpan((int)(offset - _blockOffset), count).ToArray();
        }

        var length = (int)Math.Min(Math.Max(count, BlockSize), Size - offset);
        var data = new byte[length];
        await using (var stream = await source.OpenAsync(new ByteRange(offset, length), ct))
        {
            var read = 0;
            while (read < length)
            {
                var n = await stream.ReadAsync(data.AsMemory(read, length - read), ct);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            if (read < length)
            {
                Array.Resize(ref data, read);
            }
        }

        _blockOffset = offset;
        _block = data;
        return data.AsSpan(0, Math.Min(count, data.Length)).ToArray();
    }
}
