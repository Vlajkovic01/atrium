using System.Collections.Concurrent;
using Atrium.Core.Jobs;
using Atrium.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Jobs.Tests;

/// <summary>A clock the tests move by hand. Timers still run in real time.</summary>
public sealed class MutableTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>What each test job type does, set by the test.</summary>
public sealed class TestBehaviors
{
    public ConcurrentDictionary<string, Func<JobContext, CancellationToken, Task>> ByType { get; } = new();
}

public sealed class TestHandler(TestBehaviors behaviors) : IJobHandler
{
    public Task RunAsync(JobContext context, CancellationToken ct) =>
        behaviors.ByType.TryGetValue(context.Job.Type, out var run) ? run(context, ct) : Task.CompletedTask;
}

/// <summary>
/// The job system wired as in the server, over a real database, with a
/// configurable handler for the job types <c>test.a</c> to <c>test.d</c>.
/// </summary>
public sealed class JobTestHost : IAsyncDisposable
{
    public static readonly string[] TestTypes = ["test.a", "test.b", "test.c", "test.d"];

    private readonly ServiceProvider _services;

    public JobTestHost(Action<DbContextOptionsBuilder> database, JobOptions? options = null, params RecurringJob[] recurring)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AtriumDbContext>(database);
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton(options ?? new JobOptions
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            PollInterval = TimeSpan.FromMilliseconds(50),
        });
        services.AddSingleton(Behaviors);
        services.AddAtriumJobs();
        foreach (var type in TestTypes)
        {
            services.AddJobHandler<TestHandler>(type);
        }

        foreach (var job in recurring)
        {
            services.AddRecurringJob(job);
        }

        _services = services.BuildServiceProvider(validateScopes: true);
    }

    public MutableTime Time { get; } = new();

    public TestBehaviors Behaviors { get; } = new();

    public IServiceProvider Services => _services;

    public JobRunner Runner => _services.GetRequiredService<JobRunner>();

    public static IReadOnlyCollection<JobResourceClass> AllClasses { get; } = Enum.GetValues<JobResourceClass>();

    public async Task MigrateAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AtriumDbContext>().Database.MigrateAsync();
    }

    public async Task<Job> EnqueueAsync(JobRequest request)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JobQueue>().EnqueueAsync(request, CancellationToken.None);
    }

    public async Task<T> WithQueueAsync<T>(Func<JobQueue, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<JobQueue>());
    }

    public async Task<Job> GetAsync(Guid id)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AtriumDbContext>().Jobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }

    public async Task<List<Job>> AllJobsAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AtriumDbContext>().Jobs.AsNoTracking().ToListAsync();
    }

    public async Task WithDbAsync(Func<AtriumDbContext, Task> action)
    {
        await using var scope = _services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AtriumDbContext>());
    }

    public Task<bool> RunNextAsync(CancellationToken ct = default) => Runner.RunNextAsync(AllClasses, ct);

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
