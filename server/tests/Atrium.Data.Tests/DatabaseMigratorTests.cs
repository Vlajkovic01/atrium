using Atrium.Data.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atrium.Data.Tests;

/// <summary>The ordering rules of an upgrade: refuse downgrades, back up, then migrate.</summary>
public sealed class DatabaseMigratorTests
{
    private readonly List<string> _events = [];

    private DatabaseMigrator Migrator(string[] known, string[] applied, bool backupFails = false) =>
        new(new FakeOperations(known, applied, _events), new FakeBackup(_events, backupFails), NullLogger<DatabaseMigrator>.Instance);

    [Fact]
    public async Task A_fresh_database_is_migrated_without_a_backup()
    {
        await Migrator(known: ["m1", "m2"], applied: []).MigrateAsync(CancellationToken.None);

        Assert.Equal(["apply"], _events);
    }

    [Fact]
    public async Task An_existing_database_is_backed_up_before_it_is_migrated()
    {
        await Migrator(known: ["m1", "m2", "m3"], applied: ["m1", "m2"]).MigrateAsync(CancellationToken.None);

        Assert.Equal(["backup from m2", "apply"], _events);
    }

    [Fact]
    public async Task A_failed_backup_stops_the_migration()
    {
        var migrator = Migrator(known: ["m1", "m2"], applied: ["m1"], backupFails: true);

        await Assert.ThrowsAsync<IOException>(() => migrator.MigrateAsync(CancellationToken.None));

        Assert.DoesNotContain("apply", _events);
    }

    [Fact]
    public async Task An_up_to_date_database_is_left_alone()
    {
        await Migrator(known: ["m1", "m2"], applied: ["m1", "m2"]).MigrateAsync(CancellationToken.None);

        Assert.Empty(_events);
    }

    [Fact]
    public async Task A_database_from_a_newer_server_is_refused_and_not_touched()
    {
        var migrator = Migrator(known: ["m1"], applied: ["m1", "m2-from-the-future"]);

        var error = await Assert.ThrowsAsync<DatabaseNewerThanServerException>(
            () => migrator.MigrateAsync(CancellationToken.None));

        Assert.Equal(["m2-from-the-future"], error.UnknownMigrations);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task A_newer_database_is_refused_even_when_this_server_also_has_pending_migrations()
    {
        var migrator = Migrator(known: ["m1", "m2"], applied: ["m1", "x-from-another-branch"]);

        await Assert.ThrowsAsync<DatabaseNewerThanServerException>(
            () => migrator.MigrateAsync(CancellationToken.None));

        Assert.Empty(_events);
    }

    private sealed class FakeOperations(string[] known, string[] applied, List<string> events) : IMigrationOperations
    {
        public IReadOnlyList<string> Known => known;

        public Task<IReadOnlyList<string>> GetAppliedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(applied);

        public Task ApplyPendingAsync(CancellationToken ct)
        {
            events.Add("apply");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBackup(List<string> events, bool fails) : IPreMigrationBackup
    {
        public Task<string> CreateAsync(string fromMigration, CancellationToken ct)
        {
            if (fails)
            {
                throw new IOException("disk full");
            }

            events.Add($"backup from {fromMigration}");
            return Task.FromResult("backup-location");
        }
    }
}
