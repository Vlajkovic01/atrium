using Atrium.Data.Backup;
using Atrium.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Data.Tests;

public sealed class SqliteDatabaseTests : DatabaseContractTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_dir, "atrium.db");

    protected override AtriumDbContext CreateContext()
    {
        Directory.CreateDirectory(_dir);
        // No pooling, so the files can be deleted when the test ends.
        var connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString();
        return new AtriumDbContext(
            (DbContextOptions<AtriumDbContext>)new DbContextOptionsBuilder<AtriumDbContext>()
                .UseAtriumSqlite(connectionString)
                .Options);
    }

    protected override Task StopDatabaseAsync()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp files must not fail a test run.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Connections_use_write_ahead_logging_and_full_sync()
    {
        await using var db = CreateContext();

        var journal = await db.Database.SqlQueryRaw<string>("PRAGMA journal_mode").ToListAsync();
        var synchronous = await db.Database.SqlQueryRaw<long>("PRAGMA synchronous").ToListAsync();
        var foreignKeys = await db.Database.SqlQueryRaw<long>("PRAGMA foreign_keys").ToListAsync();

        Assert.Equal("wal", Assert.Single(journal));
        Assert.Equal(2, Assert.Single(synchronous)); // 2 = FULL
        Assert.Equal(1, Assert.Single(foreignKeys));
    }

    [Fact]
    public async Task Pre_migration_backup_is_a_verified_copy_that_restores_the_data()
    {
        await using var db = CreateContext();
        db.Users.Add(NewUser("Ana"));
        await db.SaveChangesAsync();
        var backupDir = Path.Combine(_dir, "backups");
        var backup = new PortablePreMigrationBackup(db, new SqliteBackupDialect(), backupDir, new FixedTime(T0));

        var path = await backup.CreateAsync("20260101000000_Initial", CancellationToken.None);

        Assert.StartsWith(backupDir, path, StringComparison.Ordinal);
        Assert.Contains("20261110T120000000Z", Path.GetFileName(path), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(backupDir, "*.partial"));
        await using var target = Target("restored");
        await using (var input = File.OpenRead(path))
        {
            await PortableBackup.RestoreAsync(target, new SqliteBackupDialect(), input, CancellationToken.None);
        }

        Assert.Equal("Ana", (await target.Users.SingleAsync()).Name);
    }

    [Fact]
    public async Task Pre_migration_backup_never_overwrites_an_existing_backup()
    {
        await using var db = CreateContext();
        var backup = new PortablePreMigrationBackup(db, new SqliteBackupDialect(), Path.Combine(_dir, "backups"), new FixedTime(T0));
        var first = await backup.CreateAsync("Initial", CancellationToken.None);
        var before = await File.ReadAllBytesAsync(first);

        await Assert.ThrowsAsync<IOException>(() => backup.CreateAsync("Initial", CancellationToken.None));

        Assert.Equal(before, await File.ReadAllBytesAsync(first));
    }

    [Fact]
    public async Task A_backup_in_progress_does_not_block_the_server_from_writing()
    {
        await using var reader = CreateContext();
        var connection = reader.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var snapshot = await new SqliteBackupDialect().BeginSnapshotAsync(connection, CancellationToken.None);
        await using (var count = connection.CreateCommand())
        {
            count.Transaction = snapshot;
            count.CommandText = "SELECT COUNT(*) FROM \"Users\"";
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }

        await using (var writer = CreateContext())
        {
            writer.Users.Add(NewUser("Ana"));
            await writer.SaveChangesAsync();
        }

        await using var again = connection.CreateCommand();
        again.Transaction = snapshot;
        again.CommandText = "SELECT COUNT(*) FROM \"Users\"";
        Assert.Equal(0L, await again.ExecuteScalarAsync());
    }

    private AtriumDbContext Target(string name) => new(
        (DbContextOptions<AtriumDbContext>)new DbContextOptionsBuilder<AtriumDbContext>()
            .UseAtriumSqlite(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_dir, name + ".db"), Pooling = false }.ToString())
            .Options);
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
