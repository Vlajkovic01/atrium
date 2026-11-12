using System.Collections.Concurrent;
using System.Text.Json;
using Atrium.Core.Storage;
using Atrium.Data;
using Atrium.Identity.Crypto;
using Atrium.Storage;
using Atrium.Storage.Local;
using Atrium.Storage.S3;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Files.Storage;

public sealed record LocalStorageSettings(string Path);

/// <summary>What is stored, encrypted, for a location. Exactly one of the two is set, matching the kind.</summary>
public sealed record StorageLocationConfig(LocalStorageSettings? Local, S3StorageOptions? S3);

/// <summary>
/// The location cannot be used now: it is not reachable, or it does not
/// carry the identity written when it was registered (for example an
/// unmounted share). Nothing was written to it.
/// </summary>
public sealed class LocationUnavailableException(Guid locationId, string code, string? detail = null)
    : Exception($"Storage location {locationId} is unavailable: {code}.")
{
    public Guid LocationId { get; } = locationId;

    public string Code { get; } = code;

    public string? Detail { get; } = detail;
}

/// <summary>
/// Opens storage providers for registered locations. A provider is handed
/// out only after the location proved its identity, so nothing is ever
/// written to an empty mount point or a different bucket (DESIGN.md 6.8).
/// Providers are kept for the life of the process: an S3 client is costly
/// to create.
/// </summary>
public sealed class StorageProviders(SecretProtector protector, TimeProvider time) : IDisposable
{
    public const string ConfigProtectionPurpose = "storage-location-config";

    public const string IdentityMissing = "storage.identity_missing";
    public const string IdentityMismatch = "storage.identity_mismatch";
    public const string Unavailable = StorageHealthCodes.Unavailable;

    /// <summary>How long a successful identity check is trusted before it is repeated.</summary>
    public static readonly TimeSpan VerificationLifetime = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public StorageLocationConfig ReadConfig(StorageLocation location) =>
        JsonSerializer.Deserialize<StorageLocationConfig>(
            protector.UnprotectString(location.EncryptedConfig, ConfigProtectionPurpose), Json)
        ?? throw new InvalidOperationException($"Storage location {location.Id} has no settings.");

    public byte[] ProtectConfig(StorageLocationConfig config) =>
        protector.ProtectString(JsonSerializer.Serialize(config, Json), ConfigProtectionPurpose);

    public static IStorageProvider Create(StorageProviderKind kind, StorageLocationConfig config) =>
        kind == StorageProviderKind.S3
            ? new S3StorageProvider(config.S3 ?? throw new InvalidOperationException("S3 settings are missing."))
            : new LocalStorageProvider(config.Local?.Path ?? throw new InvalidOperationException("The path is missing."));

    /// <summary>
    /// A provider for the location after its identity was checked within
    /// the last minute.
    /// </summary>
    /// <exception cref="LocationUnavailableException">The location is unreachable or not the registered one.</exception>
    public async Task<IStorageProvider> OpenAsync(AtriumDbContext db, Guid locationId, CancellationToken ct)
    {
        var location = await db.StorageLocations.AsNoTracking().SingleOrDefaultAsync(l => l.Id == locationId, ct)
            ?? throw new LocationUnavailableException(locationId, Unavailable, "The location is not registered.");

        var entry = _entries.AddOrUpdate(
            locationId,
            _ => new Entry(location.Version, Create(location.Kind, ReadConfig(location))),
            (_, existing) =>
            {
                if (existing.ConfigVersion == location.Version)
                {
                    return existing;
                }

                existing.Dispose();
                return new Entry(location.Version, Create(location.Kind, ReadConfig(location)));
            });

        var now = time.GetUtcNow();
        if (entry.VerifiedAt is { } verified && now - verified < VerificationLifetime)
        {
            return entry.Provider;
        }

        LocationIdentity? identity;
        try
        {
            identity = await LocationIdentityFile.ReadAsync(entry.Provider, ct);
        }
        catch (StorageException e)
        {
            throw new LocationUnavailableException(locationId, Unavailable, e.Message);
        }

        if (identity is null)
        {
            throw new LocationUnavailableException(locationId, IdentityMissing);
        }

        if (identity.Id != locationId)
        {
            throw new LocationUnavailableException(locationId, IdentityMismatch);
        }

        entry.VerifiedAt = now;
        return entry.Provider;
    }

    /// <summary>Forces the next <see cref="OpenAsync"/> to check the identity again, for example after a failure.</summary>
    public void Invalidate(Guid locationId)
    {
        if (_entries.TryGetValue(locationId, out var entry))
        {
            entry.VerifiedAt = null;
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Dispose();
        }

        _entries.Clear();
    }

    private sealed class Entry(int configVersion, IStorageProvider provider) : IDisposable
    {
        public int ConfigVersion { get; } = configVersion;

        public IStorageProvider Provider { get; } = provider;

        public DateTimeOffset? VerifiedAt { get; set; }

        public void Dispose() => (Provider as IDisposable)?.Dispose();
    }
}
