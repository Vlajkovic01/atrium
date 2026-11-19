namespace Atrium.Media.Streaming;

/// <summary>
/// The keyframes of a Matroska or WebM file from its cues, the index muxers
/// write for every keyframe of the video, read without reading the media.
/// The segment's seek head says where the cues, the track list and the
/// timestamp scale are; without one, the segment's elements are walked by
/// their headers.
/// </summary>
public static class MatroskaKeyframes
{
    private const uint Ebml = 0x1A45DFA3;
    private const uint Segment = 0x18538067;
    private const uint SeekHead = 0x114D9B74;
    private const uint Seek = 0x4DBB;
    private const uint SeekId = 0x53AB;
    private const uint SeekPosition = 0x53AC;
    private const uint Info = 0x1549A966;
    private const uint TimestampScale = 0x2AD7B1;
    private const uint Tracks = 0x1654AE6B;
    private const uint TrackEntry = 0xAE;
    private const uint TrackNumber = 0xD7;
    private const uint TrackType = 0x83;
    private const uint Cues = 0x1C53BB6B;
    private const uint CuePoint = 0xBB;
    private const uint CueTime = 0xB3;
    private const uint CueTrackPositions = 0xB7;
    private const uint CueTrack = 0xF7;

    /// <summary>Cues larger than this are not read; a film's are tens of kilobytes.</summary>
    public const int MaxCues = 32 * 1024 * 1024;

    /// <summary>Elements of the segment walked when there is no seek head.</summary>
    private const int MaxWalk = 50_000;

    private const int MaxHeader = 12;

    /// <returns>
    /// Presentation times of the video keyframes in microseconds, ascending;
    /// null when the file has no cues for its video, so another way must be
    /// used.
    /// </returns>
    public static async Task<IReadOnlyList<long>?> ReadAsync(RangeReader file, CancellationToken ct)
    {
        var first = await ElementAt(file, 0, ct);
        if (first is not { Id: Ebml } ebml)
        {
            return null;
        }

        var segment = await ElementAt(file, ebml.End, ct);
        if (segment is not { Id: Segment } seg)
        {
            return null;
        }

        // Where the top-level elements are, by the seek head or by walking.
        var positions = new Dictionary<uint, long>();
        var segmentEnd = seg.Size is null ? file.Size : Math.Min(file.Size, seg.End);
        var offset = seg.DataStart;
        for (var walked = 0; walked < MaxWalk && offset < segmentEnd; walked++)
        {
            var element = await ElementAt(file, offset, ct);
            if (element is null || element.Size is null)
            {
                break;
            }

            positions.TryAdd(element.Id, element.Offset);
            if (element.Id == SeekHead)
            {
                foreach (var (id, position) in await ReadSeekHead(file, element, ct))
                {
                    positions.TryAdd(id, seg.DataStart + position);
                }
            }

            if (positions.ContainsKey(Cues) && positions.ContainsKey(Tracks) && positions.ContainsKey(Info))
            {
                break;
            }

            offset = element.End;
        }

        if (!positions.TryGetValue(Cues, out var cuesAt) || !positions.TryGetValue(Tracks, out var tracksAt))
        {
            return null;
        }

        var scale = positions.TryGetValue(Info, out var infoAt) ? await ReadScale(file, infoAt, ct) : 1_000_000;
        var video = await ReadVideoTrack(file, tracksAt, ct);
        if (video is null)
        {
            return null;
        }

        var cues = await ElementAt(file, cuesAt, ct);
        if (cues is not { Id: Cues, Size: { } cuesSize } || cuesSize > MaxCues)
        {
            return null;
        }

        var body = await file.ReadAsync(cues.DataStart, (int)cuesSize, ct);
        var times = new List<long>();
        foreach (var point in Children(body, CuePoint))
        {
            ulong? time = null;
            var forVideo = false;
            foreach (var (id, value) in Elements(point))
            {
                if (id == CueTime)
                {
                    time = Unsigned(value);
                }
                else if (id == CueTrackPositions && Children(value, CueTrack).Any(t => Unsigned(t) == video))
                {
                    forVideo = true;
                }
            }

            if (time is { } t && forVideo)
            {
                times.Add((long)(t * scale / 1000));
            }
        }

        if (times.Count == 0)
        {
            return null;
        }

        times.Sort();
        return times;
    }

    private sealed record Element(uint Id, long Offset, long DataStart, long? Size)
    {
        public long End => DataStart + (Size ?? 0);
    }

    private static async Task<Element?> ElementAt(RangeReader file, long offset, CancellationToken ct)
    {
        var header = await file.ReadAsync(offset, MaxHeader, ct);
        if (!TryId(header, out var id, out var idLength) || !TrySize(header.AsSpan(idLength), out var size, out var sizeLength))
        {
            return null;
        }

        return new Element(id, offset, offset + idLength + sizeLength, size);
    }

