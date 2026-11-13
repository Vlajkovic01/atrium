using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Core.Storage;
using Atrium.Data;
using Atrium.Files.Blobs;
using Atrium.Files.Storage;
using Atrium.Identity.Crypto;
using Atrium.Jobs;
using Atrium.Storage;
using Atrium.Storage.Local;
using Atrium.Storage.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Files.Tests;

/// <summary>
/// The file services wired as in the server, over a real database and a
/// real local storage location in a temporary directory.
/// </summary>
public sealed class FileServicesHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    private FileServicesHost(FileTestDatabase database, FilesOptions? options, Action<IServiceCollection>? configure)
    {
        Database = database;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AtriumDbContext>(database.Use);
        services.AddSingleton<TimeProvider>(database.Time);
        services.AddSingleton(ServerKeys.LoadOrCreate(Path.Combine(_root, "keys")));
        services.AddSingleton<SecretProtector>();
        services.AddSingleton(new JobOptions());
        if (options is not null)
        {
            services.AddSingleton(options);
        }

        services.AddSingleton(new MediaTypeResolver(name => Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".mp4" => "video/mp4",
            ".txt" => "text/plain",
            _ => "application/octet-stream",
        }));
        services.AddSingleton(new External.ExternalLibraryOptions([Path.Combine(_root, "keys")]));
        configure?.Invoke(services);
        services.AddAtriumJobs();
        services.AddAtriumFiles();
        _services = services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>A directory for external library folders, outside every storage location.</summary>
    public string ExternalRoot => Path.Combine(_root, "external");

    /// <summary>Runs queued jobs until none is ready.</summary>
    public async Task RunJobsAsync(CancellationToken stopping = default)
    {
        var runner = _services.GetRequiredService<JobRunner>();
        while (!stopping.IsCancellationRequested
            && await runner.RunNextAsync(Enum.GetValues<Atrium.Core.Jobs.JobResourceClass>(), stopping))
        {
        }
    }

    public FileTestDatabase Database { get; }

    public MutableTime Time => Database.Time;

    public IServiceProvider Services => _services;

    public string StorageRoot => Path.Combine(_root, "storage");

    public StorageLocation Location { get; private set; } = null!;

    /// <summary>The location's files, read directly, bypassing Atrium.</summary>
    public IStorageProvider Raw { get; private set; } = null!;

    /// <summary>A host whose first location, the one personal libraries use, is local, or S3 when <paramref name="s3"/> is given.</summary>
    public static async Task<FileServicesHost> StartAsync(
        FileTestDatabase database, FilesOptions? options = null, S3StorageOptions? s3 = null, Action<IServiceCollection>? configure = null)
    {
        var host = new FileServicesHost(database, options, configure);
        if (s3 is null)
        {
            host.Location = await host.AddLocationAsync("main", host.StorageRoot);
            host.Raw = new LocalStorageProvider(host.StorageRoot);
        }
        else
        {
            host.Raw = new S3StorageProvider(s3);
            host.Location = await host.AddLocationAsync(
                "main", StorageProviderKind.S3, new StorageLocationConfig(null, s3), host.Raw);
        }

        return host;
    }

    private async Task<StorageLocation> AddLocationAsync(
        string name, StorageProviderKind kind, StorageLocationConfig config, IStorageProvider raw)
    {
        var providers = _services.GetRequiredService<StorageProviders>();
        var location = new StorageLocation
        {
            Name = name,
            Kind = kind,
            EncryptedConfig = providers.ProtectConfig(config),
            State = StorageLocationState.Online,
            CreatedAt = Time.GetUtcNow(),
        };
        await LocationIdentityFile.WriteAsync(
            raw, new LocationIdentity(location.Id, Time.GetUtcNow(), LocationIdentity.CurrentFormatVersion), default);
        await WithDbAsync(async db =>
        {
            db.StorageLocations.Add(location);
            await db.SaveChangesAsync();
        });
        Time.Advance(TimeSpan.FromSeconds(1));
        return location;
    }

    /// <summary>Registers a local location the way the server does: identity file first, then the row.</summary>
    public async Task<StorageLocation> AddLocationAsync(string name, string path)
    {
        Directory.CreateDirectory(path);
        var providers = _services.GetRequiredService<StorageProviders>();
        var location = new StorageLocation
        {
            Name = name,
            Kind = StorageProviderKind.LocalFileSystem,
            EncryptedConfig = providers.ProtectConfig(new StorageLocationConfig(new LocalStorageSettings(path), null)),
            State = StorageLocationState.Online,
            CreatedAt = Time.GetUtcNow(),
        };
        await LocationIdentityFile.WriteAsync(
            new LocalStorageProvider(path), new LocationIdentity(location.Id, Time.GetUtcNow(), LocationIdentity.CurrentFormatVersion), default);
        await WithDbAsync(async db =>
        {
            db.StorageLocations.Add(location);
            await db.SaveChangesAsync();
        });
        Time.Advance(TimeSpan.FromSeconds(1));
        return location;
    }

    public async Task<T> WithAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    public async Task WithDbAsync(Func<AtriumDbContext, Task> action)
    {
        await using var scope = _services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AtriumDbContext>());
    }

    public Task<Blob> StoreAsync(byte[] content, byte[]? expectedSha256 = null, Guid? locationId = null) =>
        WithAsync(sp => sp.GetRequiredService<BlobStore>()
            .StoreAsync(locationId ?? Location.Id, new MemoryStream(content), expectedSha256, CancellationToken.None));

    public async Task<List<Blob>> BlobsAsync()
    {
        List<Blob> blobs = [];
        await WithDbAsync(async db => blobs = await db.Blobs.AsNoTracking().ToListAsync());
        return blobs;
    }

    public async Task<List<string>> StoredKeysAsync(string prefix)
    {
        var keys = new List<string>();
        await foreach (var item in Raw.ListAsync(prefix, CancellationToken.None))
        {
            keys.Add(item.Key);
        }

        return keys;
    }

    public async Task<byte[]> ReadRawAsync(string key)
    {
        await using var stream = await Raw.OpenReadAsync(key, null, CancellationToken.None);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    /// <summary>A user with a personal library and one file pointing at the blob.</summary>
    public async Task<Node> AddFileAsync(Blob blob, string name = "file.bin")
    {
        var user = await Database.AddUserAsync("user" + Guid.NewGuid().ToString("N")[..8]);
        Library library = null!;
        await WithDbAsync(async db => library = (await new LibraryService(db, Time).EnsurePersonalAsync(user, default))!);
        var node = Database.NewNode(library, library.RootNodeId, name, NodeKind.File, blob.Id);
        await WithDbAsync(async db =>
        {
            db.Nodes.Add(node);
            await db.SaveChangesAsync();
        });
        return node;
    }

    /// <summary>A new user with their personal library, and the actor that stands for them.</summary>
    public async Task<(FileActor Actor, Library Library)> NewUserAsync(string? name = null)
    {
        var user = await Database.AddUserAsync(name ?? "user" + Guid.NewGuid().ToString("N")[..8]);
        Library library = null!;
        await WithDbAsync(async db => library = (await new LibraryService(db, Time).EnsurePersonalAsync(user, default))!);
        return (new FileActor(user.Id, IsGuest: false, ReadOnlySession: false, HidePersonal: false), library);
    }

    public Task<T> NodesAsync<T>(Func<NodeService, Task<T>> action) =>
        WithAsync(sp => action(sp.GetRequiredService<NodeService>()));

    public Task<T> TrashAsync<T>(Func<TrashService, Task<T>> action) =>
        WithAsync(sp => action(sp.GetRequiredService<TrashService>()));

    public Task<T> VersionsAsync<T>(Func<VersionService, Task<T>> action) =>
        WithAsync(sp => action(sp.GetRequiredService<VersionService>()));

    /// <summary>Stores the content and adds it as a file, the way a finished upload does.</summary>
    public async Task<FileResult<Node>> UploadAsync(
        FileActor actor, Guid parentId, string name, string content, ConflictPolicy policy = ConflictPolicy.Fail)
    {
        var blob = await StoreAsync(System.Text.Encoding.UTF8.GetBytes(content));
        return await NodesAsync(n => n.AddFileAsync(actor, parentId, name, blob, "text/plain", null, policy, CancellationToken.None));
    }

    public async Task<Node?> NodeAsync(Guid id)
    {
        Node? node = null;
        await WithDbAsync(async db => node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id));
        return node;
    }

    /// <summary>Runs a job type through the real job queue.</summary>
    public async Task<Atrium.Core.Jobs.Job> RunJobAsync(string type)
    {
        var job = await WithAsync(sp => sp.GetRequiredService<JobQueue>()
            .EnqueueAsync(new JobRequest(type), CancellationToken.None));
        var runner = _services.GetRequiredService<JobRunner>();
        while (await runner.RunNextAsync(Enum.GetValues<Atrium.Core.Jobs.JobResourceClass>(), CancellationToken.None))
        {
        }

        Atrium.Core.Jobs.Job done = null!;
        await WithDbAsync(async db => done = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id));
        return done;
    }

    /// <summary>Runs the blob collector through the real job queue.</summary>
    public async Task<Atrium.Core.Jobs.Job> CollectAsync()
    {
        var job = await WithAsync(sp => sp.GetRequiredService<JobQueue>()
            .EnqueueAsync(new JobRequest(BlobCollectorHandler.Type), CancellationToken.None));
        var runner = _services.GetRequiredService<JobRunner>();
        while (await runner.RunNextAsync(Enum.GetValues<Atrium.Core.Jobs.JobResourceClass>(), CancellationToken.None))
        {
        }

        Atrium.Core.Jobs.Job done = null!;
        await WithDbAsync(async db => done = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id));
        return done;
    }

    public async ValueTask DisposeAsync()
    {
        (Raw as IDisposable)?.Dispose();
        await _services.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp files must not fail a test run.
        }
    }
}
