using Atrium.Core.Files;

namespace Atrium.Files;

/// <summary>
/// Told about files whose content is new or changed: uploads, replaced
/// content, and what a scan of an external library found. Media
/// processing listens, to read metadata and make thumbnails.
/// </summary>
public interface INodeContentObserver
{
    /// <summary>Must be quick and must not fail the change it is told about; queue work instead of doing it.</summary>
    Task ContentChangedAsync(Library library, IReadOnlyList<Node> files, CancellationToken ct);
}
