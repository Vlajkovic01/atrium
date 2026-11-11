using Atrium.Storage.Local;

namespace Atrium.Storage.Tests;

public sealed class LocalStorageProviderTests : StorageProviderContractTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"), "storage");

    private string Outside => Path.GetDirectoryName(_root)!;

    protected override Task<IStorageProvider> CreateProviderAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.FromResult<IStorageProvider>(new LocalStorageProvider(_root));
    }

    protected override Task CleanUpAsync()
    {
        Directory.Delete(Outside, recursive: true);
        return Task.CompletedTask;
    }

    private string[] TempFiles()
    {
        var temp = Path.Combine(_root, StorageKey.ReservedPrefix);
        return Directory.Exists(temp) ? Directory.GetFileSystemEntries(temp) : [];
    }

    [Fact]
    public async Task Objects_are_plain_files_under_the_root()
    {
        var content = RandomBytes(500);

        await PutAsync("blobs/ab/object", content);

        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_root, "blobs", "ab", "object")));
    }

    [Fact]
    public async Task Nothing_is_ever_created_outside_the_root()
    {
        await File.WriteAllTextAsync(Path.Combine(Outside, "neighbour.txt"), "keep me");

        foreach (var key in new[] { "../neighbour.txt", "..\\neighbour.txt", "a/../../neighbour.txt" })
        {
            await Assert.ThrowsAsync<InvalidStorageKeyException>(() => PutAsync(key, [1, 2, 3], overwrite: true));
            await Assert.ThrowsAsync<InvalidStorageKeyException>(() => Provider.DeleteAsync(key, CancellationToken.None));
        }

        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(Outside, "neighbour.txt")));
        Assert.Equal(["neighbour.txt", "storage"], Directory.GetFileSystemEntries(Outside).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Temporary_files_are_removed_after_success_and_after_failure()
    {
        await PutAsync("ok", RandomBytes(1000));
        await Assert.ThrowsAsync<StorageObjectExistsException>(() => PutAsync("ok", RandomBytes(1000)));
        var upload = await Provider.BeginMultipartAsync("multi", CancellationToken.None);
        using (var part = new MemoryStream(RandomBytes(100)))
        {
            var uploaded = await Provider.UploadPartAsync(upload, 1, part, 100, CancellationToken.None);
            await Provider.CompleteMultipartAsync(upload, [uploaded], overwrite: false, CancellationToken.None);
        }

        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task Temporary_files_never_appear_in_listings()
    {
        var upload = await Provider.BeginMultipartAsync("pending", CancellationToken.None);
        using var part = new MemoryStream(RandomBytes(100));
        await Provider.UploadPartAsync(upload, 1, part, 100, CancellationToken.None);

        Assert.NotEmpty(TempFiles());
        Assert.Empty(await KeysAsync());
    }

    [Fact]
    public async Task A_forged_upload_id_cannot_reach_another_directory()
    {
        await PutAsync("victim/file", RandomBytes(10));
        var forged = new MultipartUpload("x", "../../victim");

        await Assert.ThrowsAsync<StorageException>(() => Provider.AbortMultipartAsync(forged, CancellationToken.None));
        using var part = new MemoryStream([1]);
        await Assert.ThrowsAsync<StorageException>(() => Provider.UploadPartAsync(forged, 1, part, 1, CancellationToken.None));

        Assert.Equal(["victim/file"], await KeysAsync());
    }

    [Fact]
    public async Task Completing_with_a_missing_part_fails_and_creates_no_object()
    {
        var upload = await Provider.BeginMultipartAsync("incomplete", CancellationToken.None);
        using var part = new MemoryStream(RandomBytes(100));
        var uploaded = await Provider.UploadPartAsync(upload, 1, part, 100, CancellationToken.None);
        var neverSent = new UploadedPart(2, "none", 100);

        await Assert.ThrowsAsync<StorageException>(
            () => Provider.CompleteMultipartAsync(upload, [uploaded, neverSent], overwrite: false, CancellationToken.None));

        Assert.Null(await Provider.StatAsync("incomplete", CancellationToken.None));
    }

    [Fact]
    public async Task The_health_check_reports_free_space()
    {
        var health = await Provider.CheckAsync(CancellationToken.None);

        Assert.True(health.FreeBytes > 0);
    }

    [Fact]
    public async Task The_health_check_fails_cleanly_when_the_root_cannot_hold_files()
    {
        // A file where the root directory should be.
        var blocked = Path.Combine(Outside, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "x");
        var provider = new LocalStorageProvider(blocked);

        var health = await provider.CheckAsync(CancellationToken.None);

        Assert.False(health.Ok);
        Assert.NotNull(health.ErrorCode);
        Assert.Equal("x", await File.ReadAllTextAsync(blocked));
    }

    [Fact]
    public async Task A_missing_root_is_an_error_and_is_never_created()
    {
        // What an unmounted network share looks like: the directory is gone.
        var unmounted = Path.Combine(Outside, "unmounted-share");
        var provider = new LocalStorageProvider(unmounted);
        using var content = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<StorageUnavailableException>(() => provider.WriteAsync("blobs/x", content, false, CancellationToken.None));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => provider.OpenReadAsync("blobs/x", null, CancellationToken.None));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => provider.StatAsync("blobs/x", CancellationToken.None));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => provider.DeleteAsync("blobs/x", CancellationToken.None));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => provider.BeginMultipartAsync("blobs/x", CancellationToken.None));
        await Assert.ThrowsAsync<StorageUnavailableException>(() => provider.MoveAsync("a", "b", false, CancellationToken.None));
        await Assert.ThrowsAsync<StorageUnavailableException>(async () =>
        {
            await foreach (var _ in provider.ListAsync(string.Empty, CancellationToken.None))
            {
            }
        });

        var health = await provider.CheckAsync(CancellationToken.None);
        Assert.False(health.Ok);
        Assert.Equal("storage.root_missing", health.ErrorCode);
        Assert.False(Directory.Exists(unmounted));
    }

    [Fact]
    public async Task Unsafe_keys_are_refused_before_the_root_is_even_looked_at()
    {
        var provider = new LocalStorageProvider(Path.Combine(Outside, "unmounted-share"));

        await Assert.ThrowsAsync<InvalidStorageKeyException>(() => provider.StatAsync("../x", CancellationToken.None));
    }

    [Fact]
    public async Task A_key_that_is_the_folder_of_other_objects_is_not_an_object()
    {
        var content = RandomBytes(100);
        await PutAsync("a/b", content);

        await Assert.ThrowsAsync<StorageObjectNotFoundException>(() => GetAsync("a"));
        Assert.Null(await Provider.StatAsync("a", CancellationToken.None));
        await Provider.DeleteAsync("a", CancellationToken.None);
        await Assert.ThrowsAsync<StorageException>(() => PutAsync("a", RandomBytes(10), overwrite: true));
        await Assert.ThrowsAsync<StorageException>(() => Provider.BeginMultipartAsync("a", CancellationToken.None));
        await PutAsync("other", RandomBytes(10));
        await Assert.ThrowsAsync<StorageException>(() => Provider.MoveAsync("other", "a", overwrite: true, CancellationToken.None));

        // Nothing under the folder was harmed by any of it.
        Assert.Equal(content, await GetAsync("a/b"));
        Assert.Equal(["a/b", "other"], await KeysAsync());
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task A_key_below_an_existing_object_is_refused()
    {
        var content = RandomBytes(100);
        await PutAsync("a", content);

        await Assert.ThrowsAsync<StorageException>(() => PutAsync("a/b", RandomBytes(10)));

        Assert.Equal(content, await GetAsync("a"));
        Assert.Equal(["a"], await KeysAsync());
        Assert.Empty(TempFiles());
    }

    [Fact]
    public void The_root_must_be_an_absolute_path()
    {
        Assert.Throws<ArgumentException>(() => new LocalStorageProvider("relative/path"));
    }
}
