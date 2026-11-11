namespace Atrium.Core.Storage;

public enum StorageProviderKind
{
    LocalFileSystem,
    NetworkShare,
    S3,
}

public enum StorageLocationState
{
    /// <summary>Not checked since the server started.</summary>
    Unknown,
    Online,

    /// <summary>Last check failed. Libraries on it are unavailable, and nothing is removed from the index.</summary>
    Offline,
}

/// <summary>A place where user files are stored (DESIGN.md 3).</summary>
public sealed class StorageLocation : IVersioned
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public required StorageProviderKind Kind { get; init; }

    /// <summary>
    /// Provider settings as JSON, encrypted with the server key because they
    /// can contain credentials. Never returned by the API.
    /// </summary>
    public required byte[] EncryptedConfig { get; set; }

    public StorageLocationState State { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public int Version { get; set; }
}
