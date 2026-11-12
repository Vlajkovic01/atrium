using Atrium.Data.Backup;
using Atrium.Data.Postgres;
using Atrium.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Data.Tests;

/// <summary>
/// Each database kind has its own migrations with their own generated IDs.
/// A backup from one kind must still find its schema in the other.
/// </summary>
public sealed class BackupSchemaMatchTests
{
    private static BackupManifest Manifest(string migration, int count) =>
        new(PortableBackup.FormatVersion, DateTimeOffset.UnixEpoch, "test", "postgres", migration, count, "1.0", []);

    [Fact]
    public void The_same_id_matches_directly()
    {
        Assert.Equal("2_Second", PortableBackup.TargetMigration(["1_Initial", "2_Second"], Manifest("2_Second", 2)));
    }

    [Fact]
    public void Another_kind_matches_by_position_and_name()
    {
        Assert.Equal("200_Second", PortableBackup.TargetMigration(["100_Initial", "200_Second"], Manifest("201_Second", 2)));
        Assert.Null(PortableBackup.TargetMigration(["100_Initial", "200_Second"], Manifest("201_Other", 2)));
        Assert.Null(PortableBackup.TargetMigration(["100_Initial"], Manifest("201_Second", 2)));
    }

    [Fact]
    public void The_real_migration_sets_of_both_databases_match_each_other()
    {
        using var sqlite = new AtriumDbContext(
            (DbContextOptions<AtriumDbContext>)new DbContextOptionsBuilder<AtriumDbContext>().UseAtriumSqlite("Data Source=unused.db").Options);
        using var postgres = new AtriumDbContext(
            (DbContextOptions<AtriumDbContext>)new DbContextOptionsBuilder<AtriumDbContext>().UseAtriumPostgres("Host=unused").Options);
        var fromSqlite = sqlite.Database.GetMigrations().ToList();
        var fromPostgres = postgres.Database.GetMigrations().ToList();

        for (var i = 0; i < fromPostgres.Count; i++)
        {
            Assert.Equal(fromSqlite[i], PortableBackup.TargetMigration(fromSqlite, Manifest(fromPostgres[i], i + 1)));
            Assert.Equal(fromPostgres[i], PortableBackup.TargetMigration(fromPostgres, Manifest(fromSqlite[i], i + 1)));
        }
    }
}
