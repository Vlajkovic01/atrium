using Amazon.Runtime;
using Amazon.S3;
using Atrium.Storage.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Atrium.Storage.Tests;

/// <summary>One MinIO server shared by all S3 tests; each test gets its own bucket.</summary>
public sealed class MinioFixture : IAsyncLifetime
{
    public const string AccessKey = "atrium-test";
    public const string SecretKey = "atrium-test-secret";

    private const int Port = 9000;

    // An S3-compatible server used only here, never shipped. Pinned so the
    // tests do not change when the image does.
    private readonly IContainer _container = new ContainerBuilder("minio/minio:RELEASE.2025-04-22T22-12-26Z")
        .WithEnvironment("MINIO_ROOT_USER", AccessKey)
        .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
        .WithCommand("server", "/data")
        .WithPortBinding(Port, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(Port).ForPath("/minio/health/ready")))
        .Build();

    public string ServiceUrl => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(Port)}";

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<string> CreateBucketAsync()
    {
        var bucket = $"test-{Guid.NewGuid():N}";
        using var client = new AmazonS3Client(
            new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config { ServiceURL = ServiceUrl, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        await client.PutBucketAsync(bucket);
        return bucket;
    }

    public S3StorageOptions Options(string bucket, string? keyPrefix = null) =>
        new(bucket, AccessKey, SecretKey, ServiceUrl, Region: null, ForcePathStyle: true, keyPrefix);
}

/// <summary>
/// Runs the storage contract against a real S3-compatible server in a
/// container. Needs Docker, so it runs in CI; exclude it locally with
/// <c>dotnet test --filter "Category!=Containers"</c>.
/// </summary>
[Trait("Category", "Containers")]
public sealed class S3StorageProviderTests(MinioFixture minio) : StorageProviderContractTests, IClassFixture<MinioFixture>
{
    private const int MiB = 1024 * 1024;

    private readonly List<IDisposable> _providers = [];
    private string _bucket = string.Empty;

    protected override async Task<IStorageProvider> CreateProviderAsync()
    {
        _bucket = await minio.CreateBucketAsync();
        return Track(new S3StorageProvider(minio.Options(_bucket)));
    }

    protected override Task CleanUpAsync()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        return Task.CompletedTask;
    }

    private S3StorageProvider Track(S3StorageProvider provider)
    {
        _providers.Add(provider);
        return provider;
    }

    [Fact]
    public async Task A_large_object_is_moved_by_multipart_copy()
    {
        // The real limit is 5 GB. Lowering it exercises the same code path
        // with an object small enough for a test.
        var provider = Track(new S3StorageProvider(minio.Options(_bucket), singleCopyLimit: 5 * MiB, copyPartSize: 5 * MiB));
        var content = RandomBytes(11 * MiB + 3);
        await PutAsync("staging/large", content);

        await provider.MoveAsync("staging/large", "blobs/large", overwrite: false, CancellationToken.None);

        Assert.Equal(
            System.Security.Cryptography.SHA256.HashData(content),
            System.Security.Cryptography.SHA256.HashData(await GetAsync("blobs/large")));
        Assert.Equal(["blobs/large"], await KeysAsync());
    }

    [Fact]
    public async Task A_key_prefix_keeps_two_locations_in_one_bucket_apart()
    {
        var first = Track(new S3StorageProvider(minio.Options(_bucket, "atrium-one")));
        var second = Track(new S3StorageProvider(minio.Options(_bucket, "/atrium-two/")));
        using (var content = new MemoryStream([1, 2, 3]))
        {
            await first.WriteAsync("blobs/object", content, overwrite: false, CancellationToken.None);
        }

        Assert.NotNull(await first.StatAsync("blobs/object", CancellationToken.None));
        Assert.Null(await second.StatAsync("blobs/object", CancellationToken.None));
        var seenBySecond = new List<string>();
        await foreach (var item in second.ListAsync(string.Empty, CancellationToken.None))
        {
            seenBySecond.Add(item.Key);
        }

        Assert.Empty(seenBySecond);
        // Without a prefix the object is visible under its full path.
        Assert.Equal(["atrium-one/blobs/object"], await KeysAsync());
    }

    [Fact]
    public async Task The_health_check_explains_a_missing_bucket_and_wrong_credentials()
    {
        using var noBucket = new S3StorageProvider(minio.Options("bucket-that-does-not-exist"));
        using var badSecret = new S3StorageProvider(minio.Options(_bucket) with { SecretKey = "wrong-secret" });

        var missing = await noBucket.CheckAsync(CancellationToken.None);
        var denied = await badSecret.CheckAsync(CancellationToken.None);

        Assert.False(missing.Ok);
        Assert.Equal("storage.bucket_not_found", missing.ErrorCode);
        Assert.False(denied.Ok);
        Assert.Equal("storage.access_denied", denied.ErrorCode);
        Assert.DoesNotContain("wrong-secret", denied.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_health_check_fails_cleanly_when_the_server_is_unreachable()
    {
        using var provider = new S3StorageProvider(
            new S3StorageOptions("bucket", "key", "secret", "http://127.0.0.1:1", null, ForcePathStyle: true, null));

        var health = await provider.CheckAsync(CancellationToken.None);

        Assert.False(health.Ok);
        Assert.Equal("storage.unavailable", health.ErrorCode);
    }

    [Fact]
    public void Printing_the_options_does_not_reveal_the_secret_key()
    {
        var options = minio.Options(_bucket);

        Assert.DoesNotContain(MinioFixture.SecretKey, options.ToString(), StringComparison.Ordinal);
    }
}
