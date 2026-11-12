using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Atrium.Storage.Internal;

namespace Atrium.Storage.S3;

/// <param name="ServiceUrl">Endpoint of an S3-compatible service. Null means AWS S3 in <paramref name="Region"/>.</param>
/// <param name="ForcePathStyle">Address the bucket in the path, not the host name. Needed by MinIO and most self-hosted servers.</param>
/// <param name="KeyPrefix">Optional folder inside the bucket, so one bucket can hold other things too.</param>
public sealed record S3StorageOptions(
    string Bucket,
    string AccessKey,
    string SecretKey,
    string? ServiceUrl,
    string? Region,
    bool ForcePathStyle,
    string? KeyPrefix)
{
    // A record prints all its members. Keep the secret out of logs.
    public override string ToString() => $"S3StorageOptions {{ Bucket = {Bucket}, ServiceUrl = {ServiceUrl}, Region = {Region} }}";
}

/// <summary>
/// Stores objects in an S3-compatible bucket: AWS S3, Backblaze B2,
/// Cloudflare R2, MinIO.
/// </summary>
/// <remarks>
/// S3 has no rename. A write becomes visible only when it completes, which
/// gives the all-or-nothing rule; a move is a server-side copy, a size
/// check, and only then a delete of the source.
/// </remarks>
public sealed class S3StorageProvider : IStorageProvider, IDisposable
{
    private const long MinPartSize = 5L * 1024 * 1024;
    private const int WritePartSize = 8 * 1024 * 1024;

    /// <summary>Parts are held in memory while they are sent, so they are capped.</summary>
    private const long MaxPartSize = 64L * 1024 * 1024;

    private const long DefaultSingleCopyLimit = 5L * 1024 * 1024 * 1024;
    private const long DefaultCopyPartSize = 1024L * 1024 * 1024;

    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly long _singleCopyLimit;
    private readonly long _copyPartSize;

    public S3StorageProvider(S3StorageOptions options)
        : this(options, DefaultSingleCopyLimit, DefaultCopyPartSize)
    {
    }

    /// <summary>The copy limits are parameters so tests can exercise multipart copy without a 5 GB object.</summary>
    internal S3StorageProvider(S3StorageOptions options, long singleCopyLimit, long copyPartSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Bucket);
        _bucket = options.Bucket;
        _singleCopyLimit = singleCopyLimit;
        _copyPartSize = copyPartSize;

        var prefix = options.KeyPrefix?.Trim('/') ?? string.Empty;
        if (prefix.Length > 0)
        {
            StorageKey.Validate(prefix);
            prefix += "/";
        }

        _prefix = prefix;

        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,

            // Newer SDK versions add checksum headers that several
            // S3-compatible services reject. Each part is protected by
            // Content-MD5 instead, which every service verifies.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        var region = string.IsNullOrWhiteSpace(options.Region) ? "us-east-1" : options.Region;
        if (string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }
        else
        {
            config.ServiceURL = options.ServiceUrl;
            config.AuthenticationRegion = region;
        }

