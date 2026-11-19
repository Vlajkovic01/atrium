using System.Buffers.Binary;
using System.Text;

namespace Atrium.Media.Streaming;

/// <summary>
/// The few operations on fragmented MP4 that HLS needs (D-062). FFmpeg
/// writes each segment as a file of its own, starting at time zero, with
/// its own header; the server serves one header (the init segment) and
/// moves each segment's fragments to the time the playlist gives it.
/// </summary>
public static class Fmp4
{
    /// <summary>The header of a segment file: its ftyp and moov boxes, the HLS init segment.</summary>
    public static byte[] Init(ReadOnlySpan<byte> file)
    {
        var init = new List<byte>();
        foreach (var (type, offset, size) in Boxes(file))
        {
            if (type is "ftyp" or "moov")
            {
                init.AddRange(file.Slice(offset, size));
            }
        }

        return [.. init];
    }

    /// <summary>The timescale of each track, by track ID, from the init segment.</summary>
    public static Dictionary<uint, uint> Timescales(ReadOnlySpan<byte> init)
    {
        var result = new Dictionary<uint, uint>();
        foreach (var (type, offset, size) in Boxes(init))
        {
            if (type != "moov")
            {
                continue;
            }

            var moov = init.Slice(offset + 8, size - 8);
            foreach (var (trakType, trakOffset, trakSize) in Boxes(moov))
            {
                if (trakType != "trak")
                {
                    continue;
                }

                var trak = moov.Slice(trakOffset + 8, trakSize - 8);
                var tkhd = Child(trak, "tkhd");
                var mdhd = Child(Child(trak, "mdia"), "mdhd");
                if (tkhd.Length < 24 || mdhd.Length < 24)
                {
                    continue;
                }

                // Version 1 boxes have 64-bit times before the fields read here.
                var trackId = BinaryPrimitives.ReadUInt32BigEndian(tkhd[(tkhd[0] == 1 ? 20 : 12)..]);
                var timescale = BinaryPrimitives.ReadUInt32BigEndian(mdhd[(mdhd[0] == 1 ? 20 : 12)..]);
                result[trackId] = timescale;
            }
        }

        return result;
    }

    /// <summary>
    /// The media of a segment file: its moof and mdat boxes only, the
    /// header and the random-access index at the end (mfra) left out, since
    /// a browser's own HLS player refuses anything else in a media segment.
    /// Every fragment is moved later by <paramref name="offsetUs"/>,
    /// each track in its own timescale. Decode times must be 64-bit (tfdt
    /// version 1), which FFmpeg writes.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not what FFmpeg writes.</exception>
    public static byte[] Media(ReadOnlySpan<byte> file, IReadOnlyDictionary<uint, uint> timescales, long offsetUs)
    {
        var output = new MemoryStream(file.Length);
        foreach (var (type, offset, size) in Boxes(file))
        {
            if (type is not ("moof" or "mdat"))
            {
                continue;
            }

            var box = file.Slice(offset, size).ToArray();
            if (type == "moof")
            {
                Shift(box, timescales, offsetUs);
            }

            output.Write(box);
        }

        return output.ToArray();
    }

    private static void Shift(byte[] moof, IReadOnlyDictionary<uint, uint> timescales, long offsetUs)
    {
        foreach (var (type, offset, size) in Boxes(moof.AsSpan(8)))
        {
            if (type != "traf")
            {
                continue;
            }

            var trafStart = 8 + offset;
            uint? trackId = null;
            foreach (var (childType, childOffset, childSize) in Boxes(moof.AsSpan(trafStart + 8, size - 8)))
            {
                var at = trafStart + 8 + childOffset;
                if (childType == "tfhd" && childSize >= 16)
                {
                    trackId = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(at + 12));
                }
                else if (childType == "tfdt")
                {
                    if (childSize < 20 || moof[at + 8] != 1)
                    {
                        throw new InvalidDataException("A fragment's decode time is not 64-bit.");
                    }

                    if (trackId is not { } id || !timescales.TryGetValue(id, out var timescale))
                    {
                        throw new InvalidDataException("A fragment names a track the init segment does not have.");
                    }

                    var field = moof.AsSpan(at + 12, 8);
                    var time = BinaryPrimitives.ReadUInt64BigEndian(field);
                    var shift = (ulong)Math.Round(offsetUs * (double)timescale / 1_000_000);
                    BinaryPrimitives.WriteUInt64BigEndian(field, time + shift);
                }
            }
        }
    }

    private static ReadOnlySpan<byte> Child(ReadOnlySpan<byte> parent, string type)
    {
        foreach (var (childType, offset, size) in Boxes(parent))
        {
            if (childType == type)
            {
                return parent.Slice(offset + 8, size - 8);
            }
        }

        return [];
    }

    /// <summary>The boxes at one level, as (type, offset, size). 64-bit sizes are not used by FFmpeg's fragments and stop the walk.</summary>
    private static List<(string Type, int Offset, int Size)> Boxes(ReadOnlySpan<byte> data)
    {
        var result = new List<(string, int, int)>();
        var offset = 0;
        while (offset + 8 <= data.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            if (size < 8 || offset + size > data.Length)
            {
                break;
            }

            result.Add((Encoding.ASCII.GetString(data.Slice(offset + 4, 4)), offset, (int)size));
            offset += (int)size;
        }

        return result;
    }
}
