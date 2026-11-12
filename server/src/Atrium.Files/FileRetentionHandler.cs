using Atrium.Core.Jobs;
using Atrium.Data;
using Atrium.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atrium.Files;

/// <summary>
/// Ends the retention of trashed items and old versions. Removes rows only;
/// content goes later through the blob collector and its grace period, so
/// even here nothing is deleted from storage at once.
/// </summary>
public sealed class FileRetentionHandler(
    AtriumDbContext db,
    TimeProvider time,
    FilesOptions options,
    ILogger<FileRetentionHandler> logger) : IJobHandler
{
    public const string Type = "files.retention";

    public static readonly RecurringJob Schedule = new(Type, TimeSpan.FromDays(1), JobPriority.Background, JobResourceClass.Io);

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var trash = await TrashService.ExpireAsync(db, now, options.TrashRetention, ct);

        var versionCutoff = now - options.VersionRetention;
        var versions = await db.NodeVersions.Where(v => v.CreatedAt < versionCutoff).ExecuteDeleteAsync(ct);

        if (trash + versions > 0)
        {
            logger.LogInformation("Retention removed {Trash} trashed items and {Versions} old versions.", trash, versions);
        }
    }
}
