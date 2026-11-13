using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Files;
using Atrium.Files.External;
using Atrium.Files.Tests;
using Atrium.Media.Photos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Media.Tests;

/// <summary>Timeline, search and albums on each real database.</summary>
public abstract class PhotoServiceContractTests<TDatabase> : IAsyncLifetime
    where TDatabase : FileTestDatabase, new()
{
    private static readonly CancellationToken None = CancellationToken.None;
    private readonly TDatabase _database = new();
    private FileServicesHost? _host;

    private FileServicesHost Host => _host ?? throw new InvalidOperationException("Not initialized.");

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _host = await FileServicesHost.StartAsync(
            _database,
            configure: s => s.AddAtriumMedia(new MediaOptions { CacheDir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N")) }));
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    private Task<T> PhotosAsync<T>(Func<PhotoService, Task<T>> action) =>
        Host.WithAsync(sp => action(sp.GetRequiredService<PhotoService>()));

    /// <summary>A file taken at the given local time; photos and videos by media type.</summary>
    private async Task<Node> AddAsync(FileActor actor, Guid parentId, string name, DateTimeOffset? capturedAt, string mediaType = "image/jpeg")
    {
        var blob = await Host.StoreAsync(System.Text.Encoding.UTF8.GetBytes(name + Guid.NewGuid()));
        var added = await Host.NodesAsync(n => n.AddFileAsync(actor, parentId, name, blob, mediaType, capturedAt, ConflictPolicy.Fail, None));
        Assert.True(added.Ok, added.Error);
        return added.Value!;
    }

    private static DateTimeOffset At(int year, int month, int day, int hour = 12) => new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_timeline_shows_months_newest_first_and_pages_without_gaps()
    {
        var (actor, library) = await Host.NewUserAsync();
        for (var day = 1; day <= 5; day++)
        {
            await AddAsync(actor, library.RootNodeId, $"may{day}.jpg", At(2024, 5, day));
        }

        await AddAsync(actor, library.RootNodeId, "june.mp4", At(2024, 6, 1), "video/mp4");
        await AddAsync(actor, library.RootNodeId, "notes.txt", At(2024, 6, 2), "text/plain");

        var buckets = await PhotosAsync(p => p.BucketsAsync(actor, null, None));
        Assert.Equal([new TimelineBucket(2024, 6, 1), new TimelineBucket(2024, 5, 5)], buckets);

        var names = new List<string>();
        string? cursor = null;
        do
        {
            var page = await PhotosAsync(p => p.TimelineAsync(actor, null, null, null, cursor, 2, None));
            names.AddRange(page.Items.Select(i => i.Node.Name));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(["june.mp4", "may5.jpg", "may4.jpg", "may3.jpg", "may2.jpg", "may1.jpg"], names);

        var may = await PhotosAsync(p => p.TimelineAsync(actor, null, At(2024, 5, 1, 0), At(2024, 6, 1, 0), null, 100, None));
        Assert.Equal(5, may.Items.Count);
    }

    [Fact]
    public async Task The_timeline_leaves_out_the_trash_and_other_peoples_photos()
    {
        var (ana, anaLibrary) = await Host.NewUserAsync("ana");
        var (bob, bobLibrary) = await Host.NewUserAsync("bob");
        var folder = (await Host.NodesAsync(n => n.CreateFolderAsync(ana, anaLibrary.RootNodeId, "Trip", None))).Value!;
        await AddAsync(ana, folder.Id, "inside.jpg", At(2023, 1, 1));
        await AddAsync(ana, anaLibrary.RootNodeId, "kept.jpg", At(2023, 1, 2));
        await AddAsync(bob, bobLibrary.RootNodeId, "bobs.jpg", At(2023, 1, 3));

        await Host.NodesAsync(n => n.TrashAsync(ana, folder.Id, null, None));

        var page = await PhotosAsync(p => p.TimelineAsync(ana, null, null, null, null, 100, None));
        Assert.Equal(["kept.jpg"], page.Items.Select(i => i.Node.Name));
    }

    [Fact]
    public async Task A_shared_folder_appears_in_the_timeline_of_those_it_is_shared_with()
    {
        var folder = Path.Combine(Host.ExternalRoot, "Shared");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "shot.png"), Samples.Png(8, 8));
        var admin = await _database.AddUserAsync("admin", UserRole.Admin);
        var (ana, _) = await Host.NewUserAsync("ana");
        var (bob, _) = await Host.NewUserAsync("bob");
        await Host.WithAsync(sp => sp.GetRequiredService<ExternalLibraryService>()
            .CreateAsync(admin.Id, new NewExternalLibrary("Shared", folder, [ana.UserId]), None));
        await Host.RunJobsAsync();

        Assert.Single((await PhotosAsync(p => p.TimelineAsync(ana, null, null, null, null, 100, None))).Items);
        Assert.Empty((await PhotosAsync(p => p.TimelineAsync(bob, null, null, null, null, 100, None))).Items);
    }

    [Fact]
    public async Task A_folder_of_any_files_shows_everything_and_joins_the_timeline_only_when_chosen()
    {
        var folder = Path.Combine(Host.ExternalRoot, "Archive");
        Directory.CreateDirectory(Path.Combine(folder, "Contracts", "2024"));
        await File.WriteAllTextAsync(Path.Combine(folder, "Contracts", "2024", "lease.pdf"), "pdf");
        await File.WriteAllTextAsync(Path.Combine(folder, "budget.xlsx"), "xlsx");
        await File.WriteAllBytesAsync(Path.Combine(folder, "clip.mp4"), [0, 0, 0, 24]);
        await File.WriteAllBytesAsync(Path.Combine(folder, "scan.jpg"), Samples.JpegWithExif(16, 16));
        var admin = await _database.AddUserAsync("admin", UserRole.Admin);
        var (ana, _) = await Host.NewUserAsync("ana");
        var created = await Host.WithAsync(sp => sp.GetRequiredService<ExternalLibraryService>()
            .CreateAsync(admin.Id, new NewExternalLibrary("Archive", folder, [ana.UserId], ShowInTimeline: false), None));
        var library = created.Value!;
        await Host.RunJobsAsync();

        // Every file and folder is there to browse.
        var top = await Host.NodesAsync(n => n.ChildrenAsync(ana, library.RootNodeId, ChildSort.Name, false, null, 0, 100, None));
        Assert.Equal(["Contracts", "budget.xlsx", "clip.mp4", "scan.jpg"], top.Value.Items.Select(i => i.Name));
        var found = await PhotosAsync(p => p.SearchAsync(ana, new SearchRequest("lease", null, null, null, null, null, 10), None));
        Assert.Equal(["lease.pdf"], found.Items.Select(i => i.Node.Name));

        // Kept out of the timeline, as chosen; the photo and the video can still be found and opened.
        Assert.Empty((await PhotosAsync(p => p.TimelineAsync(ana, null, null, null, null, 100, None))).Items);
        Assert.Equal(2, (await PhotosAsync(p => p.SearchAsync(ana, new SearchRequest(null, null, null, null, library.Id, null, 10), None)))
            .Items.Count(i => i.Node.MediaKind is not null));

        // Joining the timeline brings the photo and the video, not the documents.
        await Host.WithAsync(sp => sp.GetRequiredService<ExternalLibraryService>()
            .UpdateAsync(library.Id, library.Version, null, null, null, showInTimeline: true, None));
        var timeline = await PhotosAsync(p => p.TimelineAsync(ana, null, null, null, null, 100, None));
        Assert.Equal(["clip.mp4", "scan.jpg"], timeline.Items.Select(i => i.Node.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Search_finds_names_without_diacritics_by_type_and_by_date()
    {
        var (actor, library) = await Host.NewUserAsync();
        await AddAsync(actor, library.RootNodeId, "Šuma na Tari.jpg", At(2022, 7, 10));
        await AddAsync(actor, library.RootNodeId, "suma video.mp4", At(2023, 7, 10), "video/mp4");
        await AddAsync(actor, library.RootNodeId, "Šuma plan.pdf", At(2022, 7, 11), "application/pdf");
        await AddAsync(actor, library.RootNodeId, "more.jpg", At(2022, 7, 12));

        async Task<List<string>> Find(string? q, SearchType? type = null, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
            (await PhotosAsync(p => p.SearchAsync(actor, new SearchRequest(q, type, from, to, null, null, 100), None)))
                .Items.Select(i => i.Node.Name).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["suma video.mp4", "Šuma na Tari.jpg", "Šuma plan.pdf"], await Find("suma"));
        Assert.Equal(["Šuma na Tari.jpg"], await Find("ŠUMA tari"));
        Assert.Equal(["suma video.mp4"], await Find("suma", SearchType.Video));
        Assert.Equal(["Šuma plan.pdf"], await Find(null, SearchType.Document));
        Assert.Equal(["more.jpg", "Šuma na Tari.jpg", "Šuma plan.pdf"], await Find(null, null, At(2022, 1, 1, 0), At(2023, 1, 1, 0)));

        // Wildcards in a query are plain characters.
        Assert.Empty(await Find("%"));
    }

    [Fact]
    public async Task An_album_holds_references_and_shows_only_what_is_still_there()
    {
        var (actor, library) = await Host.NewUserAsync();
        var a = await AddAsync(actor, library.RootNodeId, "a.jpg", At(2021, 3, 1));
        var b = await AddAsync(actor, library.RootNodeId, "b.jpg", At(2021, 3, 2));
        var text = await AddAsync(actor, library.RootNodeId, "c.txt", At(2021, 3, 3), "text/plain");
        var (stranger, strangerLibrary) = await Host.NewUserAsync("bob");
        var theirs = await AddAsync(stranger, strangerLibrary.RootNodeId, "theirs.jpg", At(2021, 3, 4));

        var album = (await PhotosAsync(p => p.CreateAlbumAsync(actor, "Spring", [a.Id, b.Id], None))).Value!;
        Assert.Equal(FileErrors.NotFound, (await PhotosAsync(p => p.AddItemsAsync(actor, album.Id, [theirs.Id], None))).Error);
        Assert.Equal(FileErrors.NotFound, (await PhotosAsync(p => p.AddItemsAsync(actor, album.Id, [text.Id], None))).Error);
        Assert.Equal(0, (await PhotosAsync(p => p.AddItemsAsync(actor, album.Id, [a.Id], None))).Value);

        await Host.NodesAsync(n => n.TrashAsync(actor, b.Id, null, None));
        var items = await PhotosAsync(p => p.AlbumItemsAsync(actor, album.Id, null, 100, None));
        Assert.Equal(["a.jpg"], items.Items.Select(i => i.Node.Name));

        // Restored from the trash, it is in the album again.
        await Host.TrashAsync(t => t.RestoreAsync(actor, b.Id, null, ConflictPolicy.Fail, None));
        Assert.Equal(2, (await PhotosAsync(p => p.AlbumSummaryAsync(actor, album.Id, None))).Count);

        var cover = await PhotosAsync(p => p.UpdateAlbumAsync(actor, album.Id, album.Version, null, a.Id, None));
        Assert.True(cover.Ok, cover.Error);
        await PhotosAsync(p => p.RemoveItemsAsync(actor, album.Id, [a.Id], None));
        Assert.Null((await PhotosAsync(p => p.AlbumAsync(actor, album.Id, None)))!.CoverNodeId);

        // Deleting the album leaves the photos alone.
        Assert.True(await PhotosAsync(p => p.DeleteAlbumAsync(actor, album.Id, None)));
        Assert.NotNull(await Host.NodeAsync(a.Id));
        Assert.Null(await PhotosAsync(p => p.AlbumAsync(stranger, album.Id, None)));
    }
}

public sealed class SqlitePhotoServiceTests : PhotoServiceContractTests<SqliteFileDatabase>;

[Trait("Category", "Containers")]
public sealed class PostgresPhotoServiceTests : PhotoServiceContractTests<PostgresFileDatabase>;
