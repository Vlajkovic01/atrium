namespace Atrium.Core.Media;

/// <summary>
/// Where a person is in a video, so it resumes on any of their devices
/// (DESIGN.md 7.4, D-068). One row per person and file; the last device to
/// save wins.
/// </summary>
public sealed class PlaybackState
{
    public Guid UserId { get; init; }

    public Guid NodeId { get; init; }

    public long PositionMs { get; set; }

    /// <summary>The length the player saw, which can differ a little from the file's.</summary>
    public long DurationMs { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