    private static async Task<List<(uint Id, long Position)>> ReadSeekHead(RangeReader file, Element seekHead, CancellationToken ct)
    {
        var result = new List<(uint, long)>();
        if (seekHead.Size is not { } size || size > 1024 * 1024)
        {
            return result;
        }

        var body = await file.ReadAsync(seekHead.DataStart, (int)size, ct);
        foreach (var seek in Children(body, Seek))
        {
            uint? id = null;
            ulong? position = null;
            foreach (var (childId, value) in Elements(seek))
            {
                if (childId == SeekId && value.Length is > 0 and <= 4)
                {
                    id = (uint)Unsigned(value);
                }
                else if (childId == SeekPosition)
                {
                    position = Unsigned(value);
                }
            }

            if (id is { } i && position is { } p)
            {
                result.Add((i, (long)p));
            }
        }

        return result;
    }

    private static async Task<ulong> ReadScale(RangeReader file, long offset, CancellationToken ct)
    {
        var info = await ElementAt(file, offset, ct);
        if (info is not { Id: Info, Size: { } size } || size > 1024 * 1024)
        {
            return 1_000_000;
        }

        var body = await file.ReadAsync(info.DataStart, (int)size, ct);
        return Children(body, TimestampScale).Select(Unsigned).FirstOrDefault(v => v > 0) is var scale and > 0 ? scale : 1_000_000;
    }

    private static async Task<ulong?> ReadVideoTrack(RangeReader file, long offset, CancellationToken ct)
    {
        var tracks = await ElementAt(file, offset, ct);
        if (tracks is not { Id: Tracks, Size: { } size } || size > 4 * 1024 * 1024)
        {
            return null;
        }

        var body = await file.ReadAsync(tracks.DataStart, (int)size, ct);
        foreach (var entry in Children(body, TrackEntry))
        {
            ulong? number = null;
            ulong? type = null;
            foreach (var (id, value) in Elements(entry))
            {
                if (id == TrackNumber)
                {
                    number = Unsigned(value);
                }
                else if (id == TrackType)
                {
                    type = Unsigned(value);
                }
            }

            if (type == 1 && number is not null)
            {
                return number;
            }
        }

        return null;
    }

    private static List<byte[]> Children(byte[] body, uint wanted) =>
        Elements(body).Where(e => e.Id == wanted).Select(e => e.Value).ToList();

    /// <summary>The child elements of a master element's body, each with its value.</summary>
    private static List<(uint Id, byte[] Value)> Elements(byte[] body)
    {
        var result = new List<(uint, byte[])>();
        var offset = 0;
        while (offset < body.Length)
        {
            var rest = body.AsSpan(offset);
            if (!TryId(rest, out var id, out var idLength) || !TrySize(rest[idLength..], out var size, out var sizeLength)
                || size is null || offset + idLength + sizeLength + size > body.Length)
            {
                break;
            }

            var start = offset + idLength + sizeLength;
            result.Add((id, body.AsSpan(start, (int)size.Value).ToArray()));
            offset = start + (int)size.Value;
        }

        return result;
    }

    /// <summary>An element ID, marker bit kept, as the specification writes IDs.</summary>
    private static bool TryId(ReadOnlySpan<byte> data, out uint id, out int length)
    {
        id = 0;
        length = data.Length == 0 ? 0 : LeadingLength(data[0]);
        if (length is 0 or > 4 || data.Length < length)
        {
            return false;
        }

        for (var i = 0; i < length; i++)
        {
            id = (id << 8) | data[i];
        }

        return true;
    }

    /// <summary>An element size, marker bit removed; null when the size is unknown (all ones).</summary>
    private static bool TrySize(ReadOnlySpan<byte> data, out long? size, out int length)
    {
        size = null;
        length = data.Length == 0 ? 0 : LeadingLength(data[0]);
        if (length is 0 || data.Length < length)
        {
            return false;
        }

        ulong value = (ulong)(data[0] & (0xFF >> length));
        var allOnes = value == (ulong)(0xFF >> length);
        for (var i = 1; i < length; i++)
        {
            value = (value << 8) | data[i];
            allOnes &= data[i] == 0xFF;
        }

        if (!allOnes)
        {
            if (value > long.MaxValue)
            {
                return false;
            }

            size = (long)value;
        }

        return true;
    }

    private static int LeadingLength(byte first)
    {
        for (var i = 0; i < 8; i++)
        {
            if ((first & (0x80 >> i)) != 0)
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static ulong Unsigned(byte[] value)
    {
        ulong result = 0;
        foreach (var b in value.AsSpan(0, Math.Min(value.Length, 8)))
        {
            result = (result << 8) | b;
        }

        return result;
    }
}
