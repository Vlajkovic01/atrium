using System.Buffers.Binary;
using System.Text;

namespace Atrium.Media.Streaming;

/// <summary>
/// The keyframes of an MP4 or QuickTime file from its sample tables, read
/// without reading the media: the file's boxes are walked by their
/// headers to the movie box, which alone is read. The video track's sync
/// samples (stss) are placed in time by the decoding times (stts), the
/// composition offsets (ctts) and the first edit (elst), as a player
/// presents them.
/// </summary>
public static class Mp4Keyframes
{
    /// <summary>A movie box larger than this is not read; a two-hour film's is a few megabytes.</summary>
    public const int MaxMovieBox = 64 * 1024 * 1024;

    /// <summary>Top-level boxes walked before giving up.</summary>
    private const int MaxTopLevelBoxes = 10_000;

    /// <returns>
    /// Presentation times of the keyframes in microseconds, ascending; null
    /// when the file has no such index (fragmented MP4, no video track, or
    /// damaged), so another way must be used.
    /// </returns>
    public static async Task<IReadOnlyList<long>?> ReadAsync(RangeReader file, CancellationToken ct)
    {
        long offset = 0;
        for (var i = 0; i < MaxTopLevelBoxes && offset + 8 <= file.Size; i++)
        {
            var header = await file.ReadAsync(offset, 16, ct);
            if (header.Length < 8)
            {
                return null;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            var headerSize = 8;
            if (size == 1)
            {
                if (header.Length < 16)
                {
                    return null;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = file.Size - offset;
            }

            if (size < headerSize || offset + size > file.Size)
            {
                return null;
            }

            if (type == "moov")
            {
                if (size - headerSize > MaxMovieBox)
                {
                    return null;
                }

                var moov = await file.ReadAsync(offset + headerSize, (int)(size - headerSize), ct);
                return moov.Length == size - headerSize ? FromMovie(moov) : null;
            }

            // Fragments carry their own sample tables, which this does not read.
            if (type == "moof")
            {
                return null;
            }

            offset += size;
        }

        return null;
    }

    /// <summary>The keyframes from a movie box's contents.</summary>
    public static IReadOnlyList<long>? FromMovie(ReadOnlySpan<byte> moov)
    {
        if (Find(moov, "mvex").Length > 0)
        {
            return null;
        }

        foreach (var trak in Children(moov, "trak"))
        {
            var mdia = Find(trak, "mdia");
            var hdlr = Find(mdia, "hdlr");
            if (hdlr.Length < 12 || Encoding.ASCII.GetString(hdlr.Slice(8, 4)) != "vide")
            {
                continue;
            }

            var timescale = Timescale(Find(mdia, "mdhd"));
            var stbl = Find(Find(mdia, "minf"), "stbl");
            if (timescale <= 0 || stbl.Length == 0)
            {
                return null;
            }

            return Keyframes(stbl, timescale, FirstEditMediaTime(Find(Find(trak, "edts"), "elst")));
        }

        return null;
    }

    private static IReadOnlyList<long>? Keyframes(ReadOnlySpan<byte> stbl, long timescale, long editMediaTime)
    {
        var stts = Find(stbl, "stts");
        if (stts.Length < 8)
        {
            return null;
        }

        // Sample numbers are 1-based. Without stss every sample is a keyframe.
        var stss = Find(stbl, "stss");
        HashSet<uint>? sync = null;
        if (stss.Length >= 8)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(stss[4..]);
            if (stss.Length < 8 + ((long)count * 4))
            {
                return null;
            }

            sync = new HashSet<uint>((int)Math.Min(count, 1_000_000));
            for (var i = 0; i < count; i++)
            {
                sync.Add(BinaryPrimitives.ReadUInt32BigEndian(stss[(8 + (i * 4))..]));
            }
        }

        var offsets = CompositionOffsets(Find(stbl, "ctts"));
        var times = new List<long>();
        var entries = BinaryPrimitives.ReadUInt32BigEndian(stts[4..]);
        if (stts.Length < 8 + ((long)entries * 8))
        {
            return null;
        }

        long decodeTime = 0;
        uint sample = 1;
        var offsetIndex = 0;
        var offsetLeft = offsets.Count > 0 ? offsets[0].Count : 0;
        for (var e = 0; e < entries; e++)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(stts[(8 + (e * 8))..]);
            var delta = BinaryPrimitives.ReadUInt32BigEndian(stts[(12 + (e * 8))..]);
            for (uint s = 0; s < count; s++, sample++)
            {
                long composition = 0;
                if (offsetIndex < offsets.Count)
                {
                    composition = offsets[offsetIndex].Offset;
                    if (--offsetLeft == 0 && ++offsetIndex < offsets.Count)
                    {
                        offsetLeft = offsets[offsetIndex].Count;
                    }
                }

                if (sync is null || sync.Contains(sample))
                {
                    var presented = decodeTime + composition - editMediaTime;
                    times.Add(presented * 1_000_000 / timescale);
                }

                decodeTime += delta;
            }
        }

        times.Sort();
        return times;
    }

