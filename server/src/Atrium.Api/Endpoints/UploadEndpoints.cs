using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Atrium.Api.Files;
using Atrium.Core.Files;
using Atrium.Files;
using Atrium.Files.Storage;
using Atrium.Files.Uploads;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.StaticFiles;

namespace Atrium.Api.Endpoints;

/// <param name="Offset">Bytes stored so far; resume from here.</param>
public sealed record UploadInfo(
    Guid Id,
    Guid ParentId,
    string Name,
    long Length,
    long Offset,
    long ChunkSize,
    UploadState State,
    Guid? NodeId,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <param name="Hashes">SHA-256 hashes as lowercase hex.</param>
public sealed record HashCheckRequest(IReadOnlyList<string> Hashes);

/// <param name="Owned">The hashes the caller already has in their own files.</param>
public sealed record HashCheckResponse(IReadOnlyList<string> Owned);

/// <summary>
/// The tus 1.0.0 protocol with the creation, termination, checksum and
/// expiration extensions (D-024). Chunks map one-to-one to storage parts
/// (D-028): the server tells the client its chunk size in
/// <c>Atrium-Chunk-Size</c>, and every chunk but the last must be at least
/// the storage's minimum part size.
/// </summary>
public static class UploadEndpoints
{
    public const string TusVersion = "1.0.0";
    public const string ChunkSizeHeader = "Atrium-Chunk-Size";
    public const string NodeIdHeader = "Atrium-Node-Id";
    public const string StateHeader = "Atrium-Upload-State";
    public const string UnsupportedVersion = "upload.unsupported_version";
    public const string InvalidMetadata = "upload.invalid_metadata";

    private const int ChecksumMismatchStatus = 460;
    private const int MaxHashesPerCheck = 1000;

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static IEndpointRouteBuilder MapUploadEndpoints(this IEndpointRouteBuilder routes)
    {
        var uploads = routes.MapGroup("/uploads").WithTags("Uploads");

        // Discovery carries no data, so it needs no sign-in, as tus clients expect.
        uploads.MapMethods("/", ["OPTIONS"], Options).AllowAnonymous().ExcludeFromDescription();

        uploads.MapPost("/", CreateAsync).WithName("CreateUpload")
            .WithDescription("tus creation. Headers: Tus-Resumable, Upload-Length, Upload-Metadata with parentId, name, and optionally modifiedAt (Unix milliseconds), sha256 (hex), onConflict (fail, rename, replace).")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status507InsufficientStorage);
        uploads.MapMethods("/{id:guid}", ["HEAD"], HeadAsync).WithName("GetUploadOffset");
        uploads.MapMethods("/{id:guid}", ["PATCH"], PatchAsync).WithName("AppendToUpload")
            .WithDescription("tus append. Body: application/offset+octet-stream; headers Upload-Offset, Content-Length and optionally Upload-Checksum (sha1 or sha256).")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone);
        uploads.MapDelete("/{id:guid}", DeleteAsync).WithName("CancelUpload")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone);

        uploads.MapGet("/", async (HttpContext http, UploadService service, CancellationToken ct) =>
                (await service.ListActiveAsync(http.User.Actor(), ct)).Select(ToInfo).ToList())
            .WithName("ListActiveUploads");
        uploads.MapGet("/{id:guid}", async Task<Results<Ok<UploadInfo>, ProblemHttpResult>> (
                Guid id, HttpContext http, UploadService service, CancellationToken ct) =>
                await service.FindAsync(http.User.Actor(), id, ct) is { } upload
                    ? TypedResults.Ok(ToInfo(upload))
                    : FileHttp.Problem(FileErrors.NotFound))
            .WithName("GetUpload");

        uploads.MapPost("/check", CheckAsync).WithName("CheckOwnedHashes").ProducesProblem(StatusCodes.Status400BadRequest);

        return routes;
    }

    private static IResult Options(HttpContext http)
    {
        var headers = http.Response.Headers;
        headers["Tus-Resumable"] = TusVersion;
        headers["Tus-Version"] = TusVersion;
        headers["Tus-Extension"] = "creation,termination,checksum,expiration";
        headers["Tus-Max-Size"] = UploadService.MaxLength.ToString(CultureInfo.InvariantCulture);
        headers["Tus-Checksum-Algorithm"] = "sha1,sha256";
        return Results.NoContent();
    }

    private static async Task<IResult> CreateAsync(HttpContext http, UploadService service, CancellationToken ct)
    {
        if (RefuseVersion(http) is { } refused)
        {
            return refused;
        }

        if (http.Request.Headers.ContainsKey("Upload-Defer-Length")
            || !long.TryParse(http.Request.Headers["Upload-Length"], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            return ApiProblem.Invalid();
        }

        if (!TryParseMetadata(http.Request.Headers["Upload-Metadata"], out var metadata)
            || !metadata.TryGetValue("parentId", out var parentText) || !Guid.TryParse(parentText, out var parentId)
            || !metadata.TryGetValue("name", out var name)
            || !TryReadPolicy(metadata, out var policy)
            || !TryReadModified(metadata, out var modifiedAt)
            || !TryReadSha256(metadata, out var sha256))
        {
            return ApiProblem.Create(StatusCodes.Status400BadRequest, InvalidMetadata);
        }

        var result = await service.CreateAsync(
            http.User.Actor(),
            new NewUpload(parentId, name, length, MediaTypeOf(name), modifiedAt, sha256, policy),
            ct);
        if (!result.Ok)
        {
            return Problem(result.Error!, result.Params);
        }

        var upload = result.Value!;
        WriteState(http, upload);
        http.Response.Headers.Location = $"/api/v1/uploads/{upload.Id}";
        return Results.StatusCode(StatusCodes.Status201Created);
    }

    private static async Task<IResult> HeadAsync(Guid id, HttpContext http, UploadService service, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (RefuseVersion(http) is { } refused)
        {
            return refused;
        }

        var upload = await service.FindAsync(http.User.Actor(), id, ct);
        if (upload is null)
        {
            return Results.NotFound();
        }

        WriteState(http, upload);
        return upload.State is UploadState.Active or UploadState.Completed ? Results.Ok() : Results.StatusCode(StatusCodes.Status410Gone);
    }

    private static async Task<IResult> PatchAsync(Guid id, HttpContext http, UploadService service, CancellationToken ct)
    {
        if (RefuseVersion(http) is { } refused)
        {
            return refused;
        }

        var request = http.Request;
        if (!string.Equals(request.ContentType, "application/offset+octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        if (!long.TryParse(request.Headers["Upload-Offset"], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            return ApiProblem.Invalid();
        }

        if (request.ContentLength is not { } length)
        {
            return Results.StatusCode(StatusCodes.Status411LengthRequired);
        }

        HashAlgorithmName? algorithm = null;
        byte[]? checksum = null;
        if (request.Headers.TryGetValue("Upload-Checksum", out var checksumHeader))
        {
            if (!TryParseChecksum(checksumHeader.ToString(), out var parsedAlgorithm, out checksum))
            {
                return ApiProblem.Invalid();
            }

            algorithm = parsedAlgorithm;
        }

        var actor = http.User.Actor();
        var upload = await service.FindAsync(actor, id, ct);
        if (upload is null)
        {
            return Results.NotFound();
        }

        // Large enough for this upload's chunks, and no larger.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = upload.ChunkSize;
        }

        var result = await service.AppendAsync(actor, id, new UploadChunk(offset, length, request.Body, algorithm, checksum), ct);
        if (!result.Ok)
        {
            if (await service.FindAsync(actor, id, ct) is { } current)
            {
                WriteState(http, current);
            }

            return Problem(result.Error!, result.Params);
        }

        WriteState(http, result.Value!);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, UploadService service, CancellationToken ct)
    {
        if (RefuseVersion(http) is { } refused)
        {
            return refused;
        }

        var result = await service.CancelAsync(http.User.Actor(), id, ct);
        return result.Ok ? Results.NoContent() : Problem(result.Error!, result.Params);
    }

    private static async Task<Results<Ok<HashCheckResponse>, ProblemHttpResult>> CheckAsync(
        HashCheckRequest request, HttpContext http, UploadService service, CancellationToken ct)
    {
        if (request.Hashes is not { Count: <= MaxHashesPerCheck })
        {
            return ApiProblem.Invalid();
        }

        var hashes = new List<byte[]>(request.Hashes.Count);
        foreach (var hex in request.Hashes)
        {
            if (!TryParseHex(hex, out var hash))
            {
                return ApiProblem.Invalid();
            }

            hashes.Add(hash);
        }

        var owned = await service.OwnedHashesAsync(http.User.Actor(), hashes, ct);
        return TypedResults.Ok(new HashCheckResponse(owned.Select(Convert.ToHexStringLower).Distinct().ToList()));
    }

    private static IResult? RefuseVersion(HttpContext http)
    {
        http.Response.Headers["Tus-Resumable"] = TusVersion;
        if (http.Request.Headers["Tus-Resumable"] == TusVersion)
        {
            return null;
        }

        http.Response.Headers["Tus-Version"] = TusVersion;
        return ApiProblem.Create(StatusCodes.Status412PreconditionFailed, UnsupportedVersion);
    }

    private static void WriteState(HttpContext http, Upload upload)
    {
        var headers = http.Response.Headers;
        headers["Upload-Offset"] = upload.Offset.ToString(CultureInfo.InvariantCulture);
        headers["Upload-Length"] = upload.Length.ToString(CultureInfo.InvariantCulture);
        headers[ChunkSizeHeader] = upload.ChunkSize.ToString(CultureInfo.InvariantCulture);
        headers[StateHeader] = upload.State.ToString();
        if (upload.State == UploadState.Active)
        {
            headers["Upload-Expires"] = upload.ExpiresAt.ToString("R", CultureInfo.InvariantCulture);
        }

        if (upload.NodeId is { } nodeId)
        {
            headers[NodeIdHeader] = nodeId.ToString();
        }
    }

    private static IResult Problem(string code, IReadOnlyDictionary<string, object?>? parameters) => code switch
    {
        UploadErrors.ChecksumMismatch => ApiProblem.Create(ChecksumMismatchStatus, code),
        UploadErrors.OffsetMismatch or UploadErrors.Busy => ApiProblem.Create(StatusCodes.Status409Conflict, code, Copy(parameters)),
        UploadErrors.ContentMismatch => ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, code),
        UploadErrors.ChunkSize => ApiProblem.Create(StatusCodes.Status400BadRequest, code, Copy(parameters)),
        UploadErrors.TooLarge => ApiProblem.Create(StatusCodes.Status413PayloadTooLarge, code, Copy(parameters)),
        UploadErrors.NotActive or UploadErrors.Expired => ApiProblem.Create(StatusCodes.Status410Gone, code),
        StorageProviders.Unavailable or StorageProviders.IdentityMissing or StorageProviders.IdentityMismatch =>
            ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, code),
        _ => FileHttp.Problem(code, parameters),
    };

    private static Dictionary<string, object?>? Copy(IReadOnlyDictionary<string, object?>? parameters) =>
        parameters?.ToDictionary(p => p.Key, p => p.Value);

    private static UploadInfo ToInfo(Upload u) =>
        new(u.Id, u.ParentId, u.Name, u.Length, u.Offset, u.ChunkSize, u.State, u.NodeId, u.ErrorCode, u.CreatedAt, u.ExpiresAt);

    private static string? MediaTypeOf(string name) =>
        ContentTypes.TryGetContentType(name, out var type) ? type : "application/octet-stream";

    /// <summary>tus metadata: comma-separated pairs of a key and a Base64 value.</summary>
    internal static bool TryParseMetadata(string? header, out Dictionary<string, string> metadata)
    {
        metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        foreach (var pair in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts[0].Length == 0 || metadata.ContainsKey(parts[0]))
            {
                return false;
            }

            try
            {
                metadata[parts[0]] = parts.Length == 2 ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])) : string.Empty;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadPolicy(Dictionary<string, string> metadata, out ConflictPolicy policy)
    {
        policy = ConflictPolicy.Fail;
        return !metadata.TryGetValue("onConflict", out var value)
            || (Enum.TryParse(value, ignoreCase: true, out policy) && Enum.IsDefined(policy));
    }

    private static bool TryReadModified(Dictionary<string, string> metadata, out DateTimeOffset? modifiedAt)
    {
        modifiedAt = null;
        if (!metadata.TryGetValue("modifiedAt", out var value))
        {
            return true;
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) || ms > 253402300799999)
        {
            return false;
        }

        modifiedAt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return true;
    }

    private static bool TryReadSha256(Dictionary<string, string> metadata, out byte[]? sha256)
    {
        sha256 = null;
        if (!metadata.TryGetValue("sha256", out var value))
        {
            return true;
        }

        if (!TryParseHex(value, out var parsed))
        {
            return false;
        }

        sha256 = parsed;
        return true;
    }

    private static bool TryParseHex(string? hex, out byte[] hash)
    {
        hash = [];
        if (hex is not { Length: 64 })
        {
            return false;
        }

        try
        {
            hash = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryParseChecksum(string header, out HashAlgorithmName algorithm, out byte[] value)
    {
        algorithm = default;
        value = [];
        var parts = header.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        algorithm = parts[0].ToLowerInvariant() switch
        {
            "sha1" => HashAlgorithmName.SHA1,
            "sha256" => HashAlgorithmName.SHA256,
            _ => default,
        };
        if (algorithm == default)
        {
            return false;
        }

        try
        {
            value = Convert.FromBase64String(parts[1]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
