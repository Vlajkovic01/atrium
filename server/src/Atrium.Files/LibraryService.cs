using System.Text.Json;
using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Core.Settings;
using Atrium.Data;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Files;

/// <summary>Finds and creates libraries.</summary>
public sealed class LibraryService(AtriumDbContext db, TimeProvider time)
{
    /// <summary>Which storage location new personal libraries are placed on.</summary>
    public const string DefaultLocationSettingKey = "files.default_storage_location";

    public const string PersonalLibraryName = "Personal";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The user's personal library, created on first use. Guests have none:
    /// they only read what is granted to them. Null also when no storage
    /// location exists yet.
    /// </summary>
    public async Task<Library?> EnsurePersonalAsync(User user, CancellationToken ct)
    {
        if (user.Role == UserRole.Guest)
        {
            return null;
        }

        if (await db.Libraries.AsNoTracking().SingleOrDefaultAsync(l => l.OwnerUserId == user.Id, ct) is { } existing)
        {
            return existing;
        }

        if (await DefaultLocationAsync(ct) is not { } locationId)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var libraryId = Guid.CreateVersion7();
        var root = new Node
        {
            LibraryId = libraryId,
            ParentId = null,
            Name = PersonalLibraryName,
            NameKey = NodeName.KeyOf(PersonalLibraryName),
            ActiveNameKey = NodeName.KeyOf(PersonalLibraryName),
            Kind = NodeKind.Folder,
            ModifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = user.Id,
        };
        var library = new Library
        {
            Id = libraryId,
            Name = PersonalLibraryName,
            Kind = LibraryKind.Personal,
            Mode = LibraryMode.Managed,
            StorageLocationId = locationId,
            OwnerUserId = user.Id,
            RootNodeId = root.Id,
            CreatedAt = now,
        };

        db.Libraries.Add(library);
        db.Nodes.Add(root);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A parallel request created it first; use that one.
            db.ChangeTracker.Clear();
            return await db.Libraries.AsNoTracking().SingleAsync(l => l.OwnerUserId == user.Id, ct);
        }

        db.Entry(library).State = EntityState.Detached;
        db.Entry(root).State = EntityState.Detached;
        return library;
    }

    /// <summary>
    /// The location recorded for new libraries. On first use it is the
    /// oldest registered location, the one the setup wizard created, and it
    /// is recorded so that adding a location later changes nothing.
    /// </summary>
    public async Task<Guid?> DefaultLocationAsync(CancellationToken ct)
    {
        var setting = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == DefaultLocationSettingKey, ct);
        if (setting is not null && JsonSerializer.Deserialize<Guid>(setting.Value, Json) is var stored
            && await db.StorageLocations.AnyAsync(l => l.Id == stored, ct))
        {
            return stored;
        }

        var oldest = await db.StorageLocations.AsNoTracking()
            .OrderBy(l => l.CreatedAt)
            .ThenBy(l => l.Id)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);
        if (oldest is null)
        {
            return null;
        }

        var value = JsonSerializer.Serialize(oldest.Value, Json);
        if (setting is null)
        {
            db.Settings.Add(new Setting { Key = DefaultLocationSettingKey, Value = value, UpdatedAt = time.GetUtcNow() });
        }
        else
        {
            await db.Settings.Where(s => s.Key == DefaultLocationSettingKey)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, value), ct);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Recorded by a parallel request; both chose the same oldest location.
            db.ChangeTracker.Clear();
        }

        return oldest;
    }
}
