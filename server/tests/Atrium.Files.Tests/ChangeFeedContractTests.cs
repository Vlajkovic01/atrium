using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Core.Jobs;
using Atrium.Files.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Files.Tests;

/// <summary>
/// The change feed on each real database: every way a file or folder can
/// change shows up, once, for the people who can read it and nobody else.
/// </summary>
public abstract class ChangeFeedContractTests<TDatabase> : IAsyncLifetime
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

    private Task<FileResult<ChangeBatch>> ReadAsync(FileActor actor, string? cursor, int limit = 500) =>
        Host.WithAsync(sp => sp.GetRequiredService<ChangeFeed>().ReadAsync(actor, cursor, limit, None));

    private async Task<string> StartAsync(FileActor actor)
    {
        var start = await ReadAsync(actor, null);
        Assert.True(start.Ok);
        Assert.Empty(start.Value!.Items);
        Assert.False(start.Value.HasMore);
        return start.Value.Cursor;
    }

    /// <summary>Everything since the cursor, following <c>HasMore</c> to the end.</summary>
    private async Task<(List<ChangedNode> Items, string Cursor)> DrainAsync(FileActor actor, string cursor, int limit = 500)
    {
        var items = new List<ChangedNode>();
        for (; ; )
        {
            var batch = await ReadAsync(actor, cursor, limit);
            Assert.True(batch.Ok, batch.Error);
            items.AddRange(batch.Value!.Items);
            cursor = batch.Value.Cursor;
            if (!batch.Value.HasMore)
            {
                return (items, cursor);
            }
        }
    }

    private Task<FileResult<Node>> FolderAsync(FileActor actor, Guid parentId, string name) =>
        Host.NodesAsync(n => n.CreateFolderAsync(actor, parentId, name, None));

    [Fact]
    public async Task Creating_renaming_moving_and_replacing_each_show_the_item_as_it_is_now()
    {
        var (actor, library) = await Host.NewUserAsync();
        var cursor = await StartAsync(actor);

        var docs = (await FolderAsync(actor, library.RootNodeId, "Docs")).Value!;
        var file = (await Host.UploadAsync(actor, library.RootNodeId, "a.txt", "one")).Value!;
        var (created, afterCreate) = await DrainAsync(actor, cursor);

        Assert.Equal([docs.Id, file.Id], created.Select(c => c.NodeId));
        Assert.All(created, c => Assert.True(c.Node is not null && c.CanWrite && !c.InTrash && c.LibraryId == library.Id));

        var renamed = await Host.NodesAsync(n => n.UpdateAsync(actor, file.Id, file.Version, "b.txt", docs.Id, None));
        Assert.True(renamed.Ok, renamed.Error);
        await Host.UploadAsync(actor, docs.Id, "b.txt", "two, longer", ConflictPolicy.Replace);
        var (changed, afterChange) = await DrainAsync(actor, afterCreate);

        // Two changes of one file are one item, with its newest state.
        var item = Assert.Single(changed);
        Assert.Equal(file.Id, item.NodeId);
        Assert.Equal("b.txt", item.Node!.Name);
        Assert.Equal(docs.Id, item.Node.ParentId);
        Assert.Equal("two, longer".Length, item.Node.Size);

        // Nothing new: the same cursor comes back empty, as often as asked.
        var (nothing, _) = await DrainAsync(actor, afterChange);
        Assert.Empty(nothing);
    }

    [Fact]
    public async Task Asking_again_with_an_old_cursor_gives_the_same_changes_again()
    {
        var (actor, library) = await Host.NewUserAsync();
        var cursor = await StartAsync(actor);
        var folder = (await FolderAsync(actor, library.RootNodeId, "Docs")).Value!;

        var first = await DrainAsync(actor, cursor);
        var second = await DrainAsync(actor, cursor);

        Assert.Equal([folder.Id], first.Items.Select(i => i.NodeId));
        Assert.Equal([folder.Id], second.Items.Select(i => i.NodeId));
    }

    [Fact]
    public async Task Trashing_restoring_and_removing_a_folder_is_one_entry_that_speaks_for_what_is_below()
    {
        var (actor, library) = await Host.NewUserAsync();
        var trip = (await FolderAsync(actor, library.RootNodeId, "Trip")).Value!;
        await Host.UploadAsync(actor, trip.Id, "photo.txt", "p");
        var cursor = await StartAsync(actor);

        await Host.NodesAsync(n => n.TrashAsync(actor, trip.Id, null, None));
        var (trashed, afterTrash) = await DrainAsync(actor, cursor);
        var inTrash = Assert.Single(trashed);
        Assert.Equal(trip.Id, inTrash.NodeId);
        Assert.True(inTrash.InTrash && !inTrash.CanWrite);
        Assert.NotNull(inTrash.Node!.TrashedAt);

        Assert.True((await Host.TrashAsync(t => t.RestoreAsync(actor, trip.Id, null, ConflictPolicy.Fail, None))).Ok);
        var (restored, afterRestore) = await DrainAsync(actor, afterTrash);
        var back = Assert.Single(restored);
        Assert.True(back.Node is { TrashedAt: null } && !back.InTrash && back.CanWrite);

        await Host.NodesAsync(n => n.TrashAsync(actor, trip.Id, null, None));
        Assert.True((await Host.TrashAsync(t => t.PurgeAsync(actor, trip.Id, None))).Ok);
        var (removed, _) = await DrainAsync(actor, afterRestore);
        var gone = Assert.Single(removed);
        Assert.Equal(trip.Id, gone.NodeId);
        Assert.Null(gone.Node);
    }

    [Fact]
    public async Task Emptying_the_trash_and_the_end_of_its_retention_report_what_they_removed()
    {
        var (actor, library) = await Host.NewUserAsync();
        var first = (await Host.UploadAsync(actor, library.RootNodeId, "first.txt", "1")).Value!;
        var second = (await Host.UploadAsync(actor, library.RootNodeId, "second.txt", "2")).Value!;
        var third = (await Host.UploadAsync(actor, library.RootNodeId, "third.txt", "3")).Value!;
        foreach (var node in new[] { first, second })
        {
            await Host.NodesAsync(n => n.TrashAsync(actor, node.Id, null, None));
        }

        var cursor = await StartAsync(actor);
        Assert.Equal(2, (await Host.TrashAsync(t => t.EmptyAsync(actor, None))).Value);
        var (emptied, afterEmpty) = await DrainAsync(actor, cursor);

        Assert.Equal(new[] { first.Id, second.Id }.Order(), emptied.Select(e => e.NodeId).Order());
        Assert.All(emptied, e => Assert.Null(e.Node));

        await Host.NodesAsync(n => n.TrashAsync(actor, third.Id, null, None));
        var (_, afterTrash) = await DrainAsync(actor, afterEmpty);
        Host.Time.Advance(TimeSpan.FromDays(31));
        Assert.Equal(JobState.Succeeded, (await Host.RunJobAsync(FileRetentionHandler.Type)).State);

        // The same job ended the feed's own retention. A device that had
        // seen everything a month ago still learns of the removal; one that
        // was behind even then reads everything again.
        Assert.Null(await Host.NodeAsync(third.Id));
        var (late, _) = await DrainAsync(actor, afterTrash);
        Assert.Equal(third.Id, Assert.Single(late).NodeId);
        Assert.Null(late[0].Node);
        Assert.Equal(FileErrors.CursorExpired, (await ReadAsync(actor, afterEmpty)).Error);
    }

    [Fact]
    public async Task Pages_follow_each_other_without_losing_or_repeating_an_item()
    {
        var (actor, library) = await Host.NewUserAsync();
        var cursor = await StartAsync(actor);
        var ids = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            ids.Add((await FolderAsync(actor, library.RootNodeId, $"Folder {i}")).Value!.Id);
        }

        var first = await ReadAsync(actor, cursor, limit: 3);
        Assert.True(first.Value!.HasMore);
        Assert.Equal(3, first.Value.Items.Count);

        var (all, _) = await DrainAsync(actor, cursor, limit: 3);
        Assert.Equal(ids, all.Select(a => a.NodeId));
    }

    [Fact]
    public async Task Someone_elses_changes_are_never_shown_and_do_not_hold_the_cursor_back()
    {
        var (ana, anaLibrary) = await Host.NewUserAsync();
        var (marko, markoLibrary) = await Host.NewUserAsync();
        var cursor = await StartAsync(ana);

        await FolderAsync(marko, markoLibrary.RootNodeId, "Private");
        var (nothing, afterMarko) = await DrainAsync(ana, cursor);
        var mine = (await FolderAsync(ana, anaLibrary.RootNodeId, "Mine")).Value!;
        var (changes, _) = await DrainAsync(ana, afterMarko);

        Assert.Empty(nothing);
        Assert.Equal([mine.Id], changes.Select(c => c.NodeId));

        // A device approved without the personal library sees none of it.
        var hidden = ana with { HidePersonal = true };
        Assert.Empty((await DrainAsync(hidden, cursor)).Items);

        // A read-only device sees the change but may not write.
        var readOnly = ana with { ReadOnlySession = true };
        Assert.False(Assert.Single((await DrainAsync(readOnly, cursor)).Items).CanWrite);
    }

    [Fact]
    public async Task A_cursor_that_is_damaged_or_from_another_life_of_the_database_is_expired()
    {
        var (actor, library) = await Host.NewUserAsync();
        var cursor = await StartAsync(actor);

        Assert.Equal(FileErrors.CursorExpired, (await ReadAsync(actor, "not-a-cursor")).Error);
        var altered = cursor[..20] + (cursor[20] == 'A' ? 'B' : 'A') + cursor[21..];
        Assert.Equal(FileErrors.CursorExpired, (await ReadAsync(actor, altered)).Error);
        Assert.Equal(FileErrors.CursorExpired, (await ReadAsync(actor, string.Empty)).Error);

        // What a restored backup leaves behind: an empty feed whose numbers
        // start over. The old cursor's number comes round again, and must
        // still not be accepted.
        await Host.WithDbAsync(db => db.Changes.ExecuteDeleteAsync());
        Host.Time.Advance(TimeSpan.FromMinutes(1));
        await Host.WithDbAsync(async db =>
        {
            if (db.Database.ProviderName!.Contains("Sqlite", StringComparison.Ordinal))
            {
                await db.Database.ExecuteSqlRawAsync("DELETE FROM sqlite_sequence WHERE name = 'Changes'");
            }
            else
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Changes\" ALTER COLUMN \"Sequence\" RESTART WITH 1");
            }
        });
        for (var i = 0; i < 5; i++)
        {
            await FolderAsync(actor, library.RootNodeId, $"After {i}");
        }

        Assert.Equal(FileErrors.CursorExpired, (await ReadAsync(actor, cursor)).Error);
        Assert.True((await ReadAsync(actor, await StartAsync(actor))).Ok);
    }

    [Fact]
    public async Task Old_entries_are_removed_but_a_client_that_is_up_to_date_keeps_its_place()
    {
        var (actor, library) = await Host.NewUserAsync();
        var stale = await StartAsync(actor);
        await FolderAsync(actor, library.RootNodeId, "Old");
        var (_, current) = await DrainAsync(actor, stale);

        Host.Time.Advance(TimeSpan.FromDays(40));
        Assert.Equal(JobState.Succeeded, (await Host.RunJobAsync(FileRetentionHandler.Type)).State);

        // Nothing happened for forty days: the newest entry is old, and stays.
        Assert.Equal(FileErrors.CursorExpired, (await ReadAsync(actor, stale)).Error);
        var fresh = (await FolderAsync(actor, library.RootNodeId, "New")).Value!;
        var (changes, _) = await DrainAsync(actor, current);
        Assert.Equal([fresh.Id], changes.Select(c => c.NodeId));
    }

    [Fact]
    public async Task A_scan_reports_new_changed_and_vanished_files_but_not_files_it_merely_saw_again()
    {
        var folder = Path.Combine(Host.ExternalRoot, "Shared");
        Directory.CreateDirectory(Path.Combine(folder, "Sub"));
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(folder, "Sub", "gone.txt"), "gone");
        var admin = await _database.AddUserAsync("admin", UserRole.Admin);
        var viewer = await _database.AddUserAsync("ana");
        var outsider = await _database.AddUserAsync("marko");
        var actor = new FileActor(viewer.Id, false, false, false);
        var cursor = await StartAsync(actor);

        var library = (await Host.WithAsync(sp => sp.GetRequiredService<ExternalLibraryService>()
            .CreateAsync(admin.Id, new NewExternalLibrary("Shared", folder, [viewer.Id]), None))).Value!;
        await Host.RunJobsAsync();
        var (found, afterFirst) = await DrainAsync(actor, cursor);

        Assert.Equal(
            new[] { "Shared", "Sub", "gone.txt", "keep.txt" },
            found.Select(f => f.Node!.Name).Order(StringComparer.Ordinal));
        Assert.All(found, f => Assert.False(f.CanWrite));
        Assert.Empty((await DrainAsync(new FileActor(outsider.Id, false, false, false), cursor)).Items);

        var versions = await VersionsAsync(library.Id);
        File.Delete(Path.Combine(folder, "Sub", "gone.txt"));
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "kept and longer");
        Host.Time.Advance(TimeSpan.FromMinutes(5));
        await ScanAsync(library.Id);
        var (second, afterSecond) = await DrainAsync(actor, afterFirst);

        var byId = second.ToDictionary(s => s.NodeId);
        Assert.Equal(2, byId.Count);
        Assert.Null(byId[found.Single(f => f.Node!.Name == "gone.txt").NodeId].Node);
        Assert.Equal("kept and longer".Length, byId[found.Single(f => f.Node!.Name == "keep.txt").NodeId].Node!.Size);

        // A scan that finds everything as it was changes nothing a client can see.
        Host.Time.Advance(TimeSpan.FromMinutes(5));
        await ScanAsync(library.Id);
        Assert.Equal([], (await DrainAsync(actor, afterSecond)).Items.Select(i => i.Node?.Name ?? "removed")); var after = await VersionsAsync(library.Id);
        Assert.Equal(versions["Sub"], after["Sub"]);
        Assert.Equal(versions["Shared"], after["Shared"]);
    }

    private Task ScanAsync(Guid libraryId) =>
        Host.WithAsync(async sp =>
        {
            await sp.GetRequiredService<ExternalLibraryService>().EnqueueScanAsync(libraryId, JobPriority.User, None);
            return true;
        }).ContinueWith(_ => Host.RunJobsAsync()).Unwrap();

    private async Task<Dictionary<string, int>> VersionsAsync(Guid libraryId)
    {
        var result = new Dictionary<string, int>();
        await Host.WithDbAsync(async db =>
        {
            foreach (var node in await db.Nodes.AsNoTracking().Where(n => n.LibraryId == libraryId).ToListAsync())
            {
                result[node.Name] = node.Version;
            }
        });
        return result;
    }
}

public sealed class SqliteChangeFeedTests : ChangeFeedContractTests<SqliteFileDatabase>;

[Trait("Category", "Containers")]
public sealed class PostgresChangeFeedTests : ChangeFeedContractTests<PostgresFileDatabase>;
