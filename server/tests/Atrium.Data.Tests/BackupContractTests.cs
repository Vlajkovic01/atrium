using System.IO.Compression;
using System.Text.Json;
using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Core.Jobs;
using Atrium.Core.Settings;
using Atrium.Core.Storage;
using Atrium.Data.Backup;
using Atrium.Data.Postgres;
using Atrium.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Atrium.Data.Tests;

/// <summary>
/// The portable metadata backup (D-008) against each real database: what
/// goes in comes out, row for row, and a damaged or mismatched backup is
/// refused before it can do harm.
/// </summary>
public abstract class BackupContractTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    protected abstract IBackupDialect Dialect { get; }

    protected abstract AtriumDbContext CreateSource();

    /// <summary>A new, empty database of the same kind.</summary>
    protected abstract Task<AtriumDbContext> CreateEmptyTargetAsync(string name);

    protected virtual Task StartAsync() => Task.CompletedTask;

    protected virtual Task StopAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        await StartAsync();
        await using var db = CreateSource();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => StopAsync();

    /// <summary>One of everything, including a deep folder tree and a child job, so ordering is exercised.</summary>
    protected static async Task PopulateAsync(AtriumDbContext db)
    {
        var user = new User
        {
            Name = "Ana",
            NameKey = "ana",
            DisplayName = "Ana Anić",
            PasswordHash = "$argon2id$x",
            Role = UserRole.Admin,
            QuotaBytes = 1L << 40,
            CreatedAt = T0,
            PasswordChangedAt = T0,
        };
        var session = new Session
        {
            UserId = user.Id,
            DeviceName = "TV",
            DeviceKind = DeviceKind.Tv,
            CreatedAt = T0,
            LastSeenAt = T0,
            ReadOnly = true,
        };
        var location = new StorageLocation
        {
            Name = "main",
            Kind = StorageProviderKind.S3,
            EncryptedConfig = [0, 1, 2, 255],
            State = StorageLocationState.Online,
            CreatedAt = T0,
        };
        var root = new Node { Name = "Personal", NameKey = "personal", ActiveNameKey = "personal", Kind = NodeKind.Folder, CreatedAt = T0, ModifiedAt = T0, UpdatedAt = T0 };
        var library = new Library
        {
            Name = "Personal",
            Kind = LibraryKind.Personal,
            Mode = LibraryMode.Managed,
            StorageLocationId = location.Id,
            OwnerUserId = user.Id,
            RootNodeId = root.Id,
            CreatedAt = T0,
        };
        var blob = new Blob { StorageLocationId = location.Id, Sha256 = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), Size = 5, CreatedAt = T0 };
        var older = new Blob { StorageLocationId = location.Id, Sha256 = Enumerable.Repeat((byte)7, 32).ToArray(), Size = 3, State = BlobState.PendingDelete, PendingDeleteSince = T0, CreatedAt = T0 };

        db.AddRange(user, session, location, library, blob, older);
        root = new Node
        {
            Id = root.Id,
            LibraryId = library.Id,
            Name = root.Name,
            NameKey = root.NameKey,
            ActiveNameKey = root.ActiveNameKey,
            Kind = NodeKind.Folder,
            CreatedAt = T0,
            ModifiedAt = T0,
            UpdatedAt = T0,
        };
        db.Add(root);

        // Created children first, so a restore that kept the file's order would break the foreign key.
        var chain = new List<Node>();
        var parent = root.Id;
        for (var depth = 0; depth < 12; depth++)
        {
            var folder = new Node
            {
                LibraryId = library.Id,
                ParentId = parent,
                Name = $"Level {depth}",
                NameKey = $"level {depth}",
                ActiveNameKey = $"level {depth}",
                Kind = NodeKind.Folder,
                CreatedAt = T0,
                ModifiedAt = T0,
                UpdatedAt = T0,
            };
            chain.Add(folder);
            parent = folder.Id;
        }

        db.AddRange(chain);
        var file = new Node
        {
            LibraryId = library.Id,
            ParentId = parent,
            Name = "Račun.pdf",
            NameKey = "račun.pdf",
            ActiveNameKey = "račun.pdf",
            Kind = NodeKind.File,
            BlobId = blob.Id,
            Size = 5,
            MediaType = "application/pdf",
            CreatedAt = T0,
            ModifiedAt = T0.AddMilliseconds(1),
            UpdatedAt = T0,
            CreatedByUserId = user.Id,
        };
        var trashed = new Node
        {
            LibraryId = library.Id,
            ParentId = root.Id,
            Name = "old.txt",
            NameKey = "old.txt",
            Kind = NodeKind.File,
            BlobId = blob.Id,
            Size = 5,
            CreatedAt = T0,
            ModifiedAt = T0,
            UpdatedAt = T0,
            TrashedAt = T0,
        };
        db.AddRange(file, trashed);
        db.Add(new NodeVersion { NodeId = file.Id, BlobId = older.Id, Size = 3, ModifiedAt = T0, CreatedAt = T0, CreatedByUserId = user.Id });

        var parentJob = new Job { Type = "parent", State = JobState.Running, Priority = JobPriority.Idle, CreatedAt = T0, RunAfter = T0, DedupeKey = "k", ActiveDedupeKey = "k" };
        db.Add(parentJob);
        db.Add(new Job { Type = "child", ParentJobId = parentJob.Id, OwnerUserId = user.Id, Payload = "{\"a\":1}", Progress = 0.25, CreatedAt = T0, RunAfter = T0 });
        db.Add(new Setting { Key = "files.default_storage_location", Value = JsonSerializer.Serialize(location.Id), UpdatedAt = T0 });
        await db.SaveChangesAsync();
    }

    private async Task<(MemoryStream Backup, BackupManifest Manifest)> BackupAsync()
    {
        await using var source = CreateSource();
        var stream = new MemoryStream();
        var manifest = await PortableBackup.WriteAsync(source, Dialect, stream, "test", "1.0", T0, CancellationToken.None);
        stream.Position = 0;
        return (stream, manifest);
    }

    [Fact]
    public async Task Everything_backed_up_is_restored_row_for_row()
    {
        await using (var source = CreateSource())
        {
            await PopulateAsync(source);
        }

        var (backup, manifest) = await BackupAsync();
        await using var target = await CreateEmptyTargetAsync("restored");

        var rows = await PortableBackup.RestoreAsync(target, Dialect, backup, CancellationToken.None);

        Assert.Equal(manifest.Tables.Sum(t => t.Rows), rows);
        await using var original = CreateSource();
        await AssertSameAsync(original, target);
    }

    [Fact]
    public async Task A_damaged_backup_is_refused()
    {
        await using (var source = CreateSource())
        {
            await PopulateAsync(source);
        }

        var (backup, _) = await BackupAsync();
        var damaged = new MemoryStream();
        using (var original = new ZipArchive(backup, ZipArchiveMode.Read, leaveOpen: true))
        await using (var copy = new ZipArchive(damaged, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in original.Entries)
            {
                var bytes = new MemoryStream();
                await using (var read = entry.Open())
                {
                    await read.CopyToAsync(bytes);
                }

                var data = bytes.ToArray();
                if (entry.FullName == "tables/Nodes.jsonl")
                {
                    data[10] ^= 0x01;
                }

                await using var write = copy.CreateEntry(entry.FullName).Open();
                await write.WriteAsync(data);
            }
        }

        damaged.Position = 0;
        await Assert.ThrowsAsync<BackupInvalidException>(() => PortableBackup.VerifyAsync(damaged, CancellationToken.None));
        damaged.Position = 0;
        await using var target = await CreateEmptyTargetAsync("damaged");
        await Assert.ThrowsAsync<BackupInvalidException>(() => PortableBackup.RestoreAsync(target, Dialect, damaged, CancellationToken.None));
        Assert.Empty(await target.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task A_restore_never_mixes_into_a_database_that_holds_data()
    {
        var (backup, _) = await BackupAsync();
        await using var source = CreateSource();

        await Assert.ThrowsAsync<BackupInvalidException>(() => PortableBackup.RestoreAsync(source, Dialect, backup, CancellationToken.None));
    }

    [Fact]
    public async Task A_backup_from_an_unknown_schema_is_refused()
    {
        var (backup, manifest) = await BackupAsync();
        var edited = new MemoryStream();
        using (var original = new ZipArchive(backup, ZipArchiveMode.Read, leaveOpen: true))
        await using (var copy = new ZipArchive(edited, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in original.Entries)
            {
                await using var read = entry.Open();
                await using var write = copy.CreateEntry(entry.FullName).Open();
                if (entry.FullName == PortableBackup.ManifestEntry)
                {
                    await JsonSerializer.SerializeAsync(write, manifest with { Migration = "99990101000000_FromTheFuture" }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                }
                else
                {
                    await read.CopyToAsync(write);
                }
            }
        }

        edited.Position = 0;
        await using var target = await CreateEmptyTargetAsync("future");
        var error = await Assert.ThrowsAsync<BackupInvalidException>(() => PortableBackup.RestoreAsync(target, Dialect, edited, CancellationToken.None));
        Assert.Contains("FromTheFuture", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Compares every table through the model, so keys, enums, timestamps and bytes all have to match.</summary>
    protected static async Task AssertSameAsync(AtriumDbContext expected, AtriumDbContext actual)
    {
        static async Task Same<T>(IQueryable<T> a, IQueryable<T> b, Func<T, object> key, Func<T, string> render)
            where T : class
        {
            var left = (await a.AsNoTracking().ToListAsync()).OrderBy(key).Select(render).ToList();
            var right = (await b.AsNoTracking().ToListAsync()).OrderBy(key).Select(render).ToList();
            Assert.Equal(left, right);
        }

        await Same(expected.Users, actual.Users, u => u.Id, u => $"{u.Id}|{u.Name}|{u.DisplayName}|{u.Role}|{u.QuotaBytes}|{u.CreatedAt:O}|{u.Version}");
        await Same(expected.Sessions, actual.Sessions, s => s.Id, s => $"{s.Id}|{s.UserId}|{s.DeviceKind}|{s.ReadOnly}|{s.LastSeenAt:O}");
        await Same(expected.StorageLocations, actual.StorageLocations, l => l.Id, l => $"{l.Id}|{l.Kind}|{Convert.ToHexString(l.EncryptedConfig)}|{l.State}");
        await Same(expected.Libraries, actual.Libraries, l => l.Id, l => $"{l.Id}|{l.OwnerUserId}|{l.RootNodeId}|{l.StorageLocationId}");
        await Same(expected.Nodes, actual.Nodes, n => n.Id, n => $"{n.Id}|{n.ParentId}|{n.Name}|{n.ActiveNameKey}|{n.Kind}|{n.BlobId}|{n.Size}|{n.ModifiedAt:O}|{n.TrashedAt:O}");
        await Same(expected.Blobs, actual.Blobs, b => b.Id, b => $"{b.Id}|{Convert.ToHexString(b.Sha256)}|{b.Size}|{b.State}|{b.PendingDeleteSince:O}");
        await Same(expected.NodeVersions, actual.NodeVersions, v => v.Id, v => $"{v.Id}|{v.NodeId}|{v.BlobId}|{v.Size}");
        await Same(expected.Jobs, actual.Jobs, j => j.Id, j => $"{j.Id}|{j.ParentJobId}|{j.Priority}|{j.State}|{j.Payload}|{j.Progress}|{j.ActiveDedupeKey}");
        await Same(expected.Settings, actual.Settings, s => s.Key, s => $"{s.Key}|{s.Value}");

        // Lookups by key work after the restore, which also proves keys kept their stored form.
        var nodeId = (await expected.Nodes.AsNoTracking().FirstAsync(n => n.Kind == NodeKind.File)).Id;
        Assert.NotNull(await actual.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == nodeId));
    }
}

public sealed class SqliteBackupTests : BackupContractTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    protected override IBackupDialect Dialect { get; } = new SqliteBackupDialect();

    protected override AtriumDbContext CreateSource() => Open("source");

    protected override Task<AtriumDbContext> CreateEmptyTargetAsync(string name) => Task.FromResult(Open(name));

    protected override Task StopAsync()
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

    private AtriumDbContext Open(string name)
    {
        Directory.CreateDirectory(_dir);
        return new AtriumDbContext(
            (DbContextOptions<AtriumDbContext>)new DbContextOptionsBuilder<AtriumDbContext>()
                .UseAtriumSqlite(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_dir, name + ".db"), Pooling = false }.ToString())
                .Options);
    }
}

