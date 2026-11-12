using Atrium.Core.Files;
using Atrium.Data;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Files;

/// <summary>Walking the tree up (ancestors) and down (subtrees), one level per query.</summary>
public static class NodeTree
{
    /// <summary>A tree deeper than this is treated as damaged rather than walked forever.</summary>
    public const int MaxDepth = 1000;

    private const int ChunkSize = 500;

    /// <summary>The ancestors of a node, nearest first, ending at the library root.</summary>
    public static async Task<List<Node>> AncestorsAsync(AtriumDbContext db, Node node, CancellationToken ct)
    {
        var result = new List<Node>();
        var parentId = node.ParentId;
        while (parentId is { } id)
        {
            if (result.Count >= MaxDepth)
            {
                throw new InvalidOperationException($"Node {node.Id} is nested deeper than {MaxDepth} levels.");
            }

            var parent = await db.Nodes.AsNoTracking().SingleAsync(n => n.Id == id, ct);
            result.Add(parent);
            parentId = parent.ParentId;
        }

        return result;
    }

    /// <summary>
    /// Everything below the given nodes, the nodes themselves excluded,
    /// level by level. Stops with null once more than <paramref name="limit"/>
    /// nodes were found, so a huge tree cannot exhaust memory.
    /// </summary>
    public static async Task<List<Node>?> DescendantsAsync(
        AtriumDbContext db, IReadOnlyCollection<Guid> rootIds, int limit, CancellationToken ct)
    {
        var result = new List<Node>();
        var level = rootIds.ToList();
        var depth = 0;
        while (level.Count > 0)
        {
            if (++depth > MaxDepth)
            {
                throw new InvalidOperationException($"A subtree is nested deeper than {MaxDepth} levels.");
            }

            var next = new List<Guid>();
            foreach (var chunk in level.Chunk(ChunkSize))
            {
                var ids = chunk.ToList();
                var children = await db.Nodes.AsNoTracking()
                    .Where(n => n.ParentId != null && ids.Contains(n.ParentId.Value))
                    .ToListAsync(ct);
                result.AddRange(children);
                if (result.Count > limit)
                {
                    return null;
                }

                next.AddRange(children.Where(c => c.IsFolder).Select(c => c.Id));
            }

            level = next;
        }

        return result;
    }
}
