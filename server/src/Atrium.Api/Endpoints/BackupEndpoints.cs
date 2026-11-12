using Atrium.Api.Auth;
using Atrium.Data.Backup;
using Atrium.Files.Backups;
using Atrium.Jobs;

namespace Atrium.Api.Endpoints;

/// <param name="Reason"><c>scheduled</c>, <c>manual</c> or <c>pre-migration</c>.</param>
public sealed record BackupInfo(string Name, long Size, DateTimeOffset CreatedAt, string Reason);

public sealed record JobStarted(Guid JobId);

/// <summary>Metadata backups for admins (DESIGN.md 8.2). Restore is a command-line step until Phase 11.</summary>
public static class BackupEndpoints
{
    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder routes)
    {
        var backups = routes.MapGroup("/admin/backups")
            .WithTags("Administration")
            .RequireAuthorization(AuthSetup.AdminPolicy, AuthSetup.FullSessionPolicy);

        backups.MapGet("/", (BackupOptions options) =>
                BackupFiles.List(options.Directory).Select(f => new BackupInfo(f.Name, f.Size, f.CreatedAt, f.Reason)).ToList())
            .WithName("ListBackups");

        // A backup can take a while on a large library, so it runs as a job
        // the client can follow.
        backups.MapPost("/", async (JobQueue queue, HttpContext http, CancellationToken ct) =>
            {
                var job = await queue.EnqueueAsync(
                    new JobRequest(MetadataBackupHandler.Type)
                    {
                        Payload = new MetadataBackupPayload(MetadataBackupHandler.Manual),
                        Priority = Atrium.Core.Jobs.JobPriority.User,
                        DedupeKey = "backup:manual",
                    },
                    ct);
                return TypedResults.Accepted($"/api/v1/jobs/{job.Id}", new JobStarted(job.Id));
            })
            .WithName("CreateBackup");

        return routes;
    }
}
