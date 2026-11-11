using Atrium.Api.Auth;
using Atrium.Api.Storage;
using Atrium.Core.Storage;
using Atrium.Data;
using Atrium.Storage.S3;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Endpoints;

/// <param name="CreateDirectory">Create the directory if it does not exist. Only honored when saving, never when testing.</param>
public sealed record LocalStorageRequest(string Path, bool CreateDirectory);

public sealed record S3StorageRequest(
    string Bucket,
    string AccessKey,
    string SecretKey,
    string? ServiceUrl,
    string? Region,
    bool ForcePathStyle,
    string? KeyPrefix);

public sealed record StorageLocationRequest(string? Name, StorageProviderKind Kind, LocalStorageRequest? Local, S3StorageRequest? S3);

/// <param name="HoldsAtriumData">The location was already used by an Atrium server and cannot be registered as new.</param>
public sealed record StorageTestResult(bool Ok, string? ErrorCode, string? Detail, long? FreeBytes, bool HoldsAtriumData);

/// <param name="Target">Where the location points, for display. Credentials are never returned.</param>
public sealed record StorageLocationView(
    Guid Id,
    string Name,
    StorageProviderKind Kind,
    StorageLocationState State,
    DateTimeOffset? LastCheckedAt,
    string Target,
    int Version);

public static class StorageLocationEndpoints
{
    public static IEndpointRouteBuilder MapStorageLocationEndpoints(this IEndpointRouteBuilder routes)
    {
        var locations = routes.MapGroup("/storage-locations")
            .WithTags("Storage")
            .RequireAuthorization(AuthSetup.AdminPolicy, AuthSetup.FullSessionPolicy);

        locations.MapGet("/", async (AtriumDbContext db, StorageLocationService service, CancellationToken ct) =>
            {
                var all = await db.StorageLocations.AsNoTracking().OrderBy(l => l.Name).ToListAsync(ct);
                return all.Select(l => ToView(l, service));
            })
            .WithName("ListStorageLocations");

        // Tries a location and saves nothing. The answer is always 200 with
        // the outcome inside: a failed test is a result, not a server error.
        locations.MapPost("/test", async Task<Results<Ok<StorageTestResult>, ProblemHttpResult>> (
                StorageLocationRequest request, StorageLocationService service, CancellationToken ct) =>
                TypedResults.Ok(ToResult(await service.TestAsync(request.Kind, ToConfig(request), ct))))
            .WithName("TestStorageLocation");

        locations.MapPost("/", CreateAsync)
            .WithName("CreateStorageLocation")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        locations.MapPost("/{id:guid}/check", async Task<Results<Ok<StorageTestResult>, ProblemHttpResult>> (
                Guid id, StorageLocationService service, CancellationToken ct) =>
                await service.CheckAsync(id, ct) is { } outcome
                    ? TypedResults.Ok(ToResult(outcome))
                    : ApiProblem.Missing())
            .WithName("CheckStorageLocation")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    private static async Task<Results<Created<StorageLocationView>, ProblemHttpResult>> CreateAsync(
        StorageLocationRequest request, StorageLocationService service, CancellationToken ct)
    {
        var outcome = await service.CreateAsync(
            request.Name ?? string.Empty,
            request.Kind,
            ToConfig(request),
            createDirectory: request.Local?.CreateDirectory ?? false,
            ct);
        if (outcome.Location is { } location)
        {
            return TypedResults.Created($"/api/v1/storage-locations/{location.Id}", ToView(location, service));
        }

        var code = outcome.Test.ErrorCode ?? StorageErrors.Unavailable;
        var status = code is StorageErrors.LocationInUse or StorageErrors.NameTaken
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status400BadRequest;
        return ApiProblem.Create(status, code, new Dictionary<string, object?> { ["detail"] = outcome.Test.Detail });
    }

    private static StorageLocationConfig ToConfig(StorageLocationRequest request) => new(
        request.Local is { } local ? new LocalStorageSettings(local.Path) : null,
        request.S3 is { } s3
            ? new S3StorageOptions(s3.Bucket, s3.AccessKey, s3.SecretKey, Blank(s3.ServiceUrl), Blank(s3.Region), s3.ForcePathStyle, Blank(s3.KeyPrefix))
            : null);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static StorageTestResult ToResult(StorageTestOutcome outcome) =>
        new(outcome.Ok, outcome.ErrorCode, outcome.Detail, outcome.FreeBytes, outcome.HoldsAtriumData);

    private static StorageLocationView ToView(StorageLocation location, StorageLocationService service) => new(
        location.Id,
        location.Name,
        location.Kind,
        location.State,
        location.LastCheckedAt,
        service.DescribeTarget(location),
        location.Version);
}
