using System.Text.Json;
using Atrium.Api.Hosting;
using Atrium.Core.Storage;
using Atrium.Data;
using Atrium.Identity.Crypto;
using Atrium.Storage;
using Atrium.Storage.Local;
using Atrium.Storage.S3;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Storage;

public sealed record LocalStorageSettings(string Path);

/// <summary>What is stored, encrypted, for a location. Exactly one of the two is set, matching the kind.</summary>
public sealed record StorageLocationConfig(LocalStorageSettings? Local, S3StorageOptions? S3);

/// <param name="HoldsAtriumData">The location already has an identity file: another Atrium server, or an earlier install, used it.</param>
public sealed record StorageTestOutcome(bool Ok, string? ErrorCode, string? Detail, long? FreeBytes, bool HoldsAtriumData)
{
    public static StorageTestOutcome Fail(string code, string? detail = null) => new(false, code, detail, null, false);
}

public sealed record CreateLocationOutcome(StorageLocation? Location, StorageTestOutcome Test);

public static class StorageErrors
{
    public const string InvalidConfig = "storage.invalid_config";
    public const string RootMissing = "storage.root_missing";
    public const string PathNotAllowed = "storage.path_not_allowed";
    public const string LocationInUse = "storage.location_in_use";
    public const string NameTaken = "storage.name_taken";
    public const string IdentityMissing = "storage.identity_missing";
    public const string IdentityMismatch = "storage.identity_mismatch";
    public const string Unavailable = "storage.unavailable";
}

