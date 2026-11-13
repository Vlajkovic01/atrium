using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Atrium.Jobs;

public static class JobsSetup
{
    /// <summary>The queue, the runner and the built-in housekeeping job. Workers start with <see cref="AddJobWorker"/>.</summary>
    public static IServiceCollection AddAtriumJobs(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new JobOptions());
        services.TryAddSingleton<JobSignal>();
        services.TryAddSingleton(_ => HardwareInfo.Detect());
        services.TryAddSingleton<JobLimits>();
        services.TryAddScoped<JobSettingsStore>();
        services.TryAddScoped<JobQueue>();
        services.TryAddSingleton<JobRunner>();
        services.TryAddSingleton<RecurringJobScheduler>();
        services.AddJobHandler<JobCleanupHandler>(JobCleanupHandler.Type);
        services.AddRecurringJob(JobCleanupHandler.Schedule);
        return services;
    }

    /// <summary>Runs jobs in the background of this process.</summary>
    public static IServiceCollection AddJobWorker(this IServiceCollection services) =>
        services.AddHostedService<JobWorker>();

    public static IServiceCollection AddJobHandler<THandler>(this IServiceCollection services, string type)
        where THandler : class, IJobHandler
    {
        services.AddScoped<THandler>();
        services.AddSingleton(new JobHandlerRegistration(type, typeof(THandler)));
        return services;
    }

    public static IServiceCollection AddRecurringJob(this IServiceCollection services, RecurringJob job) =>
        services.AddSingleton(job);
}
