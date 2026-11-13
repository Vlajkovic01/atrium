using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Files.Tests;

/// <summary>
/// The rules the database itself enforces for files, on each supported
/// database. They hold even if service code has a bug, which is why they
/// are tested against the schema and not only through the services.
/// </summary>
public abstract class FileModelContractTests<TDatabase> : IAsyncLifetime
    where TDatabase : FileTestDatabase, new()
{
    protected TDatabase Db { get; } = new();

    public Task InitializeAsync() => Db.InitializeAsync();

    public Task DisposeAsync() => Db.DisposeAsync();

    private async Task<(User User, Library Library)> PersonalLibraryAsync(string name = "ana")
    {
        var user = await Db.AddUserAsync(name);
        if (!await HasLocationAsync())
        {
            await Db.AddLocationAsync("main");
        }

        await using var db = Db.CreateContext();
        var library = await new LibraryService(db, Db.Time).EnsurePersonalAsync(user, CancellationToken.None);
        return (user, library!);
    }

    private async Task<bool> HasLocationAsync()
    {
        await using var db = Db.CreateContext();
        return await db.StorageLocations.AnyAsync();
    }

    private async Task AddAsync(params object[] entities)
    {
        await using var db = Db.CreateContext();
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    private Blob NewBlob(Guid locationId, byte seed) => new()
    {
        StorageLocationId = locationId,
        Sha256 = Enumerable.Repeat(seed, 32).ToArray(),
        Size = 10,
        CreatedAt = Db.Time.GetUtcNow(),
    };

    [Fact]
    public async Task A_personal_library_is_created_once_with_a_root_folder()
    {
        var (user, library) = await PersonalLibraryAsync();

        await using var db = Db.CreateContext();
        var again = await new LibraryService(db, Db.Time).EnsurePersonalAsync(user, CancellationToken.None);
        var root = await db.Nodes.SingleAsync(n => n.Id == library.RootNodeId);

        Assert.Equal(library.Id, again!.Id);
        Assert.Equal(LibraryKind.Personal, library.Kind);
        Assert.Equal(LibraryMode.Managed, library.Mode);
        Assert.Equal(NodeKind.Folder, root.Kind);
        Assert.Null(root.ParentId);
        Assert.Equal(library.Id, root.LibraryId);
        Assert.Single(await db.Libraries.ToListAsync());
    }

    [Fact]
    public async Task Parallel_first_requests_create_one_personal_library()
    {
        var user = await Db.AddUserAsync("ana");
        await Db.AddLocationAsync("main");

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var db = Db.CreateContext();
            return await new LibraryService(db, Db.Time).EnsurePersonalAsync(user, CancellationToken.None);
        }));

        Assert.Single(results.Select(l => l!.Id).Distinct());
        await using var check = Db.CreateContext();
        Assert.Equal(1, await check.Libraries.CountAsync());
        Assert.Equal(1, await check.Nodes.CountAsync());
    }

    [Fact]
    public async Task Guests_get_no_personal_library_and_nothing_is_created_without_storage()
    {
        var guest = await Db.AddUserAsync("guest", UserRole.Guest);
        var ana = await Db.AddUserAsync("ana");

        await using var db = Db.CreateContext();
        var service = new LibraryService(db, Db.Time);

        Assert.Null(await service.EnsurePersonalAsync(ana, CancellationToken.None));
        await Db.AddLocationAsync("main");
        Assert.Null(await service.EnsurePersonalAsync(guest, CancellationToken.None));
        Assert.NotNull(await service.EnsurePersonalAsync(ana, CancellationToken.None));
    }

    [Fact]
    public async Task New_libraries_stay_on_the_first_location_after_another_is_added()
    {
        var first = await Db.AddLocationAsync("first");
        var (_, ana) = await PersonalLibraryAsync("ana");
        await Db.AddLocationAsync("second");
        var (_, bob) = await PersonalLibraryAsync("bob");

        Assert.Equal(first.Id, ana.StorageLocationId);
        Assert.Equal(first.Id, bob.StorageLocationId);
    }

    [Fact]
    public async Task Siblings_cannot_share_a_name_that_differs_only_by_case()
    {
        var (_, library) = await PersonalLibraryAsync();
        await AddAsync(Db.NewNode(library, library.RootNodeId, "Report.pdf", NodeKind.File));

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            AddAsync(Db.NewNode(library, library.RootNodeId, "REPORT.pdf", NodeKind.File)));
    }

    [Fact]
    public async Task The_same_name_is_fine_in_different_folders()
    {
        var (_, library) = await PersonalLibraryAsync();
        var a = Db.NewNode(library, library.RootNodeId, "A");
        var b = Db.NewNode(library, library.RootNodeId, "B");
        await AddAsync(a, b);

        await AddAsync(Db.NewNode(library, a.Id, "notes.txt", NodeKind.File), Db.NewNode(library, b.Id, "notes.txt", NodeKind.File));
    }

    [Fact]
    public async Task A_trashed_node_does_not_block_its_name()
    {
        var (_, library) = await PersonalLibraryAsync();
        var old = Db.NewNode(library, library.RootNodeId, "notes.txt", NodeKind.File);
        old.TrashedAt = Db.Time.GetUtcNow();
        old.ActiveNameKey = null;
        var trashedTwice = Db.NewNode(library, library.RootNodeId, "notes.txt", NodeKind.File);
        trashedTwice.TrashedAt = Db.Time.GetUtcNow();
        trashedTwice.ActiveNameKey = null;
        await AddAsync(old, trashedTwice);

        await AddAsync(Db.NewNode(library, library.RootNodeId, "notes.txt", NodeKind.File));
    }

    [Fact]
    public async Task Removing_a_folder_row_removes_everything_below_it_and_its_versions()
    {
        var (_, library) = await PersonalLibraryAsync();
        await using (var db = Db.CreateContext())
        {
            var locationId = library.StorageLocationId!.Value;
            var blob = NewBlob(locationId, 1);
            var older = NewBlob(locationId, 2);
            var folder = Db.NewNode(library, library.RootNodeId, "Trip");
            var inner = Db.NewNode(library, folder.Id, "Day 1");
            var file = Db.NewNode(library, inner.Id, "photo.jpg", NodeKind.File, blob.Id);
            db.AddRange(blob, older, folder, inner, file);
            db.NodeVersions.Add(new NodeVersion { NodeId = file.Id, BlobId = older.Id, CreatedAt = Db.Time.GetUtcNow() });
            await db.SaveChangesAsync();

            await db.Nodes.Where(n => n.Id == folder.Id).ExecuteDeleteAsync();
        }

        await using var check = Db.CreateContext();
        Assert.Equal([library.RootNodeId], await check.Nodes.Select(n => n.Id).ToListAsync());
        Assert.False(await check.NodeVersions.AnyAsync());

        // Content is never deleted with the rows: the blob collector decides.
        Assert.Equal(2, await check.Blobs.CountAsync());
    }

    [Fact]
    public async Task A_blob_that_a_node_or_a_version_points_at_cannot_be_deleted()
    {
        var (_, library) = await PersonalLibraryAsync();
        var current = NewBlob(library.StorageLocationId!.Value, 1);
        var older = NewBlob(library.StorageLocationId!.Value, 2);
        var file = Db.NewNode(library, library.RootNodeId, "a.txt", NodeKind.File, current.Id);
        await AddAsync(current, older, file);
        await AddAsync(new NodeVersion { NodeId = file.Id, BlobId = older.Id, CreatedAt = Db.Time.GetUtcNow() });

        await using var db = Db.CreateContext();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Blobs.Where(b => b.Id == current.Id).ExecuteDeleteAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => db.Blobs.Where(b => b.Id == older.Id).ExecuteDeleteAsync());
        Assert.Equal(2, await db.Blobs.CountAsync());
    }

    [Fact]
    public async Task One_location_holds_each_content_hash_once()
    {
        var (_, library) = await PersonalLibraryAsync();
        await AddAsync(NewBlob(library.StorageLocationId!.Value, 7));

        await Assert.ThrowsAsync<DbUpdateException>(() => AddAsync(NewBlob(library.StorageLocationId!.Value, 7)));
    }

    [Fact]
    public async Task A_user_with_a_library_cannot_be_removed_by_accident()
    {
        var (user, _) = await PersonalLibraryAsync();

        await using var db = Db.CreateContext();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync());
        Assert.True(await db.Users.AnyAsync(u => u.Id == user.Id));
    }

    [Fact]
    public async Task Media_info_belongs_to_exactly_one_blob_or_one_node()
    {
        var (_, library) = await PersonalLibraryAsync();
        var blob = NewBlob(library.StorageLocationId!.Value, 1);
        var file = Db.NewNode(library, library.RootNodeId, "a.jpg", NodeKind.File, blob.Id);
        await AddAsync(blob, file);

        await Assert.ThrowsAsync<DbUpdateException>(() => AddAsync(new MediaInfo { Kind = MediaKind.Photo }));
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            AddAsync(new MediaInfo { Kind = MediaKind.Photo, BlobId = blob.Id, NodeId = file.Id }));
        await AddAsync(new MediaInfo { Kind = MediaKind.Photo, BlobId = blob.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => AddAsync(new MediaInfo { Kind = MediaKind.Photo, BlobId = blob.Id }));
    }

    [Fact]
    public async Task Media_info_and_album_items_go_with_their_content_and_the_album_survives()
    {
        var (user, library) = await PersonalLibraryAsync();
        var blob = NewBlob(library.StorageLocationId!.Value, 1);
        var file = Db.NewNode(library, library.RootNodeId, "a.jpg", NodeKind.File, blob.Id);
        var album = new Album { OwnerUserId = user.Id, Name = "Summer", CoverNodeId = file.Id, CreatedAt = Db.Time.GetUtcNow() };
        await AddAsync(blob, file, album);
        await AddAsync(
            new MediaInfo { Kind = MediaKind.Photo, BlobId = blob.Id },
            new AlbumItem { AlbumId = album.Id, NodeId = file.Id, AddedAt = Db.Time.GetUtcNow() });

        await using var db = Db.CreateContext();
        await db.Nodes.Where(n => n.Id == file.Id).ExecuteDeleteAsync();
        Assert.False(await db.AlbumItems.AnyAsync());
        Assert.Null((await db.Albums.SingleAsync()).CoverNodeId);

        // The collector removes the blob row once nothing uses it; its facts go with it.
        await db.Blobs.Where(b => b.Id == blob.Id).ExecuteDeleteAsync();
        Assert.False(await db.MediaInfos.AnyAsync());
    }

    [Fact]
    public void The_blob_key_spreads_objects_over_two_directory_levels()
    {
        var hash = Convert.FromHexString("ABCDEF" + new string('0', 58));

        Assert.Equal("atrium/blobs/ab/cd/abcdef" + new string('0', 58), Blob.KeyFor(hash));
    }
}

public sealed class SqliteFileModelTests : FileModelContractTests<SqliteFileDatabase>;

[Trait("Category", "Containers")]
public sealed class PostgresFileModelTests : FileModelContractTests<PostgresFileDatabase>;