/// <summary>
/// Registers and checks the places where user files are stored. The rules
/// here exist to protect files: a location is tested before it is saved,
/// an already used location is never taken over, and a registered location
/// must prove its identity before it is trusted again.
/// </summary>
public sealed class StorageLocationService(
    AtriumDbContext db,
    SecretProtector protector,
    BootstrapOptions bootstrap,
    TimeProvider time)
{
    public const string ConfigProtectionPurpose = "storage-location-config";
    public const int MaxNameLength = 128;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Tries a location without saving anything. A local directory that does
    /// not exist is reported, never created: creating is a separate, explicit
    /// choice made when the location is saved.
    /// </summary>
    public async Task<StorageTestOutcome> TestAsync(StorageProviderKind kind, StorageLocationConfig config, CancellationToken ct)
    {
        if (Validate(kind, config) is { } invalid)
        {
            return invalid;
        }

        var provider = CreateProvider(kind, config);
        try
        {
            var health = await provider.CheckAsync(ct);
            if (!health.Ok)
            {
                return StorageTestOutcome.Fail(health.ErrorCode ?? StorageErrors.Unavailable, health.Detail);
            }

            var identity = await LocationIdentityFile.ReadAsync(provider, ct);
            return new StorageTestOutcome(true, null, null, health.FreeBytes, identity is not null);
        }
        catch (StorageException e)
        {
            return StorageTestOutcome.Fail(StorageErrors.Unavailable, e.Message);
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    public async Task<CreateLocationOutcome> CreateAsync(
        string name, StorageProviderKind kind, StorageLocationConfig config, bool createDirectory, CancellationToken ct)
    {
        name = name.Trim();
        if (name.Length is 0 or > MaxNameLength)
        {
            return new CreateLocationOutcome(null, StorageTestOutcome.Fail(StorageErrors.InvalidConfig, "name"));
        }

        if (await db.StorageLocations.AnyAsync(l => l.Name == name, ct))
        {
            return new CreateLocationOutcome(null, StorageTestOutcome.Fail(StorageErrors.NameTaken));
        }

        // A missing directory is acceptable here only if the caller asked for it to be created.
        if (Validate(kind, config) is { } invalid
            && !(createDirectory && invalid.ErrorCode == StorageErrors.RootMissing))
        {
            return new CreateLocationOutcome(null, invalid);
        }

        // The one place a storage root is created, and only when asked.
        if (createDirectory && config.Local is { } local && !Directory.Exists(local.Path))
        {
            try
            {
                Directory.CreateDirectory(local.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new CreateLocationOutcome(null, StorageTestOutcome.Fail("storage.access_denied", e.Message));
            }
        }

        var test = await TestAsync(kind, config, ct);
        if (!test.Ok)
        {
            return new CreateLocationOutcome(null, test);
        }

        if (test.HoldsAtriumData)
        {
            // Taking over a location that another server may still be using
            // could make two servers delete each other's files. Adopting an
            // existing location is a deliberate recovery action (Phase 11).
            return new CreateLocationOutcome(null, test with { Ok = false, ErrorCode = StorageErrors.LocationInUse });
        }

        var now = time.GetUtcNow();
        var location = new StorageLocation
        {
            Name = name,
            Kind = kind,
            EncryptedConfig = protector.ProtectString(JsonSerializer.Serialize(config, Json), ConfigProtectionPurpose),
            State = StorageLocationState.Online,
            LastCheckedAt = now,
            CreatedAt = now,
        };

        var provider = CreateProvider(kind, config);
        try
        {
            await LocationIdentityFile.WriteAsync(
                provider, new LocationIdentity(location.Id, now, LocationIdentity.CurrentFormatVersion), ct);

            db.StorageLocations.Add(location);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another request took the name a moment ago. Remove the
                // identity just written, or the location would look used
                // by a server that has no record of it.
                db.ChangeTracker.Clear();
                await provider.DeleteAsync(LocationIdentityFile.Key, CancellationToken.None);
                return new CreateLocationOutcome(null, StorageTestOutcome.Fail(StorageErrors.NameTaken));
            }
        }
        catch (StorageException e)
        {
            return new CreateLocationOutcome(null, StorageTestOutcome.Fail(StorageErrors.Unavailable, e.Message));
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }

        return new CreateLocationOutcome(location, test);
    }

    /// <summary>
    /// Checks a registered location and records the result. Healthy means
    /// more than "writable": the location must also carry the identity that
    /// was written when it was registered. An empty directory where a network
    /// share should be mounted is writable, and is not the location.
    /// </summary>
    public async Task<StorageTestOutcome?> CheckAsync(Guid id, CancellationToken ct)
    {
        var location = await db.StorageLocations.SingleOrDefaultAsync(l => l.Id == id, ct);
        if (location is null)
        {
            return null;
        }

        var outcome = await VerifyAsync(location, ct);
        location.State = outcome.Ok ? StorageLocationState.Online : StorageLocationState.Offline;
        location.LastCheckedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return outcome;
    }

    public StorageLocationConfig ReadConfig(StorageLocation location) =>
        JsonSerializer.Deserialize<StorageLocationConfig>(
            protector.UnprotectString(location.EncryptedConfig, ConfigProtectionPurpose), Json)!;

    /// <summary>Where the location points, for display. Never includes credentials.</summary>
    public string DescribeTarget(StorageLocation location)
    {
        var config = ReadConfig(location);
        if (config.Local is { } local)
        {
            return local.Path;
        }

        var s3 = config.S3!;
        var prefix = string.IsNullOrWhiteSpace(s3.KeyPrefix) ? string.Empty : "/" + s3.KeyPrefix.Trim('/');
        return $"{s3.ServiceUrl ?? "AWS S3"} · {s3.Bucket}{prefix}";
    }

    private async Task<StorageTestOutcome> VerifyAsync(StorageLocation location, CancellationToken ct)
    {
        var provider = CreateProvider(location.Kind, ReadConfig(location));
        try
        {
            // Identity first, and read-only: nothing is written to a place
            // that has not proven it is the right one.
            var identity = await LocationIdentityFile.ReadAsync(provider, ct);
            if (identity is null)
            {
                return StorageTestOutcome.Fail(StorageErrors.IdentityMissing);
            }

            if (identity.Id != location.Id)
            {
                return StorageTestOutcome.Fail(StorageErrors.IdentityMismatch);
            }

            var health = await provider.CheckAsync(ct);
            return health.Ok
                ? new StorageTestOutcome(true, null, null, health.FreeBytes, true)
                : StorageTestOutcome.Fail(health.ErrorCode ?? StorageErrors.Unavailable, health.Detail);
        }
        catch (StorageUnavailableException e) when (location.Kind != StorageProviderKind.S3)
        {
            return StorageTestOutcome.Fail(StorageErrors.RootMissing, e.Message);
        }
        catch (StorageException e)
        {
            return StorageTestOutcome.Fail(StorageErrors.Unavailable, e.Message);
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    private StorageTestOutcome? Validate(StorageProviderKind kind, StorageLocationConfig config)
    {
        if (!Enum.IsDefined(kind))
        {
            return StorageTestOutcome.Fail(StorageErrors.InvalidConfig, "kind");
        }

        if (kind == StorageProviderKind.S3)
        {
            var s3 = config.S3;
            if (config.Local is not null
                || s3 is null
                || string.IsNullOrWhiteSpace(s3.Bucket)
                || string.IsNullOrWhiteSpace(s3.AccessKey)
                || string.IsNullOrWhiteSpace(s3.SecretKey))
            {
                return StorageTestOutcome.Fail(StorageErrors.InvalidConfig, "s3");
            }

            if (s3.ServiceUrl is { Length: > 0 } url
                && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            {
                return StorageTestOutcome.Fail(StorageErrors.InvalidConfig, "serviceUrl");
            }

            if (s3.KeyPrefix is { Length: > 0 } prefix && !StorageKey.IsValid(prefix.Trim('/')))
            {
                return StorageTestOutcome.Fail(StorageErrors.InvalidConfig, "keyPrefix");
            }

            return null;
        }

        var path = config.Local?.Path;
        if (config.S3 is not null || string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return StorageTestOutcome.Fail(StorageErrors.InvalidConfig, "path");
        }

        // User files must not share a directory with the database and the
        // server keys: a location is something users may sync, share or wipe.
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var configDir = Path.TrimEndingDirectorySeparator(bootstrap.ConfigDir);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (full.Equals(configDir, comparison)
            || full.StartsWith(configDir + Path.DirectorySeparatorChar, comparison)
            || configDir.StartsWith(full + Path.DirectorySeparatorChar, comparison))
        {
            return StorageTestOutcome.Fail(StorageErrors.PathNotAllowed);
        }

        return Directory.Exists(full) ? null : StorageTestOutcome.Fail(StorageErrors.RootMissing);
    }

    private static IStorageProvider CreateProvider(StorageProviderKind kind, StorageLocationConfig config) =>
        kind == StorageProviderKind.S3
            ? new S3StorageProvider(config.S3!)
            : new LocalStorageProvider(config.Local!.Path);
}
