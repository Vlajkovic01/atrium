using System.Text;
using Atrium.Core.Jobs;
using Atrium.Files.Blobs;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Files.Tests;

/// <summary>The integrity check finds damage and reports it, and never changes anything.</summary>
public abstract class ScrubContractTests<TDatabase> : IAsyncLifetime
    where TDatabase : FileTestDatabase, new()
{
    private readonly TDatabase _database = new();
    private FileServicesHost? _host;

    private FileServicesHost Host => _host ?? throw new InvalidOperationException("Not initialized.");

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _host = await FileServicesHost.StartAsync(_database);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    private async Task<IntegrityReport> ScrubAsync()
    {
        Assert.Equal(JobState.Succeeded, (await Host.RunJobAsync(ScrubHandler.Type)).State);
        IntegrityReport? report = null;
        await Host.WithDbAsync(async db => report = await ScrubHandler.LastReportAsync(db, CancellationToken.None));
        return report!;
    }

    private string PathOf(string key) => Path.Combine([Host.StorageRoot, .. key.Split('/')]);

    [Fact]
    public async Task Healthy_storage_reports_nothing()
    {
        await Host.StoreAsync(Encoding.UTF8.GetBytes("one"));
        await Host.StoreAsync(Encoding.UTF8.GetBytes("two"));

        var report = await ScrubAsync();

        Assert.Equal(2, report.BlobsChecked);
        Assert.Equal(6, report.BytesChecked);
        Assert.Empty(report.Issues);
        Assert.NotNull(report.FinishedAt);
    }

    [Fact]
    public async Task Damaged_missing_and_unknown_objects_are_reported_and_left_as_they_are()
    {
        var damaged = await Host.StoreAsync(Encoding.UTF8.GetBytes("will be damaged"));
        var missing = await Host.StoreAsync(Encoding.UTF8.GetBytes("will go missing"));
        await File.WriteAllTextAsync(PathOf(damaged.Key), "bit rot happened");
        File.Delete(PathOf(missing.Key));
        var stray = Blob(new string('f', 64));
        await Host.Raw.WriteAsync(stray, new MemoryStream([1, 2, 3]), false, CancellationToken.None);

        var report = await ScrubAsync();

        Assert.Equal(1, report.Damaged);
        Assert.Equal(1, report.Missing);
        Assert.Equal(1, report.Unknown);
        Assert.Contains(report.Issues, i => i.Problem == IntegrityProblem.Damaged && i.BlobId == damaged.Id);
        Assert.Contains(report.Issues, i => i.Problem == IntegrityProblem.Missing && i.BlobId == missing.Id);
        Assert.Contains(report.Issues, i => i.Problem == IntegrityProblem.Unknown && i.Key == stray);

        // Reporting only: nothing was repaired, moved or removed.
        Assert.Equal("bit rot happened", await File.ReadAllTextAsync(PathOf(damaged.Key)));
        Assert.True(File.Exists(PathOf(stray)));
        Assert.Equal(2, (await Host.BlobsAsync()).Count);
    }

    [Fact]
    public async Task An_unreachable_location_is_reported_not_counted_as_missing_files()
    {
        await Host.StoreAsync(Encoding.UTF8.GetBytes("on a share"));
        await Host.Raw.DeleteAsync(Atrium.Storage.LocationIdentityFile.Key, CancellationToken.None);
        Host.Services.GetRequiredService<Atrium.Files.Storage.StorageProviders>().Invalidate(Host.Location.Id);

        var report = await ScrubAsync();

        Assert.Equal(0, report.Missing);
        Assert.Contains(report.Issues, i => i.Problem == IntegrityProblem.LocationUnavailable);
    }

    private static string Blob(string hex) => $"atrium/blobs/{hex[..2]}/{hex[2..4]}/{hex}";
}

public sealed class SqliteScrubTests : ScrubContractTests<SqliteFileDatabase>;

[Trait("Category", "Containers")]
public sealed class PostgresScrubTests : ScrubContractTests<PostgresFileDatabase>;
