using System.Data;
using System.Data.Common;
using Atrium.Data.Backup;
using Npgsql;

namespace Atrium.Data.Postgres;

public sealed class PostgresBackupDialect : IBackupDialect
{
    public string Provider => "postgres";

    /// <summary>
    /// A read-only repeatable-read transaction: one snapshot for every
    /// table, without blocking the server's writes.
    /// </summary>
    public async Task<DbTransaction> BeginSnapshotAsync(DbConnection connection, CancellationToken ct)
    {
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SET TRANSACTION READ ONLY";
        await command.ExecuteNonQueryAsync(ct);
        return transaction;
    }

    public async Task<IReadOnlyList<string>> ListTablesAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT table_name FROM information_schema.tables " +
            "WHERE table_schema = current_schema() AND table_type = 'BASE TABLE' AND table_name NOT LIKE '\\_\\_EFMigrations%'";
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
        command.CommandText =
            "SELECT column_name, data_type FROM information_schema.columns " +
            "WHERE table_schema = current_schema() AND table_name = @table";
        command.Parameters.Add(new NpgsqlParameter("table", table));
        var columns = new Dictionary<string, ColumnKind>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            columns[reader.GetString(0)] = reader.GetString(1) switch
            {
                "uuid" => ColumnKind.Uuid,
                "boolean" => ColumnKind.Boolean,
                "bigint" or "integer" or "smallint" => ColumnKind.Integer,
                "double precision" or "real" or "numeric" => ColumnKind.Real,
                "bytea" => ColumnKind.Bytes,
                _ => ColumnKind.Text,
            };
        }

        return columns;
    }
}
