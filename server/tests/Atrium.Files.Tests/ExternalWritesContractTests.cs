using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Files.External;
using Atrium.Files.Uploads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Files.Tests;

/// <summary>
/// Changing an external library's files once an admin enabled writes:
/// every change reaches the folder, deleting only moves into the library's
/// own trash, and switching writes off stops everything at once.
/// </summary>
public abstract class ExternalWritesContractTests<TDatabase> : IAsyncLifetime
    where TDatabase : FileTestDatabase, new()
{
    private static readonly CancellationToken None = CancellationToken.None;
    private readonly TDatabase _database = new();
    private FileServicesHost? _host;
    private Guid _adminId;

    private FileServicesHost Host => _host ?? throw new InvalidOperationException("Not initialized.");

    private string Folder => Path.Combine(Host.ExternalRoot, "Photos");

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _host = await FileServicesHost.StartAsync(_database);
        Directory.CreateDirectory(Folder);
        Write("a.jpg", "aaa");
        Write("Trip/b.jpg", "bb");
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

    private string Disk(string relative) => Path.Combine(Folder, relative.Replace('/', Path.DirectorySeparatorChar));

    private Task<T> LibrariesAsync<T>(Func<ExternalLibraryService, Task<T>> action) =>
        Host.WithAsync(sp => action(sp.GetRequiredService<ExternalLibraryService>()));

    /// <summary>A scanned library with a writer and a viewer; writes still off.</summary>
    private async Task<(Library Library, FileActor Writer, FileActor Viewer)> LibraryAsync()
    {
        var admin = await _database.AddUserAsync("admin", UserRole.Admin);
        _adminId = admin.Id;
        var writer = await _database.AddUserAsync("ana");
        var viewer = await _database.AddUserAsync("bob");
        var library = (await LibrariesAsync(s => s.CreateAsync(admin.Id, new NewExternalLibrary("Photos", Folder, [writer.Id, viewer.Id]), None))).Value!;
        await Host.RunJobsAsync();
        var updated = await LibrariesAsync(s => s.UpdateAsync(library.Id, library.Version, null, [writer.Id, viewer.Id], [writer.Id], null, None));
        Assert.True(updated.Ok, updated.Error);
        return (updated.Value!, new FileActor(writer.Id, false, false, false), new FileActor(viewer.Id, false, false, false));
    }

    private Task<FileResult<Library>> EnableAsync(Library library, string name = "Photos") =>
        LibrariesAsync(s => s.SetWritesAsync(_adminId, library.Id, true, name, null, None));

    private async Task<Node> NodeAsync(Guid libraryId, string name)
    {
        Node node = null!;
        await Host.WithDbAsync(async db => node = await db.Nodes.AsNoTracking().SingleAsync(n => n.LibraryId == libraryId && n.Name == name));
        return node;
    }

    [Fact]
    public async Task Writes_are_enabled_only_with_the_name_typed_and_on_a_writable_folder()
    {
        var (library, writer, _) = await LibraryAsync();
        var a = await NodeAsync(library.Id, "a.jpg");

        Assert.Equal(FileErrors.ReadOnly, (await Host.NodesAsync(n => n.TrashAsync(writer, a.Id, null, None))).Error);
        Assert.Equal(LibraryErrors.ConfirmationMismatch, (await EnableAsync(library, "photos")).Error);

        // Something in the way of Atrium's own folder makes the folder unwritable for it.
        File.WriteAllText(Disk(ExternalFolder.TrashFolderName), "not a folder");
        Assert.Equal(LibraryErrors.FolderReadOnly, (await EnableAsync(library)).Error);
        File.Delete(Disk(ExternalFolder.TrashFolderName));

        var enabled = await EnableAsync(library);
        Assert.True(enabled.Ok);
        Assert.False(enabled.Value!.ReadOnly);
        Assert.NotNull(enabled.Value.WritesEnabledAt);
    }

    [Fact]
    public async Task Allowing_changes_also_chooses_who_makes_them_so_it_is_never_on_for_nobody()
    {
        var admin = await _database.AddUserAsync("root", UserRole.Admin);
        var library = (await LibrariesAsync(s => s.CreateAsync(admin.Id, new NewExternalLibrary("Photos", Folder, [admin.Id]), None))).Value!;
        await Host.RunJobsAsync();
        var actor = new FileActor(admin.Id, false, false, false);
        var a = await NodeAsync(library.Id, "a.jpg");

        var enabled = await LibrariesAsync(s => s.SetWritesAsync(admin.Id, library.Id, true, "Photos", [admin.Id], None));
        Assert.True(enabled.Ok, enabled.Error);

        Assert.True((await Host.NodesAsync(n => n.UpdateAsync(actor, a.Id, a.Version, "renamed.jpg", null, None))).Ok);
        Assert.True(File.Exists(Disk("renamed.jpg")));
        Assert.Equal(
            LibraryErrors.UnknownUser,
            (await LibrariesAsync(s => s.SetWritesAsync(admin.Id, library.Id, true, "Photos", [Guid.NewGuid()], None))).Error);
    }

    [Fact]
    public async Task Only_people_allowed_to_change_files_can_change_them()
    {
        var (library, _, viewer) = await LibraryAsync();
        await EnableAsync(library);
        var a = await NodeAsync(library.Id, "a.jpg");

        Assert.Equal(FileErrors.ReadOnly, (await Host.NodesAsync(n => n.TrashAsync(viewer, a.Id, null, None))).Error);
        Assert.True(File.Exists(Disk("a.jpg")));
    }

    [Fact]
    public async Task Renaming_moving_and_new_folders_reach_the_disk()
    {
        var (library, writer, _) = await LibraryAsync();
        await EnableAsync(library);
        var a = await NodeAsync(library.Id, "a.jpg");
        var trip = await NodeAsync(library.Id, "Trip");

        var renamed = await Host.NodesAsync(n => n.UpdateAsync(writer, a.Id, a.Version, "beach.jpg", null, None));
        Assert.True(renamed.Ok, renamed.Error);
        Assert.True(File.Exists(Disk("beach.jpg")));
        Assert.False(File.Exists(Disk("a.jpg")));

        var moved = await Host.NodesAsync(n => n.UpdateAsync(writer, a.Id, renamed.Value!.Version, null, trip.Id, None));
        Assert.True(moved.Ok, moved.Error);
        Assert.Equal("aaa", File.ReadAllText(Disk("Trip/beach.jpg")));
        Assert.Equal("Trip/beach.jpg", (await NodeAsync(library.Id, "beach.jpg")).ExternalPath);

        var folder = await Host.NodesAsync(n => n.CreateFolderAsync(writer, library.RootNodeId, "2025", None));
        Assert.True(folder.Ok, folder.Error);
        Assert.True(Directory.Exists(Disk("2025")));

        // A folder rename takes everything in it along, on disk and in the index.
        var tripNow = await NodeAsync(library.Id, "Trip");
        Assert.True((await Host.NodesAsync(n => n.UpdateAsync(writer, trip.Id, tripNow.Version, "Holiday", null, None))).Ok);
        Assert.Equal("bb", File.ReadAllText(Disk("Holiday/b.jpg")));
        Assert.Equal("Holiday/b.jpg", (await NodeAsync(library.Id, "b.jpg")).ExternalPath);

        // The next scan agrees: nothing missing, nothing new.
        Host.Time.Advance(TimeSpan.FromMinutes(1));
        await LibrariesAsync(s => s.EnqueueScanAsync(library.Id, Core.Jobs.JobPriority.User, None));
        await Host.RunJobsAsync();
        List<Node> all = [];
        await Host.WithDbAsync(async db => all = await db.Nodes.AsNoTracking().Where(n => n.LibraryId == library.Id).ToListAsync());
        Assert.All(all, n => Assert.Null(n.MissingSince));
        Assert.Equal(5, all.Count);
    }

    [Fact]
    public async Task A_name_already_on_disk_is_a_conflict_and_nothing_is_overwritten()
    {
        var (library, writer, _) = await LibraryAsync();
        await EnableAsync(library);
        Write("c.jpg", "made outside Atrium after the scan");
        var a = await NodeAsync(library.Id, "a.jpg");

        var result = await Host.NodesAsync(n => n.UpdateAsync(writer, a.Id, a.Version, "c.jpg", null, None));

        Assert.Equal(FileErrors.NameConflict, result.Error);
        Assert.Equal("aaa", File.ReadAllText(Disk("a.jpg")));
        Assert.Equal("made outside Atrium after the scan", File.ReadAllText(Disk("c.jpg")));
    }

    [Fact]
    public async Task Deleting_moves_into_the_library_trash_and_restoring_brings_it_back()
    {
        var (library, writer, _) = await LibraryAsync();
        await EnableAsync(library);
        var trip = await NodeAsync(library.Id, "Trip");

        Assert.True((await Host.NodesAsync(n => n.TrashAsync(writer, trip.Id, null, None))).Ok);

        Assert.False(Directory.Exists(Disk("Trip")));
        var inTrash = Disk($"{ExternalFolder.TrashFolderName}/{trip.Id:N}/Trip/b.jpg");
        Assert.Equal("bb", File.ReadAllText(inTrash));
        var listed = Assert.Single(await Host.TrashAsync(t => t.ListAsync(writer, None)));
        Assert.Equal(trip.Id, listed.Node.Id);
        Assert.True((await NodeAsync(library.Id, "b.jpg")).InTrash);

        // A scan neither indexes the trash nor calls the trashed files missing.
        Host.Time.Advance(TimeSpan.FromMinutes(1));
        await LibrariesAsync(s => s.EnqueueScanAsync(library.Id, Core.Jobs.JobPriority.User, None));
        await Host.RunJobsAsync();
        Assert.Null((await NodeAsync(library.Id, "b.jpg")).MissingSince);

        var restored = await Host.TrashAsync(t => t.RestoreAsync(writer, trip.Id, null, ConflictPolicy.Fail, None));
        Assert.True(restored.Ok, restored.Error);
        Assert.Equal("bb", File.ReadAllText(Disk("Trip/b.jpg")));
        Assert.False(Directory.Exists(Disk($"{ExternalFolder.TrashFolderName}/{trip.Id:N}")));
        Assert.False((await NodeAsync(library.Id, "b.jpg")).InTrash);
    }

    [Fact]
    public async Task The_trash_is_emptied_from_disk_only_after_its_retention_and_only_while_writes_are_on()
    {
        var (library, writer, _) = await LibraryAsync();
        await EnableAsync(library);
        var a = await NodeAsync(library.Id, "a.jpg");
        await Host.NodesAsync(n => n.TrashAsync(writer, a.Id, null, None));
        var inTrash = Disk($"{ExternalFolder.TrashFolderName}/{a.Id:N}/a.jpg");

        // Writes switched off: past the retention, the file is still kept.
        await LibrariesAsync(s => s.SetWritesAsync(_adminId, library.Id, false, null, null, None));
        Host.Time.Advance(TimeSpan.FromDays(31));
        await Host.WithAsync(async sp =>
        {
            await ActivatorRun(sp);
            return true;
        });
        Assert.True(File.Exists(inTrash));

        await EnableAsync(library);
        await Host.WithAsync(async sp =>
        {
            await ActivatorRun(sp);
            return true;
        });
        Assert.False(File.Exists(inTrash));
        Assert.Empty(await Host.TrashAsync(t => t.ListAsync(writer, None)));
    }

    [Fact]
    public async Task Switching_writes_off_stops_every_change_at_once()
    {
        var (library, writer, _) = await LibraryAsync();
        await EnableAsync(library);
        await LibrariesAsync(s => s.SetWritesAsync(_adminId, library.Id, false, null, null, None));
        var a = await NodeAsync(library.Id, "a.jpg");

        Assert.Equal(FileErrors.ReadOnly, (await Host.NodesAsync(n => n.TrashAsync(writer, a.Id, null, None))).Error);
        Assert.Equal(FileErrors.ReadOnly, (await Host.NodesAsync(n => n.CreateFolderAsync(writer, library.RootNodeId, "x", None))).Error);
        Assert.True(File.Exists(Disk("a.jpg")));
    }

    [Fact]
    public async Task An_upload_lands_in_the_folder_and_replacing_keeps_the_old_file_in_the_trash()
    {
        var (library, writer, _) = await LibraryAsync();
        await EnableAsync(library);

        var first = await UploadAsync(writer, library.RootNodeId, "new.jpg", "fresh", ConflictPolicy.Fail);
        Assert.True(first.Ok, first.Error);
        Assert.Equal("fresh", File.ReadAllText(Disk("new.jpg")));

        var kept = await UploadAsync(writer, library.RootNodeId, "new.jpg", "second", ConflictPolicy.Rename);
        Assert.True(kept.Ok, kept.Error);
        Assert.Equal("second", File.ReadAllText(Disk("new (2).jpg")));

        var old = await NodeAsync(library.Id, "new.jpg");
        var replaced = await UploadAsync(writer, library.RootNodeId, "new.jpg", "third", ConflictPolicy.Replace);
        Assert.True(replaced.Ok, replaced.Error);
        Assert.Equal("third", File.ReadAllText(Disk("new.jpg")));
        Assert.Equal("fresh", File.ReadAllText(Disk($"{ExternalFolder.TrashFolderName}/{old.Id:N}/new.jpg")));

        // Nothing is left in the staging area.
        Assert.DoesNotContain(
            Directory.EnumerateFiles(ExternalWriter.StagingRoot(library), "*", SearchOption.AllDirectories),
            f => !f.Contains(".probe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Copying_inside_an_external_folder_is_refused()
    {
        var (library, writer, _) = await LibraryAsync();
        await EnableAsync(library);
        var a = await NodeAsync(library.Id, "a.jpg");

        Assert.Equal(FileErrors.NotSupported, (await Host.NodesAsync(n => n.CopyAsync(writer, a.Id, library.RootNodeId, "copy.jpg", None))).Error);
    }

    private async Task<FileResult<Upload>> UploadAsync(FileActor actor, Guid parentId, string name, string content, ConflictPolicy policy)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return await Host.WithAsync(async sp =>
        {
            var uploads = sp.GetRequiredService<UploadService>();
            var created = await uploads.CreateAsync(actor, new NewUpload(parentId, name, bytes.Length, "image/jpeg", null, null, policy), None);
            if (!created.Ok)
            {
                return created;
            }

            return await uploads.AppendAsync(actor, created.Value!.Id, new UploadChunk(0, bytes.Length, new MemoryStream(bytes), null, null), None);
        });
    }

    private static Task ActivatorRun(IServiceProvider services) =>
        ActivatorUtilities.CreateInstance<FileRetentionHandler>(services).RunAsync(null!, None);
}

public sealed class SqliteExternalWritesTests : ExternalWritesContractTests<SqliteFileDatabase>;

[Trait("Category", "Containers")]
public sealed class PostgresExternalWritesTests : ExternalWritesContractTests<PostgresFileDatabase>;
