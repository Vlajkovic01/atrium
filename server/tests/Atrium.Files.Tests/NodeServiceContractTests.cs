using Atrium.Core.Files;
using Atrium.Core.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Files.Tests;

/// <summary>Files, folders, trash, versions and quota through the services, on each real database.</summary>
public abstract class NodeServiceContractTests<TDatabase> : IAsyncLifetime
    where TDatabase : FileTestDatabase, new()
{
    private static readonly CancellationToken None = CancellationToken.None;
    private readonly TDatabase _database = new();
    private FileServicesHost? _host;

    private FileServicesHost Host => _host ?? throw new InvalidOperationException("Not initialized.");

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _host = await FileServicesHost.StartAsync(_database);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    private Task<FileResult<Node>> FolderAsync(FileActor actor, Guid parentId, string name) =>
        Host.NodesAsync(n => n.CreateFolderAsync(actor, parentId, name, None));

    private async Task<List<string>> NamesAsync(FileActor actor, Guid folderId)
    {
        var page = await Host.NodesAsync(n => n.ChildrenAsync(actor, folderId, ChildSort.Name, false, null, 0, 1000, None));
        return page.Value.Items.Select(i => i.Name).ToList();
    }

    [Fact]
    public async Task A_folder_name_is_unique_among_siblings_ignoring_case()
    {
        var (actor, library) = await Host.NewUserAsync();
        var first = await FolderAsync(actor, library.RootNodeId, "Photos");

        var second = await FolderAsync(actor, library.RootNodeId, "PHOTOS");

        Assert.True(first.Ok);
        Assert.Equal(FileErrors.NameConflict, second.Error);
        Assert.Equal(first.Value!.Id, second.Params!["existingId"]);
    }

    [Fact]
    public async Task An_invalid_name_is_refused()
    {
        var (actor, library) = await Host.NewUserAsync();

        Assert.Equal(FileErrors.InvalidName, (await FolderAsync(actor, library.RootNodeId, "a/b")).Error);
        Assert.Equal(FileErrors.InvalidName, (await FolderAsync(actor, library.RootNodeId, "CON")).Error);
    }

    [Fact]
    public async Task Children_list_folders_first_then_by_name()
    {
        var (actor, library) = await Host.NewUserAsync();
        await Host.UploadAsync(actor, library.RootNodeId, "b.txt", "b");
        await FolderAsync(actor, library.RootNodeId, "Zeta");
        await Host.UploadAsync(actor, library.RootNodeId, "A.txt", "a");
        await FolderAsync(actor, library.RootNodeId, "alpha");

        Assert.Equal(["alpha", "Zeta", "A.txt", "b.txt"], await NamesAsync(actor, library.RootNodeId));

        var page = await Host.NodesAsync(n => n.ChildrenAsync(actor, library.RootNodeId, ChildSort.Name, true, null, 1, 2, None));
        Assert.Equal(4, page.Value.Total);
        Assert.Equal(["alpha", "b.txt"], page.Value.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Someone_elses_files_do_not_exist_for_you()
    {
        var (ana, library) = await Host.NewUserAsync("ana");
        var (bob, _) = await Host.NewUserAsync("bob");
        var folder = (await FolderAsync(ana, library.RootNodeId, "Private")).Value!;

        Assert.Null(await Host.NodesAsync(n => n.FindAsync(bob, folder.Id, None)));
        Assert.Equal(FileErrors.NotFound, (await FolderAsync(bob, folder.Id, "sneaky")).Error);
        Assert.Equal(FileErrors.NotFound, (await Host.NodesAsync(n => n.TrashAsync(bob, folder.Id, null, None))).Error);
        Assert.Equal(FileErrors.NotFound, (await Host.NodesAsync(n => n.UpdateAsync(bob, folder.Id, folder.Version, "x", null, None))).Error);
    }

    [Fact]
    public async Task A_restricted_device_cannot_change_or_cannot_see_personal_files()
    {
        var (ana, library) = await Host.NewUserAsync("ana");
        var readOnly = ana with { ReadOnlySession = true };
        var hidden = ana with { HidePersonal = true };

        var found = await Host.NodesAsync(n => n.FindAsync(readOnly, library.RootNodeId, None));

        Assert.False(found!.CanWrite);
        Assert.Equal(FileErrors.ReadOnly, (await FolderAsync(readOnly, library.RootNodeId, "x")).Error);
        Assert.Null(await Host.NodesAsync(n => n.FindAsync(hidden, library.RootNodeId, None)));
    }

    [Fact]
    public async Task Renaming_needs_the_current_version()
    {
        var (actor, library) = await Host.NewUserAsync();
        var folder = (await FolderAsync(actor, library.RootNodeId, "Old")).Value!;

        var renamed = await Host.NodesAsync(n => n.UpdateAsync(actor, folder.Id, folder.Version, "New", null, None));
        var stale = await Host.NodesAsync(n => n.UpdateAsync(actor, folder.Id, folder.Version, "Newer", null, None));

        Assert.True(renamed.Ok);
        Assert.Equal("New", renamed.Value!.Name);
        Assert.Equal(FileErrors.VersionConflict, stale.Error);
        Assert.Equal("New", (await Host.NodeAsync(folder.Id))!.Name);
    }

    [Fact]
    public async Task Renaming_onto_a_sibling_is_a_conflict_but_changing_case_is_fine()
    {
        var (actor, library) = await Host.NewUserAsync();
        var a = (await FolderAsync(actor, library.RootNodeId, "a")).Value!;
        await FolderAsync(actor, library.RootNodeId, "b");

        Assert.Equal(FileErrors.NameConflict, (await Host.NodesAsync(n => n.UpdateAsync(actor, a.Id, a.Version, "B", null, None))).Error);
        Assert.Equal("A", (await Host.NodesAsync(n => n.UpdateAsync(actor, a.Id, a.Version, "A", null, None))).Value!.Name);
    }

    [Fact]
    public async Task A_folder_cannot_be_moved_into_itself_or_below_itself()
    {
        var (actor, library) = await Host.NewUserAsync();
        var outer = (await FolderAsync(actor, library.RootNodeId, "Outer")).Value!;
        var inner = (await FolderAsync(actor, outer.Id, "Inner")).Value!;
        var deepest = (await FolderAsync(actor, inner.Id, "Deepest")).Value!;

        Assert.Equal(FileErrors.MoveIntoItself, (await Host.NodesAsync(n => n.UpdateAsync(actor, outer.Id, outer.Version, null, outer.Id, None))).Error);
        Assert.Equal(FileErrors.MoveIntoItself, (await Host.NodesAsync(n => n.UpdateAsync(actor, outer.Id, outer.Version, null, deepest.Id, None))).Error);

        var moved = await Host.NodesAsync(n => n.UpdateAsync(actor, deepest.Id, deepest.Version, null, library.RootNodeId, None));
        Assert.True(moved.Ok);
        Assert.Equal(library.RootNodeId, moved.Value!.ParentId);
    }

    [Fact]
    public async Task The_root_folder_cannot_be_renamed_moved_or_trashed()
    {
        var (actor, library) = await Host.NewUserAsync();
        var root = (await Host.NodeAsync(library.RootNodeId))!;

        Assert.Equal(FileErrors.IsRoot, (await Host.NodesAsync(n => n.UpdateAsync(actor, root.Id, root.Version, "x", null, None))).Error);
        Assert.Equal(FileErrors.IsRoot, (await Host.NodesAsync(n => n.TrashAsync(actor, root.Id, null, None))).Error);
    }

    [Fact]
    public async Task Copying_a_folder_copies_its_live_contents_without_copying_bytes()
    {
        var (actor, library) = await Host.NewUserAsync();
        var trip = (await FolderAsync(actor, library.RootNodeId, "Trip")).Value!;
        var day = (await FolderAsync(actor, trip.Id, "Day 1")).Value!;
        var photo = (await Host.UploadAsync(actor, day.Id, "photo.jpg", "pixels")).Value!;
        var deleted = (await Host.UploadAsync(actor, trip.Id, "deleted.txt", "gone")).Value!;
        await Host.NodesAsync(n => n.TrashAsync(actor, deleted.Id, null, None));

        var copy = await Host.NodesAsync(n => n.CopyAsync(actor, trip.Id, library.RootNodeId, null, None));

        Assert.True(copy.Ok);
        Assert.Equal("Trip (2)", copy.Value!.Name);
        Assert.Equal(["Day 1"], await NamesAsync(actor, copy.Value.Id));
        var copiedDay = (await Host.NodesAsync(n => n.ChildrenAsync(actor, copy.Value.Id, ChildSort.Name, false, null, 0, 10, None))).Value.Items.Single();
        var copiedPhoto = (await Host.NodesAsync(n => n.ChildrenAsync(actor, copiedDay.Id, ChildSort.Name, false, null, 0, 10, None))).Value.Items.Single();
        Assert.Equal(photo.BlobId, copiedPhoto.BlobId);
        Assert.NotEqual(photo.Id, copiedPhoto.Id);
        Assert.Equal(2, (await Host.BlobsAsync()).Count);
    }

    [Fact]
    public async Task A_folder_cannot_be_copied_into_itself()
    {
        var (actor, library) = await Host.NewUserAsync();
        var folder = (await FolderAsync(actor, library.RootNodeId, "Loop")).Value!;

        Assert.Equal(FileErrors.MoveIntoItself, (await Host.NodesAsync(n => n.CopyAsync(actor, folder.Id, folder.Id, null, None))).Error);
    }

    [Fact]
    public async Task Trashing_a_folder_hides_it_frees_its_name_and_closes_it_for_changes()
    {
        var (actor, library) = await Host.NewUserAsync();
        var folder = (await FolderAsync(actor, library.RootNodeId, "Old stuff")).Value!;
        var inside = (await Host.UploadAsync(actor, folder.Id, "notes.txt", "notes")).Value!;

        Assert.True((await Host.NodesAsync(n => n.TrashAsync(actor, folder.Id, folder.Version, None))).Ok);

        Assert.Empty(await NamesAsync(actor, library.RootNodeId));
        Assert.True((await FolderAsync(actor, library.RootNodeId, "Old stuff")).Ok);
        Assert.True((await Host.NodesAsync(n => n.FindAsync(actor, inside.Id, None)))!.InTrash);
        Assert.Equal(FileErrors.InTrash, (await FolderAsync(actor, folder.Id, "new")).Error);
        Assert.Equal(FileErrors.InTrash, (await Host.NodesAsync(n => n.TrashAsync(actor, inside.Id, null, None))).Error);

        // Nothing was deleted: rows and content are all still there.
        Assert.NotNull(await Host.NodeAsync(inside.Id));
        Assert.Single(await Host.BlobsAsync());
    }

    [Fact]
    public async Task The_trash_lists_what_was_deleted_with_its_size_and_deadline()
    {
        var (actor, library) = await Host.NewUserAsync();
        var folder = (await FolderAsync(actor, library.RootNodeId, "Folder")).Value!;
        await Host.UploadAsync(actor, folder.Id, "a.txt", "12345");
        await Host.UploadAsync(actor, folder.Id, "b.txt", "123");
        await Host.NodesAsync(n => n.TrashAsync(actor, folder.Id, null, None));

        var item = Assert.Single(await Host.TrashAsync(t => t.ListAsync(actor, None)));

        Assert.Equal(folder.Id, item.Node.Id);
        Assert.Equal(8, item.Bytes);
        Assert.Equal(3, item.ItemCount);
        Assert.Equal(Host.Time.GetUtcNow() + TimeSpan.FromDays(30), item.DeleteAt);
    }

    [Fact]
    public async Task The_trash_says_which_folder_an_item_came_from()
    {
        var (actor, library) = await Host.NewUserAsync();
        var trip = (await FolderAsync(actor, library.RootNodeId, "Trip")).Value!;
        var day = (await FolderAsync(actor, trip.Id, "Day 1")).Value!;
        var photo = (await Host.UploadAsync(actor, day.Id, "photo.jpg", "pixels")).Value!;
        await Host.NodesAsync(n => n.TrashAsync(actor, photo.Id, null, None));

        var item = Assert.Single(await Host.TrashAsync(t => t.ListAsync(actor, None)));

        Assert.Equal([library.RootNodeId, trip.Id, day.Id], item.Location.Select(n => n.Id));
        Assert.Equal(["Trip", "Day 1"], item.Location.Skip(1).Select(n => n.Name));
    }

    [Fact]
    public async Task Restoring_puts_an_item_back_or_asks_when_its_place_is_taken()
    {
        var (actor, library) = await Host.NewUserAsync();
        var file = (await Host.UploadAsync(actor, library.RootNodeId, "report.pdf", "v1")).Value!;
        await Host.NodesAsync(n => n.TrashAsync(actor, file.Id, null, None));
        await Host.UploadAsync(actor, library.RootNodeId, "Report.pdf", "other");

        var refused = await Host.TrashAsync(t => t.RestoreAsync(actor, file.Id, null, ConflictPolicy.Fail, None));
        var renamed = await Host.TrashAsync(t => t.RestoreAsync(actor, file.Id, null, ConflictPolicy.Rename, None));

        Assert.Equal(FileErrors.NameConflict, refused.Error);
        Assert.Equal("report (2).pdf", renamed.Value!.Name);
        Assert.Null(renamed.Value.TrashedAt);
        Assert.Empty(await Host.TrashAsync(t => t.ListAsync(actor, None)));
    }

    [Fact]
    public async Task An_item_whose_folder_is_in_the_trash_needs_a_new_place()
    {
        var (actor, library) = await Host.NewUserAsync();
        var folder = (await FolderAsync(actor, library.RootNodeId, "Folder")).Value!;
        var file = (await Host.UploadAsync(actor, folder.Id, "a.txt", "a")).Value!;
        await Host.NodesAsync(n => n.TrashAsync(actor, file.Id, null, None));
        await Host.NodesAsync(n => n.TrashAsync(actor, folder.Id, null, None));

        Assert.Equal(FileErrors.InTrash, (await Host.TrashAsync(t => t.RestoreAsync(actor, file.Id, null, ConflictPolicy.Fail, None))).Error);

        var restored = await Host.TrashAsync(t => t.RestoreAsync(actor, file.Id, library.RootNodeId, ConflictPolicy.Fail, None));
        Assert.Equal(library.RootNodeId, restored.Value!.ParentId);
    }

    [Fact]
    public async Task Deleting_from_the_trash_removes_rows_and_leaves_content_to_the_collector()
    {
        var (actor, library) = await Host.NewUserAsync();
        var folder = (await FolderAsync(actor, library.RootNodeId, "Folder")).Value!;
        var file = (await Host.UploadAsync(actor, folder.Id, "a.txt", "precious")).Value!;
        var live = (await Host.UploadAsync(actor, library.RootNodeId, "live.txt", "precious")).Value!;
        await Host.NodesAsync(n => n.TrashAsync(actor, folder.Id, null, None));

        Assert.Equal(FileErrors.NotInTrash, (await Host.TrashAsync(t => t.PurgeAsync(actor, live.Id, None))).Error);
        Assert.True((await Host.TrashAsync(t => t.PurgeAsync(actor, folder.Id, None))).Ok);

        Assert.Null(await Host.NodeAsync(folder.Id));
        Assert.Null(await Host.NodeAsync(file.Id));

        // The same content is still used by another file, so it survives collection.
        await Host.CollectAsync();
        Host.Time.Advance(TimeSpan.FromDays(8));
        await Host.CollectAsync();
        Assert.Equal(BlobState.Stored, Assert.Single(await Host.BlobsAsync()).State);
        Assert.Equal(live.BlobId, file.BlobId);
    }

    [Fact]
    public async Task Emptying_the_trash_removes_only_trashed_items()
    {
        var (actor, library) = await Host.NewUserAsync();
        var keep = (await FolderAsync(actor, library.RootNodeId, "Keep")).Value!;
        var drop = (await FolderAsync(actor, library.RootNodeId, "Drop")).Value!;
        await Host.NodesAsync(n => n.TrashAsync(actor, drop.Id, null, None));

        var emptied = await Host.TrashAsync(t => t.EmptyAsync(actor, None));

        Assert.Equal(1, emptied.Value);
        Assert.NotNull(await Host.NodeAsync(keep.Id));
        Assert.Null(await Host.NodeAsync(drop.Id));
    }

    [Fact]
    public async Task Retention_removes_old_trash_and_old_versions_only()
    {
        var (actor, library) = await Host.NewUserAsync();
        var old = (await FolderAsync(actor, library.RootNodeId, "Old")).Value!;
        var file = (await Host.UploadAsync(actor, library.RootNodeId, "doc.txt", "v1")).Value!;
        await Host.UploadAsync(actor, library.RootNodeId, "doc.txt", "v2", ConflictPolicy.Replace);
        await Host.NodesAsync(n => n.TrashAsync(actor, old.Id, null, None));
        Host.Time.Advance(TimeSpan.FromDays(20));
        var recent = (await FolderAsync(actor, library.RootNodeId, "Recent")).Value!;
        await Host.NodesAsync(n => n.TrashAsync(actor, recent.Id, null, None));
        Host.Time.Advance(TimeSpan.FromDays(11));

        Assert.Equal(JobState.Succeeded, (await Host.RunJobAsync(FileRetentionHandler.Type)).State);

        Assert.Null(await Host.NodeAsync(old.Id));
        Assert.NotNull(await Host.NodeAsync(recent.Id));
        Assert.Empty((await Host.VersionsAsync(v => v.ListAsync(actor, file.Id, None))).Value!);
        Assert.NotNull(await Host.NodeAsync(file.Id));
    }

    [Fact]
    public async Task Replacing_a_file_keeps_its_old_content_as_a_version()
    {
        var (actor, library) = await Host.NewUserAsync();
        var original = (await Host.UploadAsync(actor, library.RootNodeId, "doc.txt", "first draft")).Value!;

        var replaced = await Host.UploadAsync(actor, library.RootNodeId, "DOC.txt", "second draft", ConflictPolicy.Replace);

        Assert.Equal(original.Id, replaced.Value!.Id);
        Assert.NotEqual(original.BlobId, replaced.Value.BlobId);
        var version = Assert.Single((await Host.VersionsAsync(v => v.ListAsync(actor, original.Id, None))).Value!);
        Assert.Equal(original.BlobId, (await DbVersionAsync(version.Id)).BlobId);
        Assert.Equal("first draft".Length, version.Size);
    }

    [Fact]
    public async Task Uploading_identical_content_creates_no_version()
    {
        var (actor, library) = await Host.NewUserAsync();
        var original = (await Host.UploadAsync(actor, library.RootNodeId, "doc.txt", "same")).Value!;

        await Host.UploadAsync(actor, library.RootNodeId, "doc.txt", "same", ConflictPolicy.Replace);

        Assert.Empty((await Host.VersionsAsync(v => v.ListAsync(actor, original.Id, None))).Value!);
    }

    [Fact]
    public async Task At_most_ten_versions_are_kept_the_newest_ones()
    {
        var (actor, library) = await Host.NewUserAsync();
        await Host.UploadAsync(actor, library.RootNodeId, "doc.txt", "v0");
        for (var i = 1; i <= 12; i++)
        {
            Host.Time.Advance(TimeSpan.FromMinutes(1));
            await Host.UploadAsync(actor, library.RootNodeId, "doc.txt", $"v{i}", ConflictPolicy.Replace);
        }

        var file = (await Host.NodesAsync(n => n.ChildrenAsync(actor, library.RootNodeId, ChildSort.Name, false, null, 0, 10, None))).Value.Items.Single();
        var versions = (await Host.VersionsAsync(v => v.ListAsync(actor, file.Id, None))).Value!;

        Assert.Equal(10, versions.Count);
        Assert.Equal(Host.Time.GetUtcNow(), versions[0].CreatedAt);
    }

    [Fact]
    public async Task Restoring_a_version_keeps_the_current_content_as_a_version()
    {
        var (actor, library) = await Host.NewUserAsync();
        var v1 = (await Host.UploadAsync(actor, library.RootNodeId, "doc.txt", "one")).Value!;
        var v2 = (await Host.UploadAsync(actor, library.RootNodeId, "doc.txt", "two!", ConflictPolicy.Replace)).Value!;
        var old = Assert.Single((await Host.VersionsAsync(v => v.ListAsync(actor, v1.Id, None))).Value!);

        var restored = await Host.VersionsAsync(v => v.RestoreAsync(actor, v1.Id, old.Id, None));

        Assert.Equal(v1.BlobId, restored.Value!.BlobId);
        var now = Assert.Single((await Host.VersionsAsync(v => v.ListAsync(actor, v1.Id, None))).Value!);
        Assert.Equal(v2.BlobId, (await DbVersionAsync(now.Id)).BlobId);
        Assert.True((await Host.VersionsAsync(v => v.DeleteAsync(actor, v1.Id, now.Id, None))).Ok);
        Assert.Empty((await Host.VersionsAsync(v => v.ListAsync(actor, v1.Id, None))).Value!);
    }

    [Fact]
    public async Task Usage_counts_files_trash_and_versions_whatever_is_deduplicated()
    {
        var (actor, library) = await Host.NewUserAsync();
        var folder = (await FolderAsync(actor, library.RootNodeId, "F")).Value!;
        await Host.UploadAsync(actor, library.RootNodeId, "a.txt", "1234567890");
        await Host.UploadAsync(actor, folder.Id, "copy-of-a.txt", "1234567890");
        await Host.UploadAsync(actor, library.RootNodeId, "doc.txt", "12345");
        await Host.UploadAsync(actor, library.RootNodeId, "doc.txt", "123", ConflictPolicy.Replace);
        await Host.NodesAsync(n => n.TrashAsync(actor, folder.Id, null, None));

        var usage = await Host.WithAsync(sp => sp.GetRequiredService<UsageService>().ForUserAsync(actor.UserId, None));

        Assert.Equal(10 + 10 + 3 + 5, usage.UsedBytes);
        Assert.Equal(13, usage.FilesBytes);
        Assert.Equal(10, usage.TrashBytes);
        Assert.Equal(5, usage.VersionsBytes);
    }

    [Fact]
    public async Task A_full_quota_refuses_new_files_and_copies()
    {
        var (actor, library) = await Host.NewUserAsync();
        await Host.WithDbAsync(db => db.Users.Where(u => u.Id == actor.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.QuotaBytes, 12L)));
        var file = (await Host.UploadAsync(actor, library.RootNodeId, "a.txt", "1234567890")).Value!;

        var tooBig = await Host.UploadAsync(actor, library.RootNodeId, "b.txt", "123");
        var copy = await Host.NodesAsync(n => n.CopyAsync(actor, file.Id, library.RootNodeId, null, None));

        Assert.Equal(FileErrors.QuotaExceeded, tooBig.Error);
        Assert.Equal(12L, tooBig.Params!["quotaBytes"]);
        Assert.Equal(FileErrors.QuotaExceeded, copy.Error);
        Assert.Equal(["a.txt"], await NamesAsync(actor, library.RootNodeId));
    }

    private async Task<NodeVersion> DbVersionAsync(Guid id)
    {
        NodeVersion version = null!;
        await Host.WithDbAsync(async db => version = await db.NodeVersions.AsNoTracking().SingleAsync(v => v.Id == id));
        return version;
    }
}

public sealed class SqliteNodeServiceTests : NodeServiceContractTests<SqliteFileDatabase>;

[Trait("Category", "Containers")]
public sealed class PostgresNodeServiceTests : NodeServiceContractTests<PostgresFileDatabase>;
