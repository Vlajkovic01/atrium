namespace Atrium.Core.Files;

/// <summary>
/// One line of the change feed (D-047): "look at this file or folder
/// again". It says nothing about what changed; a reader gets the item as it
/// is now, or learns that it is gone. An entry about a folder speaks for
/// everything below it: trashing, restoring or removing a folder writes one
/// entry, not one per item inside.
/// </summary>
public sealed class ChangeEntry
{
    /// <summary>
    /// Assigned by the database, rising with every entry and never reused.
    /// Entries become visible in this order, so a reader that has seen
    /// everything up to a sequence never misses an earlier one.
    /// </summary>
    public long Sequence { get; init; }

    /// <summary>Null only for a marker: an entry about nothing, written so that an empty feed has a place to start from.</summary>
    public Guid? LibraryId { get; init; }

    /// <summary>Not a foreign key: the entry outlives the item it reports as removed.</summary>
    public Guid? NodeId { get; init; }

    public DateTimeOffset At { get; init; }
}
