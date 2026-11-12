using System.Data.Common;
using Atrium.Data.Backup;
using Microsoft.Data.Sqlite;

namespace Atrium.Data.Sqlite;

public sealed class SqliteBackupDialect : IBackupDialect
{
    public string Provider => "sqlite";

    /// <summary>
    /// A deferred transaction: in write-ahead-log mode its first read fixes
    /// the snapshot every later read sees, and it takes no write lock, so the
    /// server keeps working while the backup runs.
    /// </summary>
    public Task<DbTransaction> BeginSnapshotAsync(DbConnection connection, CancellationToken ct) =>
        Task.FromResult<DbTransaction>(((SqliteConnection)connection).BeginTransaction(deferred: true));

    public async Task<IReadOnlyList<string>> ListTablesAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' " +
            "AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' AND name NOT LIKE '\\_\\_EFMigrations%' ESCAPE '\\'";
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    public async Task<IReadOnlyDictionary<string, ColumnKind>> ColumnsAsync(
        DbConnection connection, DbTransaction transaction, string table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name, type FROM pragma_table_info($table)";
        command.Parameters.Add(new SqliteParameter("$table", table));
        var columns = new Dictionary<string, ColumnKind>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            columns[reader.GetString(0)] = reader.GetString(1).ToUpperInvariant() switch
            {
                "INTEGER" => ColumnKind.Integer,
                "REAL" => ColumnKind.Real,
                "BLOB" => ColumnKind.Bytes,
                _ => ColumnKind.Text,
            };
        }

        return columns;
    }
}
