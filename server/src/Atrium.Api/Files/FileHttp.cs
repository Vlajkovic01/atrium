using System.Globalization;
using System.Security.Claims;
using Atrium.Api.Auth;
using Atrium.Api.Endpoints;
using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Files;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Atrium.Api.Files;

/// <param name="Version">Send it back in <c>If-Match</c> when changing the node.</param>
/// <param name="CanWrite">Whether the caller may change it; clients hide write actions otherwise.</param>
public sealed record NodeInfo(
    Guid Id,
    Guid LibraryId,
    Guid? ParentId,
    string Name,
    NodeKind Kind,
    long Size,
    string? MediaType,
    DateTimeOffset ModifiedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? TrashedAt,
    int Version,
    bool CanWrite);

/// <summary>Shared pieces of the file endpoints.</summary>
public static class FileHttp
{
    public static FileActor Actor(this ClaimsPrincipal user) => new(
        user.UserId() ?? throw new InvalidOperationException("Not signed in."),
        user.IsInRole(nameof(UserRole.Guest)),
        user.HasClaim(c => c.Type == AccessTokens.ReadOnlyClaim),
        user.HasClaim(c => c.Type == AccessTokens.HidePersonalClaim));

    public static NodeInfo ToInfo(Node node, bool canWrite) => new(
        node.Id,
        node.LibraryId,
        node.ParentId,
        node.Name,
        node.Kind,
        node.Size,
        node.MediaType,
        node.ModifiedAt,
        node.CreatedAt,
        node.UpdatedAt,
        node.TrashedAt,
        node.Version,
        canWrite);

    public static void SetETag(HttpContext http, Node node) =>
        http.Response.Headers.ETag = $"\"{node.Version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>
    /// Reads an enum from a query string the way the contract spells it
    /// (camelCase) or any other case. ASP.NET binds enum query parameters
    /// case-sensitively, which would refuse the documented values.
    /// </summary>
    public static bool TryReadEnum<T>(string? value, out T? result)
        where T : struct, Enum
    {
        result = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(value, out _))
        {
            result = parsed;
            return true;
        }

        return false;
    }

    public static bool TryReadIfMatch(string? header, out int version)
    {
        var raw = header?.Trim();
        if (raw is not null && raw.StartsWith("W/", StringComparison.Ordinal))
        {
            raw = raw[2..];
        }

        return int.TryParse(raw?.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    public static ProblemHttpResult Problem<T>(FileResult<T> result) => Problem(result.Error!, result.Params);

    public static ProblemHttpResult Problem(string code, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var status = code switch
        {
            FileErrors.NotFound => StatusCodes.Status404NotFound,
            FileErrors.InvalidName => StatusCodes.Status400BadRequest,
            FileErrors.ReadOnly => StatusCodes.Status403Forbidden,
            FileErrors.VersionConflict => StatusCodes.Status412PreconditionFailed,
            FileErrors.QuotaExceeded => StatusCodes.Status507InsufficientStorage,
            _ => StatusCodes.Status409Conflict,
        };
        return ApiProblem.Create(status, code, parameters?.ToDictionary(p => p.Key, p => p.Value));
    }
}
