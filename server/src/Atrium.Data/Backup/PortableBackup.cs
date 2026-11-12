using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Atrium.Data.Backup;

/// <summary>What a backup says about itself. Stored as manifest.json inside the archive.</summary>
/// <param name="Migration">The last migration applied to the source database.</param>
/// <param name="MigrationCount">
/// How many migrations were applied. Each database kind has its own
/// migration set with its own generated IDs, so a backup restored into the
/// other kind finds its schema by this position and the migration's name.
/// </param>
public sealed record BackupManifest(
    int Format,
    DateTimeOffset CreatedAt,
    string Reason,
    string Provider,
    string Migration,
    int MigrationCount,
    string ServerVersion,
    IReadOnlyList<BackupTable> Tables);

public sealed record BackupTable(string Name, long Rows, string Sha256);

public sealed class BackupInvalidException(string message) : Exception(message);

/// <summary>
/// The portable metadata backup (D-008): a ZIP holding one JSON-lines file
/// per table and a manifest with each file's row count and SHA-256, read
/// in a single database snapshot. It restores into SQLite or PostgreSQL,
/// whichever the backup came from.
/// </summary>
/// <remarks>
/// Writing reads the database's own tables and columns, not the server's
/// model, so a backup taken before a migration describes the old schema
/// exactly. Restoring first migrates an empty database to the backup's
/// schema, loads the rows, and leaves later migrations to the normal
/// startup.
/// </remarks>
public static class PortableBackup
{
    public const int FormatVersion = 1;

    /// <summary>
    /// Signed-in devices and their tokens are left out (D-029). They are worth
    /// nothing for recovery, since everyone simply signs in again, and a
    /// backup copied next to the files should hold no session material.
    /// Accounts, password hashes and encrypted secrets stay: without them
    /// nobody could sign in after a restore.
    /// </summary>
    public static readonly IReadOnlySet<string> ExcludedTables =
        new HashSet<string>(["Sessions", "RefreshTokens", "PairingRequests", "LinkCodes"], StringComparer.Ordinal);
    public const string ManifestEntry = "manifest.json";
    private const string TablesFolder = "tables/";
    private const string BytesMarker = "$b";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<BackupManifest> WriteAsync(
        AtriumDbContext db, IBackupDialect dialect, Stream output, string reason, string serverVersion, DateTimeOffset now, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            await using var transaction = await dialect.BeginSnapshotAsync(connection, ct);
            var (migration, migrationCount) = await LastMigrationAsync(connection, transaction, ct);
            var tables = new List<BackupTable>();
            await using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                var included = (await dialect.ListTablesAsync(connection, transaction, ct))
                    .Where(t => !ExcludedTables.Contains(t))
                    .Order(StringComparer.Ordinal);
                foreach (var table in included)
                {
                    var entry = zip.CreateEntry(TablesFolder + table + ".jsonl", CompressionLevel.Optimal);
                    await using var stream = await entry.OpenAsync(ct);
                    tables.Add(await WriteTableAsync(connection, transaction, table, stream, ct));
                }

                var manifest = new BackupManifest(FormatVersion, now, reason, dialect.Provider, migration, migrationCount, serverVersion, tables);
                var manifestEntry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                await using (var manifestStream = await manifestEntry.OpenAsync(ct))
                {
                    await JsonSerializer.SerializeAsync(manifestStream, manifest, Json, ct);
                }

                await transaction.CommitAsync(ct);
                return manifest;
            }
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// Reads the manifest and checks every table file against it. A backup
    /// that fails this is never used: restoring from it, or trusting it
    /// before a migration, would give false confidence.
    /// </summary>
    public static async Task<BackupManifest> VerifyAsync(Stream input, CancellationToken ct)
    {
        await using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = await ReadManifestAsync(zip, ct);
        foreach (var table in manifest.Tables)
        {
            var entry = zip.GetEntry(TablesFolder + table.Name + ".jsonl")
                ?? throw new BackupInvalidException($"The backup has no data for table {table.Name}.");
            await using var stream = await entry.OpenAsync(ct);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long lines = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                lines += buffer.AsSpan(0, read).Count((byte)'\n');
            }

            if (lines != table.Rows || System.Convert.ToHexStringLower(hash.GetHashAndReset()) != table.Sha256)
            {
                throw new BackupInvalidException($"The data for table {table.Name} is damaged.");
            }
        }

