using System.Security.Cryptography;
using Atrium.Core.Files;
using Atrium.Core.Jobs;
using Atrium.Files.Blobs;
using Atrium.Files.Uploads;
using Atrium.Storage.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Files.Tests;

internal static class UploadSteps
{
    public static Task<FileResult<Upload>> CreateAsync(FileServicesHost host, FileActor actor, Guid parentId, string name, long length, byte[]? sha256 = null) =>
        host.WithAsync(sp => sp.GetRequiredService<UploadService>().CreateAsync(
            actor, new NewUpload(parentId, name, length, "application/octet-stream", null, sha256, ConflictPolicy.Fail), CancellationToken.None));

    public static Task<FileResult<Upload>> AppendAsync(FileServicesHost host, FileActor actor, Upload upload, long offset, byte[] chunk) =>
        host.WithAsync(sp => sp.GetRequiredService<UploadService>().AppendAsync(
            actor, upload.Id, new UploadChunk(offset, chunk.Length, new MemoryStream(chunk), null, null), CancellationToken.None));

    public static async Task<byte[]> ReadFileAsync(FileServicesHost host, Guid nodeId)
    {
        var node = (await host.NodeAsync(nodeId))!;
        Blob blob = null!;
        await host.WithDbAsync(async db => blob = await db.Blobs.AsNoTracking().SingleAsync(b => b.Id == node.BlobId));
        return await host.ReadRawAsync(blob.Key);
    }
}

/// <summary>Upload housekeeping, on each real database.</summary>
public abstract class UploadServiceContractTests<TDatabase> : IAsyncLifetime
    where TDatabase : FileTestDatabase, new()
{
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

    [Fact]
    public async Task An_upload_nobody_continues_expires_and_its_parts_are_removed()
    {
        var (actor, library) = await Host.NewUserAsync();
        var upload = (await UploadSteps.CreateAsync(Host, actor, library.RootNodeId, "a.bin", 10)).Value!;
        await UploadSteps.AppendAsync(Host, actor, upload, 0, new byte[4]);
        var partDirectory = Directory.GetDirectories(Host.StorageRoot, "*", SearchOption.AllDirectories)
            .Single(d => d.Contains(upload.MultipartUploadId, StringComparison.Ordinal));

        Host.Time.Advance(TimeSpan.FromHours(23));
        await Host.RunJobAsync(UploadCleanupHandler.Type);
        Assert.Equal(UploadState.Active, (await FindAsync(upload.Id)).State);

        Host.Time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(JobState.Succeeded, (await Host.RunJobAsync(UploadCleanupHandler.Type)).State);

        var expired = await FindAsync(upload.Id);
        Assert.Equal(UploadState.Cancelled, expired.State);
        Assert.Equal(UploadErrors.Expired, expired.ErrorCode);
        Assert.False(Directory.Exists(partDirectory));
        Assert.Equal(UploadErrors.NotActive, (await UploadSteps.AppendAsync(Host, actor, upload, 4, new byte[6])).Error);
    }

    [Fact]
    public async Task Activity_keeps_an_upload_alive()
    {
        var (actor, library) = await Host.NewUserAsync();
        var upload = (await UploadSteps.CreateAsync(Host, actor, library.RootNodeId, "a.bin", 10)).Value!;
        Host.Time.Advance(TimeSpan.FromHours(20));
        await UploadSteps.AppendAsync(Host, actor, upload, 0, new byte[4]);
        Host.Time.Advance(TimeSpan.FromHours(20));

        await Host.RunJobAsync(UploadCleanupHandler.Type);

        Assert.Equal(UploadState.Active, (await FindAsync(upload.Id)).State);
    }

    [Fact]
    public async Task Stale_staged_objects_are_removed_and_recent_ones_kept()
    {
        await Host.Raw.WriteAsync(BlobStore.StagingPrefix + "old", new MemoryStream([1]), false, CancellationToken.None);
        File.SetLastWriteTimeUtc(Path.Combine(Host.StorageRoot, "atrium", "staging", "old"), Host.Time.GetUtcNow().UtcDateTime.AddDays(-2));
        await Host.Raw.WriteAsync(BlobStore.StagingPrefix + "new", new MemoryStream([1]), false, CancellationToken.None);
        File.SetLastWriteTimeUtc(Path.Combine(Host.StorageRoot, "atrium", "staging", "new"), Host.Time.GetUtcNow().UtcDateTime);

        await Host.RunJobAsync(UploadCleanupHandler.Type);

        Assert.Equal([BlobStore.StagingPrefix + "new"], await Host.StoredKeysAsync(BlobStore.StagingPrefix));
    }

    [Fact]
    public async Task A_finished_upload_produces_a_verified_file()
    {
        var (actor, library) = await Host.NewUserAsync();
        var content = RandomNumberGenerator.GetBytes(3000);
        var upload = (await UploadSteps.CreateAsync(Host, actor, library.RootNodeId, "r.bin", content.Length, SHA256.HashData(content))).Value!;

        await UploadSteps.AppendAsync(Host, actor, upload, 0, content[..1000]);
        var done = await UploadSteps.AppendAsync(Host, actor, upload, 1000, content[1000..]);

        Assert.Equal(UploadState.Completed, done.Value!.State);
        Assert.Equal(content, await UploadSteps.ReadFileAsync(Host, done.Value.NodeId!.Value));
        Assert.Empty(await Host.StoredKeysAsync(BlobStore.StagingPrefix));
    }

    private async Task<Upload> FindAsync(Guid id)
    {
        Upload upload = null!;
        await Host.WithDbAsync(async db => upload = await db.Uploads.AsNoTracking().SingleAsync(u => u.Id == id));
        return upload;
    }
}

