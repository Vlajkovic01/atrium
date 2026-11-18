namespace Atrium.Media.Streaming;

/// <summary>One HLS segment: where it starts in the video and how long it lasts, in microseconds.</summary>
public sealed record Segment(int Index, long StartUs, long DurationUs)
{
    public long EndUs => StartUs + DurationUs;
}

/// <summary>
/// Where a video is cut into HLS segments (DESIGN.md 7.4). The whole list
/// is known before anything is produced, so the full playlist is written at
/// once and any segment can be asked for first: a seek starts FFmpeg at
/// that segment and every boundary stays where the playlist said.
/// </summary>
public static class SegmentPlan
{
    /// <summary>The length segments aim for. Shorter means faster starts and seeks, longer fewer requests.</summary>
    public static readonly long TargetUs = 6_000_000;

    /// <summary>
    /// Segments of exactly the target length, for video encoded again, whose
    /// keyframes are placed at the boundaries.
    /// </summary>
    public static IReadOnlyList<Segment> Fixed(long durationUs, long targetUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetUs);
        var segments = new List<Segment>();
        for (long start = 0; start < durationUs; start += targetUs)
        {
            segments.Add(new Segment(segments.Count, start, Math.Min(targetUs, durationUs - start)));
        }

        return segments;
    }

    /// <summary>
    /// Segments for copied video, which can only be cut where a keyframe
    /// is: each ends at the first keyframe at least the target length after
    /// its start. A long stretch without keyframes makes one long segment.
    /// Times are on the file's own clock (<see cref="KeyframeIndex"/>): the
    /// first segment starts at the first keyframe, the last ends the video's
    /// duration later.
    /// </summary>
    public static IReadOnlyList<Segment> AtKeyframes(IReadOnlyList<long> keyframesUs, long durationUs, long targetUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetUs);
        var segments = new List<Segment>();
        var origin = keyframesUs.Count > 0 ? keyframesUs[0] : 0;
        var end = origin + durationUs;
        var start = origin;
        foreach (var keyframe in keyframesUs)
        {
            if (keyframe >= end)
            {
                break;
            }

            if (keyframe - start >= targetUs)
            {
                segments.Add(new Segment(segments.Count, start, keyframe - start));
                start = keyframe;
            }
        }

        if (end > start)
        {
            segments.Add(new Segment(segments.Count, start, end - start));
        }

        return segments;
    }

    /// <summary>The segment that holds a position, for starting where a person seeks or resumes.</summary>
    public static Segment? At(IReadOnlyList<Segment> segments, long positionUs)
    {
        if (segments.Count == 0)
        {
            return null;
        }

        var low = 0;
        var high = segments.Count - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (segments[middle].StartUs <= positionUs)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return segments[low];
    }
}
