using Atrium.Files.Backups;
using Atrium.Files.Blobs;
using Atrium.Files.External;
using Atrium.Files.Storage;
using Atrium.Files.Uploads;
using Atrium.Files.Users;
using Atrium.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Atrium.Files;

public static class FilesSetup
{
    public static IServiceCollection AddAtriumFiles(this IServiceCollection services)
    {
        services.TryAddSingleton(new FilesOptions());
        services.AddSingleton<StorageProviders>();
        services.AddSingleton<BlobLocks>();
        services.AddScoped<LibraryService>();
        services.AddScoped<BlobStore>();
        services.AddScoped<NodeService>();
        services.AddScoped<TrashService>();
        services.AddScoped<VersionService>();
        services.AddScoped<UsageService>();
        services.AddJobHandler<FileRetentionHandler>(FileRetentionHandler.Type);
        services.AddRecurringJob(FileRetentionHandler.Schedule);
        services.AddSingleton<UploadRunningHashes>();
        services.AddScoped<UploadService>();
        services.AddJobHandler<UploadCleanupHandler>(UploadCleanupHandler.Type);
        services.AddRecurringJob(UploadCleanupHandler.Schedule);
        services.AddJobHandler<ScrubHandler>(ScrubHandler.Type);
        services.AddRecurringJob(ScrubHandler.Schedule);
        services.AddScoped<UserDeletionService>();
        services.AddJobHandler<UserDeletionHandler>(UserDeletionHandler.Type);
        services.AddJobHandler<MetadataBackupHandler>(MetadataBackupHandler.Type);
        services.AddRecurringJob(MetadataBackupHandler.Schedule);
        services.AddJobHandler<BlobCollectorHandler>(BlobCollectorHandler.Type);
        services.AddRecurringJob(BlobCollectorHandler.Schedule);

        // External libraries. The host supplies the real media type table
        // and the paths a library must stay clear of.
        services.TryAddSingleton(new MediaTypeResolver(_ => "application/octet-stream"));
        services.TryAddSingleton(new ExternalLibraryOptions([]));
        services.AddScoped<ExternalLibraryService>();
        services.AddScoped<NodeContent>();
        services.AddJobHandler<LibraryScanHandler>(LibraryScanHandler.Type);
        services.AddJobHandler<LibraryScanAllHandler>(LibraryScanAllHandler.Type);
        services.AddRecurringJob(LibraryScanAllHandler.Schedule);
        return services;
    }
}
