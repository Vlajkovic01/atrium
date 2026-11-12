using System.Security.Cryptography;
using System.Text;
using Atrium.Core.Files;
using Atrium.Core.Jobs;
using Atrium.Files.Blobs;
using Atrium.Files.Storage;
using Atrium.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Files.Tests;

/// <summary>
/// Storing and collecting content, against a real local storage location
/// and each real database. These are the paths where a bug loses files.
/// </summary>
public abstract class BlobStoreContractTests<TDatabase> : IAsyncLifetime
    where TDatabase : FileTestDatabase, new()
{
    private readonly TDatabase _database = new();
    private FileServicesHost? _host;

    protected FileServicesHost Host => _host ?? throw new InvalidOperationException("Not initialized.");

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

    private static byte[] Content(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task Stored_content_lands_under_its_hash_and_nothing_is_left_in_staging()
    {
        var content = Content("hello, atrium");

        var blob = await Host.StoreAsync(content);

        Assert.Equal(SHA256.HashData(content), blob.Sha256);
        Assert.Equal(content.Length, blob.Size);
        Assert.Equal(BlobState.Stored, blob.State);
        Assert.Equal(content, await Host.ReadRawAsync(blob.Key));
        Assert.Empty(await Host.StoredKeysAsync(BlobStore.StagingPrefix));
        Assert.Single(await Host.BlobsAsync());
    }

    [Fact]
    public async Task The_same_content_is_stored_once()
    {
        var first = await Host.StoreAsync(Content("same"));
        var second = await Host.StoreAsync(Content("same"));

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await Host.BlobsAsync());
        Assert.Single(await Host.StoredKeysAsync("atrium/blobs/"));
        Assert.Empty(await Host.StoredKeysAsync(BlobStore.StagingPrefix));
    }

    [Fact]
    public async Task Parallel_uploads_of_the_same_content_make_one_blob()
    {
        var blobs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Host.StoreAsync(Content("racing"))));

        Assert.Single(blobs.Select(b => b.Id).Distinct());
        Assert.Single(await Host.BlobsAsync());
    }

    [Fact]
    public async Task Content_that_differs_from_the_declared_hash_is_not_stored()
    {
        var wrong = SHA256.HashData(Content("something else"));

        await Assert.ThrowsAsync<BlobHashMismatchException>(() => Host.StoreAsync(Content("actual"), wrong));

        Assert.Empty(await Host.BlobsAsync());
        Assert.Empty(await Host.StoredKeysAsync("atrium/blobs/"));
        Assert.Empty(await Host.StoredKeysAsync(BlobStore.StagingPrefix));
    }

    [Fact]
    public async Task The_declared_hash_is_accepted_when_it_matches()
    {
        var content = Content("declared");

        var blob = await Host.StoreAsync(content, SHA256.HashData(content));

        Assert.Equal(SHA256.HashData(content), blob.Sha256);
    }

    [Fact]
    public async Task Content_left_on_storage_without_a_record_is_reused_after_checking_it()
    {
        var content = Content("left behind by a crash");
        var key = Blob.KeyFor(SHA256.HashData(content));
        await Host.Raw.WriteAsync(key, new MemoryStream(content), overwrite: false, CancellationToken.None);

        var blob = await Host.StoreAsync(content);

        Assert.Equal(content, await Host.ReadRawAsync(blob.Key));
        Assert.Single(await Host.BlobsAsync());
    }

    [Fact]
    public async Task A_damaged_object_without_a_record_is_replaced_by_the_verified_upload()
    {
        var content = Content("the real content");
        var key = Blob.KeyFor(SHA256.HashData(content));
        await Host.Raw.WriteAsync(key, new MemoryStream(Content("garbage!!!!!!!!!")), overwrite: false, CancellationToken.None);

        var blob = await Host.StoreAsync(content);

        Assert.Equal(content, await Host.ReadRawAsync(blob.Key));
    }

    [Fact]
    public async Task Content_missing_from_storage_is_restored_by_the_next_upload_of_it()
    {
        var content = Content("someone deleted me by hand");
        var blob = await Host.StoreAsync(content);
        await Host.Raw.DeleteAsync(blob.Key, CancellationToken.None);

        var again = await Host.StoreAsync(content);

        Assert.Equal(blob.Id, again.Id);
        Assert.Equal(content, await Host.ReadRawAsync(blob.Key));
    }

    [Fact]
    public async Task Nothing_is_written_to_a_location_that_lost_its_identity()
    {
        await Host.Raw.DeleteAsync(LocationIdentityFile.Key, CancellationToken.None);

        var refused = await Assert.ThrowsAsync<LocationUnavailableException>(() => Host.StoreAsync(Content("x")));

        Assert.Equal(StorageProviders.IdentityMissing, refused.Code);
        Assert.Empty(await Host.StoredKeysAsync("atrium/"));
    }

    [Fact]
    public async Task Nothing_is_written_to_a_location_that_belongs_to_someone_else()
    {
        await Host.Raw.DeleteAsync(LocationIdentityFile.Key, CancellationToken.None);
        await LocationIdentityFile.WriteAsync(
            Host.Raw, new LocationIdentity(Guid.NewGuid(), Host.Time.GetUtcNow(), 1), CancellationToken.None);

        var refused = await Assert.ThrowsAsync<LocationUnavailableException>(() => Host.StoreAsync(Content("x")));

        Assert.Equal(StorageProviders.IdentityMismatch, refused.Code);
        Assert.Equal([LocationIdentityFile.Key], await Host.StoredKeysAsync("atrium/"));
    }

    [Fact]
    public async Task The_collector_keeps_referenced_content_and_waits_before_deleting_the_rest()
    {
        var kept = await Host.StoreAsync(Content("referenced by a file"));
        var orphan = await Host.StoreAsync(Content("referenced by nothing"));
        await Host.AddFileAsync(kept);

        Assert.Equal(JobState.Succeeded, (await Host.CollectAsync()).State);
        var marked = (await Host.BlobsAsync()).Single(b => b.Id == orphan.Id);
        Assert.Equal(BlobState.PendingDelete, marked.State);
        Assert.Equal(Content("referenced by nothing"), await Host.ReadRawAsync(orphan.Key));

        Host.Time.Advance(TimeSpan.FromDays(6));
        await Host.CollectAsync();
        Assert.Equal(2, (await Host.BlobsAsync()).Count);

        Host.Time.Advance(TimeSpan.FromDays(1));
        await Host.CollectAsync();
        var left = Assert.Single(await Host.BlobsAsync());
        Assert.Equal(kept.Id, left.Id);
        Assert.Equal(BlobState.Stored, left.State);
        Assert.Equal([kept.Key], await Host.StoredKeysAsync("atrium/blobs/"));
    }

    [Fact]
    public async Task Content_kept_only_as_an_earlier_version_is_not_collected()
    {
        var current = await Host.StoreAsync(Content("new"));
        var older = await Host.StoreAsync(Content("old"));
        var file = await Host.AddFileAsync(current);
        await Host.WithDbAsync(async db =>
        {
            db.NodeVersions.Add(new NodeVersion { NodeId = file.Id, BlobId = older.Id, Size = 3, CreatedAt = Host.Time.GetUtcNow() });
            await db.SaveChangesAsync();
        });

        await Host.CollectAsync();
        Host.Time.Advance(TimeSpan.FromDays(8));
        await Host.CollectAsync();

        Assert.Equal(2, (await Host.BlobsAsync()).Count(b => b.State == BlobState.Stored));
    }

    [Fact]
    public async Task Uploading_content_that_waits_for_deletion_saves_it()
    {
        var content = Content("almost gone");
        var blob = await Host.StoreAsync(content);
        await Host.CollectAsync();
        Assert.Equal(BlobState.PendingDelete, (await Host.BlobsAsync()).Single().State);

        var again = await Host.StoreAsync(content);
        await Host.AddFileAsync(again);
        Host.Time.Advance(TimeSpan.FromDays(30));
        await Host.CollectAsync();

        Assert.Equal(blob.Id, again.Id);
        Assert.Equal(BlobState.Stored, (await Host.BlobsAsync()).Single().State);
        Assert.Equal(content, await Host.ReadRawAsync(blob.Key));
    }

    [Fact]
    public async Task A_file_created_after_marking_saves_its_content()
    {
        var blob = await Host.StoreAsync(Content("marked, then used"));
        await Host.CollectAsync();
        await Host.AddFileAsync(blob);

        Host.Time.Advance(TimeSpan.FromDays(8));
        await Host.CollectAsync();

        Assert.Equal(BlobState.Stored, (await Host.BlobsAsync()).Single().State);
        Assert.Equal(Content("marked, then used"), await Host.ReadRawAsync(blob.Key));
    }

    [Fact]
    public async Task The_collector_deletes_nothing_on_a_location_it_cannot_verify()
    {
        var blob = await Host.StoreAsync(Content("on a share that went away"));
        await Host.CollectAsync();
        Host.Time.Advance(TimeSpan.FromDays(8));
        await Host.Raw.DeleteAsync(LocationIdentityFile.Key, CancellationToken.None);
        Host.Services.GetRequiredService<StorageProviders>().Invalidate(Host.Location.Id);

        var job = await Host.CollectAsync();

        Assert.NotEqual(JobState.Succeeded, job.State);
        Assert.Equal(StorageProviders.Unavailable, job.ErrorCode);
        Assert.Single(await Host.BlobsAsync());
        Assert.Equal(Content("on a share that went away"), await Host.ReadRawAsync(blob.Key));
    }
}

public sealed class SqliteBlobStoreTests : BlobStoreContractTests<SqliteFileDatabase>;

[Trait("Category", "Containers")]
public sealed class PostgresBlobStoreTests : BlobStoreContractTests<PostgresFileDatabase>;
