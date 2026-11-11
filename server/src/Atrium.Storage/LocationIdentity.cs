using System.Text.Json;

namespace Atrium.Storage;

/// <summary>
/// What a storage location says about itself (DESIGN.md 3.2). Written once
/// when the location is registered, and read back before the location is
/// trusted. It lets the server prove it is looking at the place it
/// registered and not at an empty mount point or another bucket.
/// </summary>
/// <param name="FormatVersion">Leaves room for a different on-storage layout later, such as encryption (D-005).</param>
public sealed record LocationIdentity(Guid Id, DateTimeOffset CreatedAt, int FormatVersion)
{
    public const int CurrentFormatVersion = 1;
}

public static class LocationIdentityFile
{
    public const string Key = "atrium/location.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Null when the location has no identity file. Throws if the file exists but cannot be understood.</summary>
    public static async Task<LocationIdentity?> ReadAsync(IStorageProvider provider, CancellationToken ct)
    {
        try
        {
            await using var stream = await provider.OpenReadAsync(Key, null, ct);
            return await JsonSerializer.DeserializeAsync<LocationIdentity>(stream, Json, ct)
                ?? throw new StorageException("The location's identity file is empty.");
        }
        catch (StorageObjectNotFoundException)
        {
            return null;
        }
        catch (JsonException e)
        {
            // Unreadable is not the same as absent: something is there, and
            // treating the location as new could overwrite another install.
            throw new StorageException("The location's identity file is damaged.", e);
        }
    }

    /// <summary>Writes the identity. Never replaces an existing one.</summary>
    public static async Task WriteAsync(IStorageProvider provider, LocationIdentity identity, CancellationToken ct)
    {
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(identity, Json));
        await provider.WriteAsync(Key, stream, overwrite: false, ct);
    }
}
