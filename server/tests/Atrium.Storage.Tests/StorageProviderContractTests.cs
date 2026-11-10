using System.Security.Cryptography;

namespace Atrium.Storage.Tests;

/// <summary>
/// What every storage provider must do. The same tests run against the
/// local filesystem and against a real S3-compatible server, so a
/// difference between them shows up here and not with a user's files.
/// </summary>
public abstract class StorageProviderContractTests : IAsyncLifetime
{
    private const int MiB = 1024 * 1024;

    protected IStorageProvider Provider { get; private set; } = null!;

    protected abstract Task<IStorageProvider> CreateProviderAsync();

    protected virtual Task CleanUpAsync() => Task.CompletedTask;

    public async Task InitializeAsync() => Provider = await CreateProviderAsync();

    public Task DisposeAsync() => CleanUpAsync();

    protected static byte[] RandomBytes(int length) => RandomNumberGenerator.GetBytes(length);

    protected async Task PutAsync(string key, byte[] content, bool overwrite = false)
    {
        using var stream = new MemoryStream(content);
        await Provider.WriteAsync(key, stream, overwrite, CancellationToken.None);
    }

    protected async Task<byte[]> GetAsync(string key, ByteRange? range = null)
    {
        await using var stream = await Provider.OpenReadAsync(key, range, CancellationToken.None);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    protected async Task<List<string>> KeysAsync(string prefix = "")
    {
        var keys = new List<string>();
        await foreach (var item in Provider.ListAsync(prefix, CancellationToken.None))
        {
            keys.Add(item.Key);
        }

        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    [Fact]
    public async Task What_is_written_is_what_is_read()
    {
        var content = RandomBytes(MiB + 123);

        using var stream = new MemoryStream(content);
        var written = await Provider.WriteAsync("blobs/ab/cd/object", stream, overwrite: false, CancellationToken.None);

        Assert.Equal("blobs/ab/cd/object", written.Key);
        Assert.Equal(content.Length, written.Size);
        Assert.Equal(content, await GetAsync("blobs/ab/cd/object"));
        var stat = await Provider.StatAsync("blobs/ab/cd/object", CancellationToken.None);
        Assert.Equal(content.Length, stat!.Size);
        Assert.True(stat.LastModified > DateTimeOffset.UtcNow.AddMinutes(-10));
    }

    [Fact]
    public async Task An_empty_object_can_be_stored()
    {
        await PutAsync("empty", []);

        Assert.Empty(await GetAsync("empty"));
        Assert.Equal(0, (await Provider.StatAsync("empty", CancellationToken.None))!.Size);
    }

    [Fact]
    public async Task A_stream_larger_than_one_part_is_stored_whole()
    {
        // Larger than the S3 provider's internal part size, and not a
        // multiple of it, so the multipart path and its last part are used.
        var content = RandomBytes(17 * MiB + 7);

        await PutAsync("large", content);

        Assert.Equal(SHA256.HashData(content), SHA256.HashData(await GetAsync("large")));
    }

    [Fact]
    public async Task Names_with_unicode_and_spaces_round_trip()
    {
        var content = RandomBytes(1000);
        const string key = "фото/Летовање 2024/слика 1.jpg";

        await PutAsync(key, content);

        Assert.Equal(content, await GetAsync(key));
        Assert.Equal([key], await KeysAsync("фото/"));
    }

    [Fact]
    public async Task An_existing_object_is_never_replaced_unless_asked()
    {
        var original = RandomBytes(1000);
        await PutAsync("object", original);

        await Assert.ThrowsAsync<StorageObjectExistsException>(() => PutAsync("object", RandomBytes(2000)));

        Assert.Equal(original, await GetAsync("object"));

        var replacement = RandomBytes(500);
        await PutAsync("object", replacement, overwrite: true);
        Assert.Equal(replacement, await GetAsync("object"));
    }

    [Fact]
    public async Task A_write_that_fails_midway_leaves_nothing_behind()
    {
        await using var failing = new FailingStream(RandomBytes(10 * MiB), failAfter: 9 * MiB);

        await Assert.ThrowsAsync<IOException>(
            () => Provider.WriteAsync("broken", failing, overwrite: false, CancellationToken.None));

        Assert.Null(await Provider.StatAsync("broken", CancellationToken.None));
        Assert.Empty(await KeysAsync());
    }

    [Fact]
    public async Task A_failed_overwrite_keeps_the_previous_content()
    {
        var original = RandomBytes(3000);
        await PutAsync("object", original);
        await using var failing = new FailingStream(RandomBytes(10 * MiB), failAfter: 9 * MiB);

        await Assert.ThrowsAsync<IOException>(
            () => Provider.WriteAsync("object", failing, overwrite: true, CancellationToken.None));

        Assert.Equal(original, await GetAsync("object"));
    }

    [Fact]
    public async Task Ranges_return_exactly_the_requested_bytes()
    {
        var content = RandomBytes(10_000);
        await PutAsync("object", content);

        Assert.Equal(content[100..350], await GetAsync("object", new ByteRange(100, 250)));
        Assert.Equal(content[..1], await GetAsync("object", new ByteRange(0, 1)));
        Assert.Equal(content[9_000..], await GetAsync("object", new ByteRange(9_000, null)));
        Assert.Equal(content[9_999..], await GetAsync("object", new ByteRange(9_999, 1)));
        // Asking for more than is left gives what is left.
        Assert.Equal(content[9_990..], await GetAsync("object", new ByteRange(9_990, 500)));
        Assert.Empty(await GetAsync("object", new ByteRange(10_000, null)));
        Assert.Empty(await GetAsync("object", new ByteRange(5, 0)));
    }

    [Fact]
    public async Task Missing_objects_are_reported_not_invented()
    {
        await Assert.ThrowsAsync<StorageObjectNotFoundException>(() => GetAsync("missing/object"));
        await Assert.ThrowsAsync<StorageObjectNotFoundException>(() => GetAsync("missing/object", new ByteRange(0, 10)));
        Assert.Null(await Provider.StatAsync("missing/object", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_removes_the_object_and_is_safe_to_repeat()
    {
        await PutAsync("dir/object", RandomBytes(100));

        await Provider.DeleteAsync("dir/object", CancellationToken.None);
        await Provider.DeleteAsync("dir/object", CancellationToken.None);
        await Provider.DeleteAsync("never/existed", CancellationToken.None);

        Assert.Null(await Provider.StatAsync("dir/object", CancellationToken.None));
        Assert.Empty(await KeysAsync());
    }

    [Fact]
    public async Task Listing_matches_keys_by_prefix()
    {
        foreach (var key in new[] { "a/1", "a/sub/2", "ab/3", "b/4" })
        {
            await PutAsync(key, RandomBytes(10));
        }

        Assert.Equal(["a/1", "a/sub/2", "ab/3", "b/4"], await KeysAsync());
        Assert.Equal(["a/1", "a/sub/2"], await KeysAsync("a/"));
        Assert.Equal(["a/1", "a/sub/2", "ab/3"], await KeysAsync("a"));
        Assert.Equal(["a/sub/2"], await KeysAsync("a/sub/"));
        Assert.Equal(["a/sub/2"], await KeysAsync("a/s"));
        Assert.Empty(await KeysAsync("zzz/"));
    }

    [Fact]
    public async Task Listing_reports_sizes()
    {
        await PutAsync("sized", RandomBytes(4321));

        var items = new List<StorageObject>();
        await foreach (var item in Provider.ListAsync(string.Empty, CancellationToken.None))
        {
            items.Add(item);
        }

        Assert.Equal(4321, Assert.Single(items).Size);
    }

    [Fact]
    public async Task A_move_puts_the_content_at_the_destination_and_removes_the_source()
    {
        var content = RandomBytes(MiB);
        await PutAsync("staging/upload-1", content);

        await Provider.MoveAsync("staging/upload-1", "blobs/ab/final", overwrite: false, CancellationToken.None);

        Assert.Equal(content, await GetAsync("blobs/ab/final"));
        Assert.Null(await Provider.StatAsync("staging/upload-1", CancellationToken.None));
        Assert.Equal(["blobs/ab/final"], await KeysAsync());
    }

    [Fact]
    public async Task A_move_never_replaces_an_existing_object_unless_asked()
    {
        var source = RandomBytes(100);
        var existing = RandomBytes(200);
        await PutAsync("source", source);
        await PutAsync("destination", existing);

        await Assert.ThrowsAsync<StorageObjectExistsException>(
            () => Provider.MoveAsync("source", "destination", overwrite: false, CancellationToken.None));

        // Both are untouched: nothing was lost by the refused move.
        Assert.Equal(source, await GetAsync("source"));
        Assert.Equal(existing, await GetAsync("destination"));

        await Provider.MoveAsync("source", "destination", overwrite: true, CancellationToken.None);
        Assert.Equal(source, await GetAsync("destination"));
        Assert.Null(await Provider.StatAsync("source", CancellationToken.None));
    }

    [Fact]
    public async Task Moving_a_missing_object_is_an_error()
    {
        await Assert.ThrowsAsync<StorageObjectNotFoundException>(
            () => Provider.MoveAsync("missing", "destination", overwrite: false, CancellationToken.None));

        Assert.Null(await Provider.StatAsync("destination", CancellationToken.None));
    }

    private async Task<UploadedPart> PartAsync(MultipartUpload upload, int number, byte[] content)
    {
        using var stream = new MemoryStream(content);
        return await Provider.UploadPartAsync(upload, number, stream, content.Length, CancellationToken.None);
    }

    [Fact]
    public async Task A_multipart_upload_is_invisible_until_completed_and_then_whole()
    {
        var first = RandomBytes(5 * MiB);
        var second = RandomBytes(5 * MiB);
        var last = RandomBytes(MiB + 1);
        var upload = await Provider.BeginMultipartAsync("staging/big", CancellationToken.None);

        // Parts may arrive in any order.
        var p2 = await PartAsync(upload, 2, second);
        var p1 = await PartAsync(upload, 1, first);
        var p3 = await PartAsync(upload, 3, last);
        Assert.Null(await Provider.StatAsync("staging/big", CancellationToken.None));
        Assert.Empty(await KeysAsync());

        var done = await Provider.CompleteMultipartAsync(upload, [p3, p1, p2], overwrite: false, CancellationToken.None);

        Assert.Equal(first.Length + second.Length + last.Length, done.Size);
        Assert.Equal(
            SHA256.HashData([.. first, .. second, .. last]),
            SHA256.HashData(await GetAsync("staging/big")));
        Assert.Equal(["staging/big"], await KeysAsync());
    }

    [Fact]
    public async Task A_part_can_be_sent_again_and_the_later_one_counts()
    {
        var good = RandomBytes(5 * MiB);
        var last = RandomBytes(100);
        var upload = await Provider.BeginMultipartAsync("retried", CancellationToken.None);
        await PartAsync(upload, 1, RandomBytes(5 * MiB));

        var p1 = await PartAsync(upload, 1, good);
        var p2 = await PartAsync(upload, 2, last);
        await Provider.CompleteMultipartAsync(upload, [p1, p2], overwrite: false, CancellationToken.None);

        Assert.Equal(SHA256.HashData([.. good, .. last]), SHA256.HashData(await GetAsync("retried")));
    }

    [Fact]
    public async Task An_aborted_upload_leaves_nothing_and_abort_can_be_repeated()
    {
        var upload = await Provider.BeginMultipartAsync("aborted", CancellationToken.None);
        await PartAsync(upload, 1, RandomBytes(5 * MiB));

        await Provider.AbortMultipartAsync(upload, CancellationToken.None);
        await Provider.AbortMultipartAsync(upload, CancellationToken.None);

        Assert.Null(await Provider.StatAsync("aborted", CancellationToken.None));
        Assert.Empty(await KeysAsync());
    }

    [Fact]
    public async Task Completing_onto_an_existing_object_is_refused_unless_asked()
    {
        var existing = RandomBytes(100);
        await PutAsync("taken", existing);
        var upload = await Provider.BeginMultipartAsync("taken", CancellationToken.None);
        var part = await PartAsync(upload, 1, RandomBytes(200));

        await Assert.ThrowsAsync<StorageObjectExistsException>(
            () => Provider.CompleteMultipartAsync(upload, [part], overwrite: false, CancellationToken.None));

        Assert.Equal(existing, await GetAsync("taken"));
        await Provider.AbortMultipartAsync(upload, CancellationToken.None);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(101)]
    public async Task A_part_whose_size_differs_from_what_was_announced_is_rejected(int announced)
    {
        var upload = await Provider.BeginMultipartAsync("mismatch", CancellationToken.None);
        using var stream = new MemoryStream(RandomBytes(100));

        await Assert.ThrowsAsync<StorageException>(
            () => Provider.UploadPartAsync(upload, 1, stream, announced, CancellationToken.None));

        await Provider.AbortMultipartAsync(upload, CancellationToken.None);
    }

    public static TheoryData<string> UnsafeKeys() =>
    [
        "../outside",
        "a/../../outside",
        "/absolute",
        "a//b",
        "a\\b",
        "C:/drive",
        ".atrium-tmp/write-1",
        "nul",
        "trailing.",
        "bad\0byte",
        string.Empty,
    ];

    [Theory]
    [MemberData(nameof(UnsafeKeys))]
    public async Task Unsafe_keys_are_refused_by_every_operation(string key)
    {
        using var content = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<InvalidStorageKeyException>(() => Provider.WriteAsync(key, content, true, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidStorageKeyException>(() => Provider.OpenReadAsync(key, null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidStorageKeyException>(() => Provider.StatAsync(key, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidStorageKeyException>(() => Provider.DeleteAsync(key, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidStorageKeyException>(() => Provider.BeginMultipartAsync(key, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidStorageKeyException>(() => Provider.MoveAsync(key, "ok", true, CancellationToken.None));

        await PutAsync("real", [1]);
        await Assert.ThrowsAsync<InvalidStorageKeyException>(() => Provider.MoveAsync("real", key, true, CancellationToken.None));
        Assert.Equal([1], await GetAsync("real"));
    }

    [Theory]
    [InlineData("../")]
    [InlineData("/")]
    [InlineData(".atrium-tmp/")]
    public async Task Unsafe_prefixes_are_refused_when_listing(string prefix)
    {
        await Assert.ThrowsAsync<InvalidStorageKeyException>(async () =>
        {
            await foreach (var _ in Provider.ListAsync(prefix, CancellationToken.None))
            {
            }
        });
    }

    [Fact]
    public async Task The_health_check_passes_and_leaves_nothing_behind()
    {
        var health = await Provider.CheckAsync(CancellationToken.None);

        Assert.True(health.Ok, $"{health.ErrorCode}: {health.Detail}");
        Assert.Null(health.ErrorCode);
        Assert.Equal(Provider.Capabilities.ReportsFreeSpace, health.FreeBytes is not null);
        Assert.Empty(await KeysAsync());
    }

    /// <summary>A source that breaks partway, like a dropped connection.</summary>
    private sealed class FailingStream(byte[] data, int failAfter) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (Position >= failAfter)
            {
                throw new IOException("The connection was lost.");
            }

            return base.Read(buffer[..(int)Math.Min(buffer.Length, failAfter - Position)]);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
            CopyViaReadAsync(destination, bufferSize, cancellationToken);

        private async Task CopyViaReadAsync(Stream destination, int bufferSize, CancellationToken ct)
        {
            var buffer = new byte[bufferSize];
            int read;
            while ((read = Read(buffer)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
    }
}
