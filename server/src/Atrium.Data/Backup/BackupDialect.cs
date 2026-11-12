using System.Data.Common;

namespace Atrium.Data.Backup;

/// <summary>Where local backups are kept: <c>/config/backups</c>.</summary>
public sealed record BackupOptions(string Directory);

/// <summary>How a value is stored in a column of the database being restored into.</summary>
public enum ColumnKind
{
    Text,
    Integer,
    Real,
    Boolean,
    Bytes,
    Uuid,
}

/// <summary>
/// The few things the portable backup needs to know about a database
/// engine. Everything else, the format included, is the same for SQLite
/// and PostgreSQL (D-008).
/// </summary>
public interface IBackupDialect
{
    string Provider { get; }

    /// <summary>
    /// A read transaction with one consistent view of every table for the
    /// whole backup: a snapshot, not a series of reads at different times.
    /// It must not block the server's writes while the backup runs.
    /// </summary>
    Task<DbTransaction> BeginSnapshotAsync(DbConnection connection, CancellationToken ct);

    /// <summary>The application's tables, without the migration bookkeeping.</summary>
    Task<IReadOnlyList<string>> ListTablesAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct);

    /// <summary>The columns of a table and how each stores its values.</summary>
    Task<IReadOnlyDictionary<string, ColumnKind>> ColumnsAsync(DbConnection connection, DbTransaction transaction, string table, CancellationToken ct);
}