/// <summary>Needs Docker, so it runs in CI (D-021).</summary>
[Trait("Category", "Containers")]
public sealed class PostgresBackupTests : BackupContractTests
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly string _sqliteDir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    protected override IBackupDialect Dialect { get; } = new PostgresBackupDialect();

    protected override Task StartAsync() => _container.StartAsync();

    protected override async Task StopAsync()
    {
        await _container.DisposeAsync();
        try
        {
            Directory.Delete(_sqliteDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    protected override AtriumDbContext CreateSource() => Open(_container.GetConnectionString());

    protected override async Task<AtriumDbContext> CreateEmptyTargetAsync(string name)
    {
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{name}\"";
            await create.ExecuteNonQueryAsync();
        }

        return Open(new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ToString());
    }

    /// <summary>The supported way between the two databases (DESIGN.md 8.2).</summary>
    [Fact]
    public async Task A_postgres_backup_restores_into_sqlite()
    {
        await using (var source = CreateSource())
        {
            await PopulateAsync(source);
        }

        var stream = new MemoryStream();
        await using (var source = CreateSource())
        {
            await PortableBackup.WriteAsync(source, Dialect, stream, "test", "1.0", DateTimeOffset.UtcNow, CancellationToken.None);
        }

        stream.Position = 0;
        Directory.CreateDirectory(_sqliteDir);
        await using var target = new AtriumDbContext(
            (DbContextOptions<AtriumDbContext>)new DbContextOptionsBuilder<AtriumDbContext>()
                .UseAtriumSqlite(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_sqliteDir, "from-postgres.db"), Pooling = false }.ToString())
                .Options);

        await PortableBackup.RestoreAsync(target, new SqliteBackupDialect(), stream, CancellationToken.None);

        await using var original = CreateSource();
        await AssertSameAsync(original, target);
    }

    private static AtriumDbContext Open(string connectionString) =>
        new((DbContextOptions<AtriumDbContext>)new DbContextOptionsBuilder<AtriumDbContext>()
            .UseAtriumPostgres(connectionString)
            .Options);
}
