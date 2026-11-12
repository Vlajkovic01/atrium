namespace Atrium.Core.Files;

public enum UploadState
{
    Active,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// A resumable upload in progress (tus, DESIGN.md 4.1). Each accepted chunk
/// is one part of a multipart upload on the storage location, so the
/// offset recorded here is always backed by durable parts.
/// </summary>
public sealed class Upload : IVersioned
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid UserId { get; init; }

    public Guid LibraryId { get; init; }

    /// <summary>The folder the file goes into.</summary>
    public Guid ParentId { get; init; }

    public Guid StorageLocationId { get; init; }

    /// <summary>The file name, already validated.</summary>
    public required string Name { get; init; }

    public string? MediaType { get; init; }

    /// <summary>Total size in bytes, declared at creation.</summary>
    public long Length { get; init; }

    /// <summary>Bytes received and stored durably.</summary>
    public long Offset { get; set; }

    /// <summary>Largest chunk accepted; every chunk but the last is at least the provider's minimum part size.</summary>
    public long ChunkSize { get; init; }

    /// <summary>What happens if the name is taken when the upload finishes. Stored as the enum member name.</summary>
    public required string ConflictPolicy { get; init; }

    public DateTimeOffset? ModifiedAt { get; init; }

    /// <summary>SHA-256 the client declared; the content is refused if it differs.</summary>
    public byte[]? ExpectedSha256 { get; init; }

    public UploadState State { get; set; }

    public required string StagingKey { get; init; }

    public required string MultipartUploadId { get; init; }

    /// <summary>The stored parts, as JSON: number, ETag and size of each.</summary>
    public string Parts { get; set; } = "[]";

    /// <summary>The file the upload became.</summary>
    public Guid? NodeId { get; set; }

    public string? ErrorCode { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>An upload that sees no activity until this time is discarded.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public int Version { get; set; }
}
