using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Core.Jobs;
using Atrium.Files.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Files.Tests;

/// <summary>Counts folders indexed, and can stop the scan after a number of them.</summary>
public sealed class CountingObserver : INodeContentObserver
{
    public int Calls { get; private set; }

    public int StopAfter { get; set; } = int.MaxValue;

    public CancellationTokenSource Stopping { get; } = new();

    public List<Node> Files { get; } = [];

    public Task ContentChangedAsync(Library library, IReadOnlyList<Node> files, CancellationToken ct)
    {
        Files.AddRange(files);
        if (++Calls >= StopAfter)
        {
            Stopping.Cancel();
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// External libraries on each real database: adding one, scanning it,
/// following changes on disk, and never writing to the folder.
/// </summary>
public abstract class ExternalLibraryContractTests<TDatabase> : IAsyncLifetime
    where TDatabase : FileTestDatabase, new()
{
    private static readonly CancellationToken None = CancellationToken.None;
    private readonly TDatabase _database = new();
    private readonly CountingObserver _observer = new();
    private FileServicesHost? _host;

    private FileServicesHost Host => _host ?? throw new InvalidOperationException("Not initialized.");

    private string Folder => Path.Combine(Host.ExternalRoot, "Screenshots");

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _host = await FileServicesHost.StartAsync(_database, configure: s => s.AddSingleton<INodeContentObserver>(_observer));
        Directory.CreateDirectory(Folder);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(Folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private Task<T> WithLibrariesAsync<T>(Func<ExternalLibraryService, Task<T>> action) =>
        Host.WithAsync(sp => action(sp.GetRequiredService<ExternalLibraryService>()));

    private async Task<(Library Library, FileActor Viewer)> AddLibraryAsync(string? path = null)
    {
        var admin = await _database.AddUserAsync("admin" + Guid.NewGuid().ToString("N")[..6], UserRole.Admin);
        var viewer = await _database.AddUserAsync("ana" + Guid.NewGuid().ToString("N")[..6]);
        var created = await WithLibrariesAsync(s => s.CreateAsync(admin.Id, new NewExternalLibrary("Screenshots", path ?? Folder, [viewer.Id]), None));
        Assert.True(created.Ok, created.Error);
        return (created.Value!, new FileActor(viewer.Id, false, false, false));
    }

    private async Task<List<Node>> NodesAsync(Guid libraryId)
    {
        List<Node> nodes = [];
        await Host.WithDbAsync(async db => nodes = await db.Nodes.AsNoTracking().Where(n => n.LibraryId == libraryId).ToListAsync());
        return nodes;
    }

    private async Task<Library> LibraryAsync(Guid id)
    {
        Library library = null!;
        await Host.WithDbAsync(async db => library = await db.Libraries.AsNoTracking().SingleAsync(l => l.Id == id));
        return library;
    }

    private Task ScanAsync(Guid libraryId) =>
        WithLibrariesAsync(async s => await s.EnqueueScanAsync(libraryId, JobPriority.User, None)).ContinueWith(_ => Host.RunJobsAsync()).Unwrap();

    private static Dictionary<string, (long Size, DateTime Written)> Snapshot(string folder) =>
        Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(
                p => Path.GetRelativePath(folder, p),
                p => File.Exists(p) ? (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)) : (-1L, Directory.GetLastWriteTimeUtc(p)));

    [Fact]
    public async Task A_folder_is_checked_before_it_becomes_a_library()
    {
        Assert.Equal(LibraryErrors.PathNotAbsolute, (await WithLibrariesAsync(s => s.CheckPathAsync("relative/path", null, None))).ErrorCode);
        Assert.Equal(LibraryErrors.PathMissing, (await WithLibrariesAsync(s => s.CheckPathAsync(Path.Combine(Folder, "nope"), null, None))).ErrorCode);
        Assert.Equal(LibraryErrors.PathProtected, (await WithLibrariesAsync(s => s.CheckPathAsync(Host.StorageRoot, null, None))).ErrorCode);

        // A folder that contains a storage location would show Atrium's own blobs.
        Assert.Equal(LibraryErrors.PathProtected, (await WithLibrariesAsync(s => s.CheckPathAsync(Path.GetDirectoryName(Host.StorageRoot), null, None))).ErrorCode);

        Write("a.png", "a");
        var ok = await WithLibrariesAsync(s => s.CheckPathAsync(Folder, null, None));
        Assert.True(ok.Ok);
        Assert.Equal(1, ok.EntryCount);
    }

    [Fact]
    public async Task Two_libraries_cannot_cover_the_same_files()
    {
        Directory.CreateDirectory(Path.Combine(Folder, "2024"));
        await AddLibraryAsync();

        Assert.Equal(LibraryErrors.PathOverlaps, (await WithLibrariesAsync(s => s.CheckPathAsync(Folder, null, None))).ErrorCode);
        Assert.Equal(LibraryErrors.PathOverlaps, (await WithLibrariesAsync(s => s.CheckPathAsync(Path.Combine(Folder, "2024"), null, None))).ErrorCode);
        Assert.Equal(LibraryErrors.PathOverlaps, (await WithLibrariesAsync(s => s.CheckPathAsync(Host.ExternalRoot, null, None))).ErrorCode);
    }

    [Fact]
    public async Task A_scan_indexes_the_folder_and_never_changes_it()
    {
        Write("a.png", "aaa");
        Write("2024/May/b.jpg", "bb");
        Write("2024/notes.txt", "n");
        Write(".hidden/c.jpg", "c");
        Write("@eaDir/thumb.jpg", "t");
        Write(".atrium-trash/old.jpg", "o");
        var before = Snapshot(Folder);
        var (library, viewer) = await AddLibraryAsync();

        await Host.RunJobsAsync();

        var nodes = await NodesAsync(library.Id);
        Assert.Equal(
            ["2024", "2024/May", "2024/May/b.jpg", "2024/notes.txt", "a.png"],
            nodes.Where(n => n.ExternalPath is { Length: > 0 }).Select(n => n.ExternalPath!).Order(StringComparer.Ordinal));
        var b = nodes.Single(n => n.Name == "b.jpg");
        Assert.Equal(2, b.Size);
        Assert.Equal(MediaKind.Photo, b.MediaKind);
        Assert.Equal("image/jpeg", b.MediaType);
        Assert.Equal(LibraryState.Online, (await LibraryAsync(library.Id)).State);
        Assert.NotNull((await LibraryAsync(library.Id)).LastScanAt);

        // Nothing was created, changed or removed in the folder.
        Assert.Equal(before, Snapshot(Folder));

        var found = await Host.NodesAsync(n => n.FindAsync(viewer, b.Id, None));
        Assert.NotNull(found);
        Assert.False(found.CanWrite);
    }

    [Fact]
    public async Task Only_people_with_access_see_the_library_and_nobody_can_change_it()
    {
        Write("a.png", "a");
        var (library, viewer) = await AddLibraryAsync();
        await Host.RunJobsAsync();
        var (stranger, _) = await Host.NewUserAsync("bob");
        var file = (await NodesAsync(library.Id)).Single(n => n.Name == "a.png");

        Assert.Null(await Host.NodesAsync(n => n.FindAsync(stranger, file.Id, None)));
        Assert.Equal(FileErrors.ReadOnly, (await Host.NodesAsync(n => n.TrashAsync(viewer, file.Id, null, None))).Error);
        Assert.Equal(FileErrors.ReadOnly, (await Host.NodesAsync(n => n.UpdateAsync(viewer, file.Id, file.Version, "b.png", null, None))).Error);
        Assert.Equal(FileErrors.ReadOnly, (await Host.NodesAsync(n => n.CreateFolderAsync(viewer, library.RootNodeId, "New", None))).Error);
        Assert.True(File.Exists(Path.Combine(Folder, "a.png")));
    }

    [Fact]
    public async Task Changes_on_disk_are_followed_and_vanished_files_are_kept_as_missing()
    {
        Write("a.png", "a");
        Write("b.png", "b");
        var (library, viewer) = await AddLibraryAsync();
        await Host.RunJobsAsync();

        Write("a.png", "longer");
        File.Delete(Path.Combine(Folder, "b.png"));
        Write("c.png", "c");
        Host.Time.Advance(TimeSpan.FromMinutes(1));
        await ScanAsync(library.Id);

        var nodes = await NodesAsync(library.Id);
        Assert.Equal(6, nodes.Single(n => n.Name == "a.png").Size);
        var b = nodes.Single(n => n.Name == "b.png");
        Assert.NotNull(b.MissingSince);
        Assert.Null(await Host.NodesAsync(n => n.FindAsync(viewer, b.Id, None)));
        Assert.Contains(nodes, n => n.Name == "c.png" && n.MissingSince == null);

        // Back again: the same entry, with whatever it was part of.
        Write("b.png", "b");
        Host.Time.Advance(TimeSpan.FromMinutes(1));
        await ScanAsync(library.Id);
        var back = (await NodesAsync(library.Id)).Single(n => n.Name == "b.png");
        Assert.Equal(b.Id, back.Id);
        Assert.Null(back.MissingSince);
    }

    [Fact]
    public async Task A_folder_that_is_gone_or_suddenly_empty_marks_the_library_offline_and_changes_nothing()
    {
        for (var i = 0; i < 60; i++)
        {
            Write($"p{i:00}.png", "x");
        }

        var (library, _) = await AddLibraryAsync();
        await Host.RunJobsAsync();

        // An unmounted share: the mount point is there, but empty.
        var aside = Folder + "-aside";
        Directory.Move(Folder, aside);
        Directory.CreateDirectory(Folder);
        Host.Time.Advance(TimeSpan.FromMinutes(1));
        await ScanAsync(library.Id);
        Assert.Equal(LibraryErrors.FolderEmpty, (await LibraryAsync(library.Id)).StateReason);

        // No mount point at all.
        Directory.Delete(Folder);
        await ScanAsync(library.Id);
        Assert.Equal(LibraryState.Offline, (await LibraryAsync(library.Id)).State);
        Assert.Equal(LibraryErrors.FolderUnavailable, (await LibraryAsync(library.Id)).StateReason);

        // Most files gone at once.
        Directory.Move(aside, Folder);
        foreach (var file in Directory.GetFiles(Folder).Skip(5))
        {
            File.Delete(file);
        }

        await ScanAsync(library.Id);
        Assert.Equal(LibraryErrors.TooManyMissing, (await LibraryAsync(library.Id)).StateReason);
        Assert.All(await NodesAsync(library.Id), n => Assert.Null(n.MissingSince));
    }

    [Fact]
    public async Task An_interrupted_scan_continues_where_it_stopped()
    {
        for (var i = 0; i < 40; i++)
        {
            Write($"f{i:00}/p.png", "x");
        }

        var (library, _) = await AddLibraryAsync();
        _observer.StopAfter = 25;
        await Host.RunJobsAsync(_observer.Stopping.Token);

        var interrupted = await NodesAsync(library.Id);
        Assert.InRange(interrupted.Count(n => n.Name == "p.png"), 1, 39);
        Job job = null!;
        await Host.WithDbAsync(async db => job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Type == LibraryScanHandler.Type));
        Assert.Equal(JobState.Queued, job.State);
        Assert.NotNull(job.Checkpoint);

        var callsBefore = _observer.Calls;
        _observer.StopAfter = int.MaxValue;
        await Host.RunJobsAsync();

        Assert.Equal(40, (await NodesAsync(library.Id)).Count(n => n.Name == "p.png" && n.MissingSince == null));

        // 41 folders in all; at most the folders since the last checkpoint are read twice.
        Assert.InRange(_observer.Calls - callsBefore, 41 - callsBefore, 41 - callsBefore + LibraryScanHandler.CheckpointEvery);
    }

    [Fact]
    public async Task Deleting_a_library_removes_the_index_and_leaves_every_file()
    {
        Write("a.png", "a");
        var before = Snapshot(Folder);
        var (library, _) = await AddLibraryAsync();
        await Host.RunJobsAsync();

        Assert.Equal(LibraryErrors.ConfirmationMismatch, (await WithLibrariesAsync(s => s.DeleteAsync(library.Id, "screenshots", None))).Error);
        Assert.True((await WithLibrariesAsync(s => s.DeleteAsync(library.Id, "Screenshots", None))).Ok);

        Assert.Empty(await NodesAsync(library.Id));
        Assert.Equal(before, Snapshot(Folder));
    }

    [Fact]
    public async Task Symbolic_links_are_neither_listed_nor_followed()
    {
        var outside = Path.Combine(Host.ExternalRoot, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.png"), "s");
        Write("a.png", "a");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(Folder, "link"), outside);
            File.CreateSymbolicLink(Path.Combine(Folder, "file-link.png"), Path.Combine(outside, "secret.png"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Creating links needs a privilege Windows grants only in developer
            // mode; CI on Linux always runs this test.
            return;
        }

        var (library, _) = await AddLibraryAsync();
        await Host.RunJobsAsync();

        Assert.DoesNotContain(await NodesAsync(library.Id), n => n.Name is "link" or "file-link.png" or "secret.png");
        var folder = new ExternalFolder(Folder);
        Assert.Null(folder.Resolve("link/secret.png"));
        Assert.Null(folder.Resolve("file-link.png"));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("a/../../outside.png")]
    [InlineData("/etc/passwd")]
    [InlineData("a//b")]
    [InlineData("./a.png")]
    [InlineData("a\\..\\..\\b")]
    public void Paths_that_could_leave_the_folder_are_refused(string relative)
    {
        Assert.Null(new ExternalFolder(Folder).Resolve(relative));
    }
}

public sealed class SqliteExternalLibraryTests : ExternalLibraryContractTests<SqliteFileDatabase>;

[Trait("Category", "Containers")]
public sealed class PostgresExternalLibraryTests : ExternalLibraryContractTests<PostgresFileDatabase>;