    private static List<(uint Count, long Offset)> CompositionOffsets(ReadOnlySpan<byte> ctts)
    {
        var result = new List<(uint, long)>();
        if (ctts.Length < 8)
        {
            return result;
        }

        var entries = BinaryPrimitives.ReadUInt32BigEndian(ctts[4..]);
        for (var i = 0; i < entries && 16 + (i * 8) <= ctts.Length; i++)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(ctts[(8 + (i * 8))..]);

            // Read as signed in both versions: version 0 is unsigned by the
            // specification, but writers put negative offsets there too.
            result.Add((count, BinaryPrimitives.ReadInt32BigEndian(ctts[(12 + (i * 8))..])));
        }

        return result;
    }

    /// <summary>Where the presentation starts in the track's own time: the first edit that is not empty.</summary>
    private static long FirstEditMediaTime(ReadOnlySpan<byte> elst)
    {
        if (elst.Length < 8)
        {
            return 0;
        }

        var version = elst[0];
        var entries = BinaryPrimitives.ReadUInt32BigEndian(elst[4..]);
        var entrySize = version == 1 ? 20 : 12;
        for (var i = 0; i < entries && 8 + ((i + 1) * entrySize) <= elst.Length; i++)
        {
            var entry = elst[(8 + (i * entrySize))..];
            var mediaTime = version == 1
                ? BinaryPrimitives.ReadInt64BigEndian(entry[8..])
                : BinaryPrimitives.ReadInt32BigEndian(entry[4..]);
            if (mediaTime >= 0)
            {
                return mediaTime;
            }
        }

        return 0;
    }

    private static long Timescale(ReadOnlySpan<byte> mdhd)
    {
        if (mdhd.Length < 24)
        {
            return 0;
        }

        // Version 1 has 64-bit creation and modification times before it.
        return BinaryPrimitives.ReadUInt32BigEndian(mdhd[(mdhd[0] == 1 ? 20 : 12)..]);
    }

    /// <summary>The contents of the first child box of the given type, or empty.</summary>
    private static ReadOnlySpan<byte> Find(ReadOnlySpan<byte> parent, string type)
    {
        var offset = 0;
        while (offset + 8 <= parent.Length)
        {
            var (size, headerSize) = BoxSize(parent[offset..]);
            if (size < headerSize || offset + size > parent.Length)
            {
                return [];
            }

            if (Encoding.ASCII.GetString(parent.Slice(offset + 4, 4)) == type)
            {
                return parent.Slice(offset + headerSize, (int)size - headerSize);
            }

            offset += (int)size;
        }

        return [];
    }

    private static List<byte[]> Children(ReadOnlySpan<byte> parent, string type)
    {
        var result = new List<byte[]>();
        var offset = 0;
        while (offset + 8 <= parent.Length)
        {
            var (size, headerSize) = BoxSize(parent[offset..]);
            if (size < headerSize || offset + size > parent.Length)
            {
                break;
            }

            if (Encoding.ASCII.GetString(parent.Slice(offset + 4, 4)) == type)
            {
                result.Add(parent.Slice(offset + headerSize, (int)size - headerSize).ToArray());
            }

            offset += (int)size;
        }

        return result;
    }

    private static (long Size, int HeaderSize) BoxSize(ReadOnlySpan<byte> box)
    {
        long size = BinaryPrimitives.ReadUInt32BigEndian(box);
        if (size == 1)
        {
            return box.Length >= 16 ? ((long)BinaryPrimitives.ReadUInt64BigEndian(box[8..]), 16) : (0, 16);
        }

        return size == 0 ? (box.Length, 8) : (size, 8);
    }
}
