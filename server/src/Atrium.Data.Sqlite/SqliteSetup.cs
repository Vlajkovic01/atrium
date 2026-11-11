using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Atrium.Data.Sqlite;

public static class SqliteSetup
{
    public static DbContextOptionsBuilder UseAtriumSqlite(this DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseSqlite(connectionString, o => o.MigrationsAssembly(typeof(SqliteSetup).Assembly.GetName().Name))
            .AddInterceptors(new SqlitePragmaInterceptor());
}

/// <summary>
/// Per-connection settings. Write-ahead logging lets readers work while a
/// write is in progress; synchronous=FULL makes a committed transaction
/// survive power loss, which matters more here than write speed because
/// this database is the only record of file names and folders.
/// </summary>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const string Pragmas =
        "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Used only by the EF Core tools when generating migrations.</summary>
internal sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<AtriumDbContext>
{
    public AtriumDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AtriumDbContext>()
            .UseSqlite("Data Source=design-time.db", o => o.MigrationsAssembly(typeof(SqliteSetup).Assembly.GetName().Name))
            .Options);
}
