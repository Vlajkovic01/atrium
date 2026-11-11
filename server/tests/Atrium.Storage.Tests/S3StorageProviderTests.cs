using Amazon.Runtime;
using Amazon.S3;
using Atrium.Storage.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Atrium.Storage.Tests;

/// <summary>
/// One S3-compatible server shared by all S3 tests; each test gets its own
/// bucket. The server is SeaweedFS (Apache-2.0): MinIO, the usual choice,
/// withdrew its container image. It is used only here and never shipped.
/// </summary>
public sealed class S3ServerFixture : IAsyncLifetime
{
    public const string AccessKey = "atrium-test";
    public const string SecretKey = "atrium-test-secret";

    public const string Bucket = "atrium-tests";

    private const int Port = 8333;
    private const string ConfigPath = "/etc/seaweedfs/s3.json";

    // With an identity configured the server checks request signatures, so
    // wrong credentials are really refused.
    private const string Config =
        """
        {"identities":[{"name":"atrium","credentials":[{"accessKey":"atrium-test","secretKey":"atrium-test-secret"}],"actions":["Admin","Read","Write","List","Tagging"]}]}
        """;

    // Pinned so the tests do not change when the image does.
    private readonly IContainer _container = new ContainerBuilder("chrislusf/seaweedfs:4.53")
        .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(Config), ConfigPath)
        .WithCommand("server", "-s3", $"-s3.config={ConfigPath}")
        .WithPortBinding(Port, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(Port))
        .Build();

    public string ServiceUrl => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(Port)}";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // The port opens before the server's parts have found each other and
        // before it can store anything. The provider's own health check is
        // the probe, so "ready" means ready for exactly the requests the
        // tests send. If it never passes, fail with the server's own log.
        using var client = NewClient();
        using var provider = new S3StorageProvider(Options(keyPrefix: "readiness"));
        var deadline = DateTime.UtcNow.AddSeconds(90);
        var last = "the bucket could not be created";
        var bucketCreated = false;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (!bucketCreated)
                {
                    await client.PutBucketAsync(Bucket);
                    bucketCreated = true;
                }

                var health = await provider.CheckAsync(CancellationToken.None);
                if (health.Ok)
                {
                    return;
                }

                last = $"{health.ErrorCode}: {health.Detail}";
            }
            catch (Exception e) when (e is AmazonServiceException or AmazonClientException or HttpRequestException)
            {
                last = e.Message;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        var (stdout, stderr) = await _container.GetLogsAsync();
        throw new InvalidOperationException(
            $"The S3 test server never passed the provider's health check. Last result: {last}\n" +
            $"--- server stdout (tail) ---\n{Tail(stdout)}\n--- server stderr (tail) ---\n{Tail(stderr)}");
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();


    private AmazonS3Client NewClient() => new(
        new BasicAWSCredentials(AccessKey, SecretKey),
        new AmazonS3Config
        {
            ServiceURL = ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    private static string Tail(string text) => text.Length <= 4000 ? text : text[^4000..];

    /// <summary>
    /// All tests share one bucket and are kept apart by key prefix. This
    /// server reserves storage volumes per bucket, so a bucket per test
    /// runs it out of volumes after a few tests.
    /// </summary>
    public S3StorageOptions Options(string? keyPrefix, string bucket = Bucket) =>
        new(bucket, AccessKey, SecretKey, ServiceUrl, Region: null, ForcePathStyle: true, keyPrefix);
}

/// <summary>
/// Runs the storage contract against a real S3-compatible server in a
/// container. Needs Docker, so it runs in CI; exclude it locally with
/// <c>dotnet test --filter "Category!=Containers"</c>.
/// </summary>
[Trait("Category", "Containers")]
public sealed class S3StorageProviderTests(S3ServerFixture server) : StorageProviderContractTests, IClassFixture<S3ServerFixture>
{
    private const int MiB = 1024 * 1024;

    private readonly List<IDisposable> _providers = [];

    // A fresh prefix per test: each test sees an empty location.
    private readonly string _prefix = $"test-{Guid.NewGuid():N}";

    protected override Task<IStorageProvider> CreateProviderAsync() =>
        Task.FromResult<IStorageProvider>(Track(new S3StorageProvider(server.Options(_prefix))));

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
        var provider = Track(new S3StorageProvider(server.Options(_prefix), singleCopyLimit: 5 * MiB, copyPartSize: 5 * MiB));
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
        var first = Track(new S3StorageProvider(server.Options($"{_prefix}/atrium-one")));
        var second = Track(new S3StorageProvider(server.Options($"/{_prefix}/atrium-two/")));
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
        // From the enclosing location the object is visible under its full path.
        Assert.Equal(["atrium-one/blobs/object"], await KeysAsync());
    }

    [Fact]
    public async Task The_health_check_explains_a_missing_bucket_and_wrong_credentials()
    {
        using var noBucket = new S3StorageProvider(server.Options(_prefix, bucket: "bucket-that-does-not-exist"));
        using var badSecret = new S3StorageProvider(server.Options(_prefix) with { SecretKey = "wrong-secret" });

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
    public async Task Service_failures_surface_as_storage_errors_without_the_secret()
    {
        using var badSecret = new S3StorageProvider(server.Options(_prefix) with { SecretKey = "wrong-secret" });
        using var content = new MemoryStream([1, 2, 3]);

        var onRead = await Assert.ThrowsAsync<StorageUnavailableException>(() => badSecret.StatAsync("object", CancellationToken.None));
        var onWrite = await Assert.ThrowsAsync<StorageUnavailableException>(
            () => badSecret.WriteAsync("object", content, overwrite: true, CancellationToken.None));

        Assert.DoesNotContain("wrong-secret", onRead.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-secret", onWrite.ToString(), StringComparison.Ordinal);
        Assert.Null(await Provider.StatAsync("object", CancellationToken.None));
    }

    [Fact]
    public async Task An_unreachable_server_surfaces_as_a_storage_error()
    {
        using var provider = new S3StorageProvider(
            new S3StorageOptions("bucket", "key", "secret", "http://127.0.0.1:1", null, ForcePathStyle: true, null));

        await Assert.ThrowsAsync<StorageUnavailableException>(() => provider.StatAsync("object", CancellationToken.None));
    }

    [Fact]
    public void Printing_the_options_does_not_reveal_the_secret_key()
    {
        var options = server.Options(_prefix);

        Assert.DoesNotContain(S3ServerFixture.SecretKey, options.ToString(), StringComparison.Ordinal);
    }
}
