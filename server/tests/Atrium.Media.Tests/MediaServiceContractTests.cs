using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Core.Jobs;
using Atrium.Core.Media;
using Atrium.Files;
using Atrium.Files.External;
using Atrium.Files.Tests;
using Atrium.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Media.Tests;

/// <summary>Metadata and thumbnails through the jobs, on each real database.</summary>
public abstract class MediaServiceContractTests<TDatabase> : IAsyncLifetime
    where TDatabase : FileTestDatabase, new()
{
    private static readonly CancellationToken None = CancellationToken.None;
    private readonly TDatabase _database = new();
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N") + "-cache");
    private FileServicesHost? _host;

    private FileServicesHost Host => _host ?? throw new InvalidOperationException("Not initialized.");

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _host = await FileServicesHost.StartAsync(
            _database,
            configure: s => s.AddAtriumMedia(new MediaOptions
            {
                CacheDir = _cache,
                TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade"),
            }));
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        await _database.DisposeAsync();
        try
        {
            Directory.Delete(_cache, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<Node> AddPhotoAsync(FileActor actor, Guid parentId, string name, byte[] content)
    {
        var blob = await Host.StoreAsync(content);
        var added = await Host.NodesAsync(n => n.AddFileAsync(actor, parentId, name, blob, "image/jpeg", null, ConflictPolicy.Fail, None));
        Assert.True(added.Ok, added.Error);

        // What an upload does when it finishes.
        await Host.WithAsync(async sp =>
        {
            await sp.GetRequiredService<JobQueue>().EnqueueAsync(MediaExtractHandler.Request(added.Value!.Id), None);
            return true;
        });
        return added.Value!;
    }

    private async Task<T> DbAsync<T>(Func<Atrium.Data.AtriumDbContext, Task<T>> query)
    {
        T result = default!;
        await Host.WithDbAsync(async db => result = await query(db));
        return result;
    }

    [Fact]
    public async Task An_uploaded_photo_is_dated_from_its_exif_and_gets_a_thumbnail()
    {
        var (actor, library) = await Host.NewUserAsync();
        var photo = await AddPhotoAsync(actor, library.RootNodeId, "beach.jpg", Samples.JpegWithExif(640, 480, "2023:08:15 07:45:00"));

        await Host.RunJobsAsync();

        var node = await DbAsync(db => db.Nodes.AsNoTracking().SingleAsync(n => n.Id == photo.Id));
        var info = await DbAsync(db => db.MediaInfos.AsNoTracking().SingleAsync(m => m.BlobId == photo.BlobId));
        Assert.Equal(new DateTimeOffset(2023, 8, 15, 7, 45, 0, TimeSpan.Zero), node.CapturedAt);
        Assert.Equal((640, 480), (info.Width, info.Height));
        Assert.Equal(ThumbnailState.Ready, info.ThumbnailState);
        Assert.Equal(MediaInfo.CurrentExtractorVersion, info.ExtractorVersion);
        Assert.Single(Directory.GetFiles(Path.Combine(_cache, "thumbs"), "*-s.webp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Two_files_with_the_same_content_share_one_reading()
    {
        var (actor, library) = await Host.NewUserAsync();
        var bytes = Samples.JpegWithExif(64, 64, "2022:01:02 03:04:05");
        var first = await AddPhotoAsync(actor, library.RootNodeId, "a.jpg", bytes);
        var second = await AddPhotoAsync(actor, library.RootNodeId, "b.jpg", bytes);

        await Host.RunJobsAsync();

        Assert.Equal(1, await DbAsync(db => db.MediaInfos.CountAsync()));
        var dates = await DbAsync(db => db.Nodes.AsNoTracking().Where(n => n.Id == first.Id || n.Id == second.Id).Select(n => n.CapturedAt).ToListAsync());
        Assert.All(dates, d => Assert.Equal(new DateTimeOffset(2022, 1, 2, 3, 4, 5, TimeSpan.Zero), d));
    }

    [Fact]
    public async Task A_broken_photo_is_marked_and_not_retried_forever()
    {
        var (actor, library) = await Host.NewUserAsync();
        var broken = await AddPhotoAsync(actor, library.RootNodeId, "broken.jpg", "not a picture"u8.ToArray());

        await Host.RunJobsAsync();

        var info = await DbAsync(db => db.MediaInfos.AsNoTracking().SingleAsync(m => m.BlobId == broken.BlobId));
        Assert.Equal(ThumbnailState.Unsupported, info.ThumbnailState);
        var job = await DbAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Type == MediaExtractHandler.Type));
        Assert.Equal(JobState.Succeeded, job.State);
    }

    [Fact]
    public async Task The_sweep_finds_photos_that_were_never_read()
    {
        var (actor, library) = await Host.NewUserAsync();
        var blob = await Host.StoreAsync(Samples.JpegWithExif(32, 32));
        var added = await Host.NodesAsync(n => n.AddFileAsync(actor, library.RootNodeId, "quiet.jpg", blob, "image/jpeg", null, ConflictPolicy.Fail, None));

        await Host.WithAsync(async sp =>
        {
            await sp.GetRequiredService<JobQueue>().EnqueueAsync(new JobRequest(MediaSweepHandler.Type), None);
            return true;
        });
        await Host.RunJobsAsync();

        Assert.True(await DbAsync(db => db.MediaInfos.AnyAsync(m => m.BlobId == added.Value!.BlobId)));
    }

    [Fact]
    public async Task A_photo_in_an_external_folder_is_read_where_it_is_and_again_when_it_changes()
    {
        var folder = Path.Combine(Host.ExternalRoot, "Camera");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "IMG_0001.jpg");
        await File.WriteAllBytesAsync(file, Samples.JpegWithExif(100, 50, "2021:12:24 20:00:00"));
        var admin = await _database.AddUserAsync("admin", UserRole.Admin);
        var library = await Host.WithAsync(sp => sp.GetRequiredService<ExternalLibraryService>()
            .CreateAsync(admin.Id, new NewExternalLibrary("Camera", folder, [admin.Id]), None));

        await Host.RunJobsAsync();

        var node = await DbAsync(db => db.Nodes.AsNoTracking().SingleAsync(n => n.Name == "IMG_0001.jpg"));
        Assert.Equal(new DateTimeOffset(2021, 12, 24, 20, 0, 0, TimeSpan.Zero), node.CapturedAt);
        Assert.Equal(100, (await DbAsync(db => db.MediaInfos.AsNoTracking().SingleAsync(m => m.NodeId == node.Id))).Width);

        // The same name, other content: read again, under a new thumbnail.
        await File.WriteAllBytesAsync(file, Samples.JpegWithExif(30, 60, "2020:06:01 12:00:00"));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));
        Host.Time.Advance(TimeSpan.FromMinutes(1));
        await Host.WithAsync(sp => sp.GetRequiredService<ExternalLibraryService>().EnqueueScanAsync(library.Value!.Id, JobPriority.User, None));
        await Host.RunJobsAsync();

        var changed = await DbAsync(db => db.Nodes.AsNoTracking().SingleAsync(n => n.Id == node.Id));
        Assert.Equal(new DateTimeOffset(2020, 6, 1, 12, 0, 0, TimeSpan.Zero), changed.CapturedAt);
        Assert.Equal(30, (await DbAsync(db => db.MediaInfos.AsNoTracking().SingleAsync(m => m.NodeId == node.Id))).Width);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_cache, "thumbs"), "*-s.webp", SearchOption.AllDirectories).Length);
    }
}

public sealed class SqliteMediaServiceTests : MediaServiceContractTests<SqliteFileDatabase>;

[Trait("Category", "Containers")]
public sealed class PostgresMediaServiceTests : MediaServiceContractTests<PostgresFileDatabase>;