        return manifest;
    }

    /// <summary>
    /// Restores into a database that holds no data yet. Refuses a backup
    /// from a schema this server does not know, and a database that is not
    /// empty: a restore must never mix two sets of metadata.
    /// </summary>
    public static async Task<long> RestoreAsync(AtriumDbContext db, IBackupDialect dialect, Stream input, CancellationToken ct)
    {
        var manifest = await VerifyAsync(input, ct);
        input.Position = 0;
        if (manifest.Format != FormatVersion)
        {
            throw new BackupInvalidException($"The backup uses format {manifest.Format}; this server reads format {FormatVersion}.");
        }

        var target = TargetMigration(db.Database.GetMigrations().ToList(), manifest)
            ?? throw new BackupInvalidException(
                $"The backup was taken at schema {manifest.Migration}, which this server does not know. Restore it with the same or a newer version.");

        if ((await db.Database.GetAppliedMigrationsAsync(ct)).Any())
        {
            throw new BackupInvalidException("The database is not empty. Restore into a new, empty database.");
        }

        await db.GetService<IMigrator>().MigrateAsync(target, ct);

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            long total = 0;
            foreach (var table in InsertionOrder(db, manifest.Tables.Select(t => t.Name)))
            {
                var columns = await dialect.ColumnsAsync(connection, transaction, table, ct);
                await foreach (var row in RowsInOrderAsync(db, zip, table, ct))
                {
                    await InsertAsync(connection, transaction, table, columns, row, ct);
                    total++;
                }
            }

            await transaction.CommitAsync(ct);
            return total;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private static async Task<BackupManifest> ReadManifestAsync(ZipArchive zip, CancellationToken ct)
    {
        var entry = zip.GetEntry(ManifestEntry) ?? throw new BackupInvalidException("This is not an Atrium backup: it has no manifest.");
        await using var stream = await entry.OpenAsync(ct);
        try
        {
            return await JsonSerializer.DeserializeAsync<BackupManifest>(stream, Json, ct)
                ?? throw new BackupInvalidException("The backup's manifest is empty.");
        }
        catch (JsonException e)
        {
            throw new BackupInvalidException("The backup's manifest is damaged: " + e.Message);
        }
    }

    /// <summary>
    /// The migration of this server that matches the backup's schema: the
    /// same ID on the same kind of database, or on the other kind the
    /// migration at the same position with the same name.
    /// </summary>
    public static string? TargetMigration(IReadOnlyList<string> known, BackupManifest manifest)
    {
        if (known.Contains(manifest.Migration, StringComparer.Ordinal))
        {
            return manifest.Migration;
        }

        if (manifest.MigrationCount < 1 || manifest.MigrationCount > known.Count)
        {
            return null;
        }

        var candidate = known[manifest.MigrationCount - 1];
        return NameOf(candidate) == NameOf(manifest.Migration) ? candidate : null;
    }

    // "20261112154212_Initial" is named "Initial".
    private static string NameOf(string migrationId) =>
        migrationId.IndexOf('_', StringComparison.Ordinal) is var underscore and >= 0 ? migrationId[(underscore + 1)..] : migrationId;

    private static async Task<(string Id, int Count)> LastMigrationAsync(DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        var applied = new List<string>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            applied.Add(reader.GetString(0));
        }

        return applied.Count == 0
            ? throw new BackupInvalidException("The database has no schema yet; there is nothing to back up.")
            : (applied[^1], applied.Count);
    }

    private static async Task<BackupTable> WriteTableAsync(
        DbConnection connection, DbTransaction transaction, string table, Stream output, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT * FROM {Quote(table)}";
        await using var reader = await command.ExecuteReaderAsync(ct);
        long rows = 0;
        var buffer = new MemoryStream();
        while (await reader.ReadAsync(ct))
        {
            buffer.SetLength(0);
            await using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    writer.WritePropertyName(reader.GetName(i));
                    WriteValue(writer, await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i));
                }

                writer.WriteEndObject();
            }

            buffer.WriteByte((byte)'\n');
            var line = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
            hash.AppendData(line.Span);
            await output.WriteAsync(line, ct);
            rows++;
        }

        return new BackupTable(table, rows, System.Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case short sh:
                writer.WriteNumberValue(sh);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case byte[] bytes:
                writer.WriteStartObject();
                writer.WriteString(BytesMarker, System.Convert.ToBase64String(bytes));
                writer.WriteEndObject();
                break;
            case Guid g:
                // The form EF Core uses for GUIDs in SQLite, so a backup taken
                // on PostgreSQL restores into SQLite with matching keys.
                writer.WriteStringValue(g.ToString("D").ToUpperInvariant());
                break;
            default:
                throw new NotSupportedException($"Values of type {value.GetType().Name} cannot be backed up.");
        }
    }

    /// <summary>
    /// The rows of a table in an order the foreign keys accept. Streamed one
    /// at a time, except for a table that references itself, which must be
    /// read whole to put parents before children.
    /// </summary>
    private static async IAsyncEnumerable<Dictionary<string, JsonElement>> RowsInOrderAsync(
        AtriumDbContext db, ZipArchive zip, string table, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var selfReferencing = db.Model.GetEntityTypes()
            .Any(e => e.GetTableName() == table && e.GetForeignKeys().Any(fk => fk.PrincipalEntityType == e));
        if (!selfReferencing)
        {
            await foreach (var row in ReadRowsAsync(zip, table, ct))
            {
                yield return row;
            }

            yield break;
        }

        var rows = new List<Dictionary<string, JsonElement>>();
        await foreach (var row in ReadRowsAsync(zip, table, ct))
        {
            rows.Add(row);
        }

        foreach (var row in ParentsFirst(db, table, rows))
        {
            yield return row;
        }
    }

    private static async IAsyncEnumerable<Dictionary<string, JsonElement>> ReadRowsAsync(
        ZipArchive zip, string table, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var entry = zip.GetEntry(TablesFolder + table + ".jsonl")!;
        await using var stream = await entry.OpenAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            yield return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        }
    }

    private static async Task InsertAsync(
        DbConnection connection, DbTransaction transaction, string table, IReadOnlyDictionary<string, ColumnKind> columns,
        Dictionary<string, JsonElement> row, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = new List<string>();
        var placeholders = new List<string>();
        foreach (var (column, value) in row)
        {
            if (!columns.TryGetValue(column, out var kind))
            {
                throw new BackupInvalidException($"Column {table}.{column} in the backup does not exist in the database.");
            }

            var parameter = command.CreateParameter();
            parameter.ParameterName = "@p" + names.Count.ToString(CultureInfo.InvariantCulture);
            parameter.Value = FromJson(value, kind) ?? DBNull.Value;
            command.Parameters.Add(parameter);
            names.Add(Quote(column));
            placeholders.Add(parameter.ParameterName);
        }

        command.CommandText = $"INSERT INTO {Quote(table)} ({string.Join(", ", names)}) VALUES ({string.Join(", ", placeholders)})";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static object? FromJson(JsonElement value, ColumnKind kind)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return kind switch
        {
            ColumnKind.Uuid => Guid.Parse(value.GetString()!),
            ColumnKind.Boolean => value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => value.GetInt64() != 0,
            },
            ColumnKind.Integer => value.ValueKind switch
            {
                JsonValueKind.True => 1L,
                JsonValueKind.False => 0L,
                _ => value.GetInt64(),
            },
            ColumnKind.Real => value.GetDouble(),
            ColumnKind.Bytes => System.Convert.FromBase64String(value.GetProperty(BytesMarker).GetString()!),
            _ => value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(),
        };
    }

    /// <summary>Tables ordered so that every table comes after the tables its foreign keys point to.</summary>
    private static List<string> InsertionOrder(AtriumDbContext db, IEnumerable<string> tables)
    {
        var present = tables.ToHashSet(StringComparer.Ordinal);
        var dependsOn = present.ToDictionary(t => t, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var entity in db.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is not { } table || !present.Contains(table))
            {
                continue;
            }

            foreach (var fk in entity.GetForeignKeys())
            {
                if (fk.PrincipalEntityType.GetTableName() is { } principal && principal != table && present.Contains(principal))
                {
                    dependsOn[table].Add(principal);
                }
            }
        }

        var ordered = new List<string>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string table, HashSet<string> path)
        {
            if (done.Contains(table))
            {
                return;
            }

            if (!path.Add(table))
            {
                throw new InvalidOperationException($"Foreign keys form a cycle through {table}.");
            }

            foreach (var principal in dependsOn[table].Order(StringComparer.Ordinal))
            {
                Visit(principal, path);
            }

            path.Remove(table);
            done.Add(table);
            ordered.Add(table);
        }

        foreach (var table in present.Order(StringComparer.Ordinal))
        {
            Visit(table, new HashSet<string>(StringComparer.Ordinal));
        }

        return ordered;
    }

    /// <summary>For a table that references itself (folders, child jobs), rows whose parent comes first.</summary>
    private static IEnumerable<Dictionary<string, JsonElement>> ParentsFirst(AtriumDbContext db, string table, List<Dictionary<string, JsonElement>> rows)
    {
        var entity = db.Model.GetEntityTypes().FirstOrDefault(e => e.GetTableName() == table);
        var selfKey = entity?.GetForeignKeys().FirstOrDefault(fk => fk.PrincipalEntityType == entity && fk.Properties.Count == 1);
        if (entity is null || selfKey is null)
        {
            return rows;
        }

        var keyColumn = entity.FindPrimaryKey()!.Properties[0].GetColumnName();
        var parentColumn = selfKey.Properties[0].GetColumnName();
        string? Text(Dictionary<string, JsonElement> row, string column) =>
            row.TryGetValue(column, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.ToUpperInvariant() : null;

        var byId = rows.ToDictionary(r => Text(r, keyColumn)!, StringComparer.Ordinal);
        var ordered = new List<Dictionary<string, JsonElement>>(rows.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var chain = new Stack<Dictionary<string, JsonElement>>();
            var current = row;
            while (current is not null && !placed.Contains(Text(current, keyColumn)!))
            {
                chain.Push(current);
                var parent = Text(current, parentColumn);
                current = parent is not null && byId.TryGetValue(parent, out var p) && !chain.Contains(p) ? p : null;
            }

            while (chain.Count > 0)
            {
                var next = chain.Pop();
                placed.Add(Text(next, keyColumn)!);
                ordered.Add(next);
            }
        }

        return ordered;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