        _client = new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }

    public StorageCapabilities Capabilities { get; } =
        new(AtomicMove: false, ReportsFreeSpace: false, MinimumPartSize: MinPartSize, MaximumPartSize: MaxPartSize);

    private async Task<Stream> OpenReadCoreAsync(string key, ByteRange? range, CancellationToken ct)
    {
        StorageKey.Validate(key);
        var request = new GetObjectRequest { BucketName = _bucket, Key = Full(key) };
        if (range is { } r)
        {
            if (r.Offset < 0 || r.Length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(range), "The requested range is outside the object.");
            }

            if (r.Length == 0)
            {
                // S3 cannot express an empty range; make sure the object exists and answer directly.
                _ = await StatAsync(key, ct) ?? throw new StorageObjectNotFoundException(key);
                return Stream.Null;
            }

            request.ByteRange = r.Length is { } length
                ? new Amazon.S3.Model.ByteRange(r.Offset, r.Offset + length - 1)
                : new Amazon.S3.Model.ByteRange($"bytes={r.Offset}-");
        }

        try
        {
            var response = await _client.GetObjectAsync(request, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            throw new StorageObjectNotFoundException(key);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Reading from exactly the end is an empty read, as on a file.
            var stat = await StatAsync(key, ct) ?? throw new StorageObjectNotFoundException(key);
            if (range!.Value.Offset == stat.Size)
            {
                return Stream.Null;
            }

            throw new ArgumentOutOfRangeException(nameof(range), "The requested range is outside the object.");
        }
    }

    private async Task<StorageObject> WriteCoreAsync(string key, Stream content, bool overwrite, CancellationToken ct)
    {
        StorageKey.Validate(key);

        // Check-then-write is not atomic on S3. That is acceptable here:
        // managed objects are named by their content hash, so two writers of
        // the same key write the same bytes.
        if (!overwrite && await StatAsync(key, ct) is not null)
        {
            throw new StorageObjectExistsException(key);
        }

        var buffer = new byte[WritePartSize];
        var count = await FillAsync(content, buffer, ct);
        if (count < buffer.Length)
        {
            // Fits in one request.
            using var body = new MemoryStream(buffer, 0, count, writable: false);
            await _client.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = _bucket,
                    Key = Full(key),
                    InputStream = body,
                    AutoCloseStream = false,
                    MD5Digest = Md5(buffer.AsSpan(0, count)),
                },
                ct);
            return await RequireStatAsync(key, ct);
        }

        var upload = await BeginMultipartAsync(key, ct);
        try
        {
            var parts = new List<UploadedPart>();
            while (count > 0)
            {
                parts.Add(await SendPartAsync(upload, parts.Count + 1, buffer, count, ct));
                count = await FillAsync(content, buffer, ct);
            }

            // The existence check was already made above for the whole write.
            return await CompleteMultipartAsync(upload, parts, overwrite: true, ct);
        }
        catch
        {
            // Nothing became visible under the key; discard the parts.
            await TryAbortAsync(upload);
            throw;
        }
    }

    private async Task<MultipartUpload> BeginMultipartCoreAsync(string key, CancellationToken ct)
    {
        StorageKey.Validate(key);
        var response = await _client.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest { BucketName = _bucket, Key = Full(key) }, ct);
        return new MultipartUpload(key, response.UploadId);
    }

    private async Task<UploadedPart> UploadPartCoreAsync(
        MultipartUpload upload, int partNumber, Stream content, long length, CancellationToken ct)
    {
        StorageKey.Validate(upload.Key);
        ArgumentOutOfRangeException.ThrowIfLessThan(partNumber, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaxPartSize);

        // One byte more than announced, so a longer stream is detected too.
        var buffer = new byte[length + 1];
        var count = await FillAsync(content, buffer, ct);
        if (count != length)
        {
            throw new StorageException(
                $"Part {partNumber} was announced as {length} bytes but {(count > length ? "more" : count.ToString(System.Globalization.CultureInfo.InvariantCulture))} arrived.");
        }

        return await SendPartAsync(upload, partNumber, buffer, count, ct);
    }

    private async Task<StorageObject> CompleteMultipartCoreAsync(
        MultipartUpload upload, IReadOnlyList<UploadedPart> parts, bool overwrite, CancellationToken ct)
    {
        StorageKey.Validate(upload.Key);
        if (!overwrite && await StatAsync(upload.Key, ct) is not null)
        {
            throw new StorageObjectExistsException(upload.Key);
        }

        await _client.CompleteMultipartUploadAsync(
            new CompleteMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = Full(upload.Key),
                UploadId = upload.UploadId,
                PartETags = [.. parts.OrderBy(p => p.PartNumber).Select(p => new PartETag(p.PartNumber, p.ETag))],
            },
            ct);
        return await RequireStatAsync(upload.Key, ct);
    }

    private async Task AbortMultipartCoreAsync(MultipartUpload upload, CancellationToken ct)
    {
        StorageKey.Validate(upload.Key);
        try
        {
            await _client.AbortMultipartUploadAsync(
                new AbortMultipartUploadRequest { BucketName = _bucket, Key = Full(upload.Key), UploadId = upload.UploadId }, ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Already completed or aborted.
        }
    }

    public async IAsyncEnumerable<StorageObject> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        StorageKey.ValidatePrefix(prefix);
        string? continuation = null;
        do
        {
            var token = continuation;
            var response = await GuardAsync(() => _client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = _bucket, Prefix = Full(prefix), ContinuationToken = token }, ct));
            foreach (var item in response.S3Objects ?? [])
            {
                yield return new StorageObject(
                    item.Key[_prefix.Length..],
                    item.Size ?? 0,
                    ToUtc(item.LastModified));
            }

            continuation = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuation is not null);
    }

    private async Task<StorageObject?> StatCoreAsync(string key, CancellationToken ct)
    {
        StorageKey.Validate(key);
        try
        {
            var response = await _client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _bucket, Key = Full(key) }, ct);
            return new StorageObject(key, response.ContentLength, ToUtc(response.LastModified));
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task DeleteCoreAsync(string key, CancellationToken ct)
    {
        StorageKey.Validate(key);
        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = Full(key) }, ct);
    }

    private async Task MoveCoreAsync(string sourceKey, string destinationKey, bool overwrite, CancellationToken ct)
    {
        StorageKey.Validate(sourceKey);
        StorageKey.Validate(destinationKey);
        var source = await StatAsync(sourceKey, ct) ?? throw new StorageObjectNotFoundException(sourceKey);
        if (!overwrite && await StatAsync(destinationKey, ct) is not null)
        {
            throw new StorageObjectExistsException(destinationKey);
        }

        if (source.Size <= _singleCopyLimit)
        {
            await _client.CopyObjectAsync(
                new CopyObjectRequest
                {
                    SourceBucket = _bucket,
                    SourceKey = Full(sourceKey),
                    DestinationBucket = _bucket,
                    DestinationKey = Full(destinationKey),
                },
                ct);
        }
        else
        {
            await MultipartCopyAsync(sourceKey, destinationKey, source.Size, ct);
        }

        // The source is the only copy until the destination is proven to
        // exist with the same size. Only then is it deleted.
        var copied = await StatAsync(destinationKey, ct);
        if (copied is null || copied.Size != source.Size)
        {
            throw new StorageException(
                $"Copy of '{sourceKey}' could not be verified; the source was kept.");
        }

        await DeleteAsync(sourceKey, ct);
    }

    public async Task<StorageHealth> CheckAsync(CancellationToken ct)
    {
        try
        {
            // Ask about the bucket before writing to it. Some servers create
            // a missing bucket on the first write, which would turn a
            // mistyped name into a new, empty location instead of an error.
            await _client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = _bucket, Prefix = _prefix, MaxKeys = 1 }, ct);

            return await StorageProbe.RoundTripAsync(this, StorageProbe.NewKey(), ct)
                ? StorageHealth.Healthy(freeBytes: null)
                : StorageHealth.Failed(StorageProbe.MismatchCode, null);
        }
        catch (Exception e) when (e is AmazonServiceException or AmazonClientException or HttpRequestException or StorageException or IOException)
        {
            // The service's own error may be wrapped by the guard below.
            var cause = e is StorageUnavailableException { InnerException: { } inner } ? inner : e;
            return cause switch
            {
                AmazonS3Exception { StatusCode: HttpStatusCode.Forbidden } s3 => StorageHealth.Failed(StorageHealthCodes.AccessDenied, s3.ErrorCode),
                AmazonS3Exception s3 when s3.ErrorCode == "NoSuchBucket" || s3.StatusCode == HttpStatusCode.NotFound =>
                    StorageHealth.Failed(StorageHealthCodes.BucketNotFound, s3.ErrorCode),

                // The message of a client-side failure names the endpoint, never the credentials.
                _ => StorageHealth.Failed(StorageHealthCodes.Unavailable, cause.Message),
            };
        }
    }

    public void Dispose() => _client.Dispose();

    // The public operations. Each runs its implementation inside a guard
    // that turns the SDK's exceptions into StorageUnavailableException, so
    // the rest of the server never needs to know which SDK is underneath.
    public Task<Stream> OpenReadAsync(string key, ByteRange? range, CancellationToken ct) =>
        GuardAsync(() => OpenReadCoreAsync(key, range, ct));

    public Task<StorageObject> WriteAsync(string key, Stream content, bool overwrite, CancellationToken ct) =>
        GuardAsync(() => WriteCoreAsync(key, content, overwrite, ct));

    public Task<MultipartUpload> BeginMultipartAsync(string key, CancellationToken ct) =>
        GuardAsync(() => BeginMultipartCoreAsync(key, ct));

    public Task<UploadedPart> UploadPartAsync(
        MultipartUpload upload, int partNumber, Stream content, long length, CancellationToken ct) =>
        GuardAsync(() => UploadPartCoreAsync(upload, partNumber, content, length, ct));

    public Task<StorageObject> CompleteMultipartAsync(
        MultipartUpload upload, IReadOnlyList<UploadedPart> parts, bool overwrite, CancellationToken ct) =>
        GuardAsync(() => CompleteMultipartCoreAsync(upload, parts, overwrite, ct));

    public Task AbortMultipartAsync(MultipartUpload upload, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            await AbortMultipartCoreAsync(upload, ct);
            return true;
        });

    public Task<StorageObject?> StatAsync(string key, CancellationToken ct) =>
        GuardAsync(() => StatCoreAsync(key, ct));

    public Task DeleteAsync(string key, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            await DeleteCoreAsync(key, ct);
            return true;
        });

    public Task MoveAsync(string sourceKey, string destinationKey, bool overwrite, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            await MoveCoreAsync(sourceKey, destinationKey, overwrite, ct);
            return true;
        });

    /// <summary>Cleanup after a failure. Must not replace the failure that caused it.</summary>
    private async Task TryAbortAsync(MultipartUpload upload)
    {
        try
        {
            await AbortMultipartAsync(upload, CancellationToken.None);
        }
        catch (StorageException)
        {
            // The parts stay behind as an incomplete upload, which holds no
            // visible object. The original error is the one to report.
        }
    }

    private static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (AmazonServiceException e)
        {
            // The service answered with an error. Its code and status say
            // what went wrong; its message can echo request details.
            throw new StorageUnavailableException(
                $"The storage service refused the request: {e.ErrorCode} (HTTP {(int)e.StatusCode}).", e);
        }
        catch (Exception e) when (e is AmazonClientException or HttpRequestException)
        {
            throw new StorageUnavailableException("The storage service could not be reached.", e);
        }
    }

    private async Task MultipartCopyAsync(string sourceKey, string destinationKey, long size, CancellationToken ct)
    {
        var upload = await BeginMultipartAsync(destinationKey, ct);
        try
        {
            var parts = new List<UploadedPart>();
            for (long offset = 0; offset < size; offset += _copyPartSize)
            {
                var last = Math.Min(offset + _copyPartSize, size) - 1;
                var response = await _client.CopyPartAsync(
                    new CopyPartRequest
                    {
                        SourceBucket = _bucket,
                        SourceKey = Full(sourceKey),
                        DestinationBucket = _bucket,
                        DestinationKey = Full(destinationKey),
                        UploadId = upload.UploadId,
                        PartNumber = parts.Count + 1,
                        FirstByte = offset,
                        LastByte = last,
                    },
                    ct);
                parts.Add(new UploadedPart(parts.Count + 1, response.ETag, last - offset + 1));
            }

            await CompleteMultipartAsync(upload, parts, overwrite: true, ct);
        }
        catch
        {
            await TryAbortAsync(upload);
            throw;
        }
    }

    private async Task<UploadedPart> SendPartAsync(
        MultipartUpload upload, int partNumber, byte[] buffer, int count, CancellationToken ct)
    {
        using var body = new MemoryStream(buffer, 0, count, writable: false);
        var response = await _client.UploadPartAsync(
            new UploadPartRequest
            {
                BucketName = _bucket,
                Key = Full(upload.Key),
                UploadId = upload.UploadId,
                PartNumber = partNumber,
                PartSize = count,
                InputStream = body,
                MD5Digest = Md5(buffer.AsSpan(0, count)),
            },
            ct);
        return new UploadedPart(partNumber, response.ETag, count);
    }

    private async Task<StorageObject> RequireStatAsync(string key, CancellationToken ct) =>
        await StatAsync(key, ct) ?? throw new StorageException($"Object '{key}' is missing right after it was written.");

    private string Full(string key) => _prefix + key;

    /// <summary>Reads until the buffer is full or the stream ends. Returns the number of bytes read.</summary>
    private static async Task<int> FillAsync(Stream content, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await content.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    // MD5 is the transfer checksum the S3 protocol defines (Content-MD5).
    // It detects corruption in transit; it is not used for security.
#pragma warning disable CA5351
    private static string Md5(ReadOnlySpan<byte> data) => Convert.ToBase64String(MD5.HashData(data));
#pragma warning restore CA5351

    private static DateTimeOffset ToUtc(DateTime? value) =>
        value is { } v ? new DateTimeOffset(v.ToUniversalTime()) : DateTimeOffset.MinValue;
}
