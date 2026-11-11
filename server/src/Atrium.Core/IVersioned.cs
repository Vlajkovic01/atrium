namespace Atrium.Core;

/// <summary>
/// A row that clients edit. The version increases on every change so that a
/// write based on stale data is rejected instead of silently overwriting.
/// </summary>
public interface IVersioned
{
    int Version { get; set; }
}