public sealed class SqliteUploadServiceTests : UploadServiceContractTests<SqliteFileDatabase>;

[Trait("Category", "Containers")]
public sealed class PostgresUploadServiceTests : UploadServiceContractTests<PostgresFileDatabase>;

/// <summary>
/// Uploads to a real S3-compatible server, where every part but the last
/// must be at least 5 MB. Needs Docker, so it runs in CI.
/// </summary>
[Trait("Category", "Containers")]
public sealed class S3UploadTests(S3ServerFixture server) : IClassFixture<S3ServerFixture>, IAsyncLifetime
{
    private const int MiB = 1024 * 1024;
    private readonly SqliteFileDatabase _database = new();
    private FileServicesHost? _host;

    private FileServicesHost Host => _host ?? throw new InvalidOperationException("Not initialized.");

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _host = await FileServicesHost.StartAsync(_database, s3: server.Options("uploads-" + Guid.NewGuid().ToString("N")));
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_multi_part_upload_becomes_a_verified_file(bool hashLostInRestart)
    {
        var (actor, library) = await Host.NewUserAsync();
        var content = RandomNumberGenerator.GetBytes((11 * MiB) + 123);
        var upload = (await UploadSteps.CreateAsync(Host, actor, library.RootNodeId, "big.bin", content.Length, SHA256.HashData(content))).Value!;

        Assert.True((await UploadSteps.AppendAsync(Host, actor, upload, 0, content[..(5 * MiB)])).Ok);
        Assert.True((await UploadSteps.AppendAsync(Host, actor, upload, 5 * MiB, content[(5 * MiB)..(10 * MiB)])).Ok);
        if (hashLostInRestart)
        {
            Host.Services.GetRequiredService<UploadRunningHashes>().Forget(upload.Id);
        }

        var done = await UploadSteps.AppendAsync(Host, actor, upload, 10 * MiB, content[(10 * MiB)..]);

        Assert.Equal(UploadState.Completed, done.Value!.State);
        Assert.Equal(content, await UploadSteps.ReadFileAsync(Host, done.Value.NodeId!.Value));
    }

    [Fact]
    public async Task A_chunk_smaller_than_a_part_is_refused_unless_it_is_the_last()
    {
        var (actor, library) = await Host.NewUserAsync();
        var upload = (await UploadSteps.CreateAsync(Host, actor, library.RootNodeId, "big.bin", 8 * MiB)).Value!;

        var small = await UploadSteps.AppendAsync(Host, actor, upload, 0, new byte[MiB]);

        Assert.Equal(UploadErrors.ChunkSize, small.Error);
        Assert.True((await UploadSteps.AppendAsync(Host, actor, upload, 0, new byte[5 * MiB])).Ok);
        Assert.Equal(UploadState.Completed, (await UploadSteps.AppendAsync(Host, actor, upload, 5 * MiB, new byte[3 * MiB])).Value!.State);
    }
}
