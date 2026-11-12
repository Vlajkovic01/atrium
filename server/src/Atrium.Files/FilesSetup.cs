using Atrium.Files.Blobs;
using Atrium.Files.Storage;
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
        services.AddJobHandler<BlobCollectorHandler>(BlobCollectorHandler.Type);
        services.AddRecurringJob(BlobCollectorHandler.Schedule);
        return services;
    }
}
