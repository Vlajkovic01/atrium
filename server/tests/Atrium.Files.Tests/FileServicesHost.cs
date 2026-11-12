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

    private FileServicesHost(FileTestDatabase database, FilesOptions? options)
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

        services.AddAtriumJobs();
        services.AddAtriumFiles();
        _services = services.BuildServiceProvider(validateScopes: true);
    }

    public FileTestDatabase Database { get; }

    public MutableTime Time => Database.Time;

    public IServiceProvider Services => _services;

    public string StorageRoot => Path.Combine(_root, "storage");

    public StorageLocation Location { get; private set; } = null!;

    /// <summary>The location's files, read directly, bypassing Atrium.</summary>
    public IStorageProvider Raw { get; private set; } = null!;

    public static async Task<FileServicesHost> StartAsync(FileTestDatabase database, FilesOptions? options = null)
    {
        var host = new FileServicesHost(database, options);
        host.Location = await host.AddLocationAsync("main", host.StorageRoot);
        host.Raw = new LocalStorageProvider(host.StorageRoot);
        return host;
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
