using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Core.Storage;
using Atrium.Data;
using Atrium.Data.Postgres;
using Atrium.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Atrium.Files.Tests;

/// <summary>A clock the tests move by hand.</summary>
public sealed class MutableTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>One real database for a test class, with helpers for the rows file tests need.</summary>
public abstract class FileTestDatabase : IAsyncLifetime
{
    public MutableTime Time { get; } = new();

    public abstract void Use(DbContextOptionsBuilder builder);

    public AtriumDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<AtriumDbContext>();
        Use(builder);
        return new AtriumDbContext(builder.Options);
    }

    public virtual Task StartAsync() => Task.CompletedTask;

    public virtual Task StopAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        await StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => StopAsync();

    public async Task<User> AddUserAsync(string name, UserRole role = UserRole.User)
    {
        var user = new User
        {
            Name = name,
            NameKey = UserName.KeyOf(name),
            DisplayName = name,
            PasswordHash = "not-a-real-hash",
            Role = role,
            CreatedAt = Time.GetUtcNow(),
            PasswordChangedAt = Time.GetUtcNow(),
        };
        await using var db = CreateContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task<StorageLocation> AddLocationAsync(string name)
    {
        var location = new StorageLocation
        {
            Name = name,
            Kind = StorageProviderKind.LocalFileSystem,
            EncryptedConfig = [1, 2, 3],
            State = StorageLocationState.Online,
            CreatedAt = Time.GetUtcNow(),
        };
        await using var db = CreateContext();
        db.StorageLocations.Add(location);
        await db.SaveChangesAsync();
        Time.Advance(TimeSpan.FromSeconds(1));
        return location;
    }

    public Node NewNode(Library library, Guid parentId, string name, NodeKind kind = NodeKind.Folder, Guid? blobId = null)
    {
        Assert.True(NodeName.TryNormalize(name, out var normalized, out var key));
        return new Node
        {
            LibraryId = library.Id,
            ParentId = parentId,
            Name = normalized,
            NameKey = key,
            ActiveNameKey = key,
            Kind = kind,
            BlobId = blobId,
            ModifiedAt = Time.GetUtcNow(),
            CreatedAt = Time.GetUtcNow(),
            UpdatedAt = Time.GetUtcNow(),
        };
    }
}

public sealed class SqliteFileDatabase : FileTestDatabase
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    public override void Use(DbContextOptionsBuilder builder)
    {
        Directory.CreateDirectory(_dir);
        builder.UseAtriumSqlite(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dir, "atrium.db"),
            Pooling = false,
        }.ToString());
    }

    public override Task StopAsync()
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
}

public sealed class PostgresFileDatabase : FileTestDatabase
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public override Task StartAsync() => _container.StartAsync();

    public override Task StopAsync() => _container.DisposeAsync().AsTask();

    public override void Use(DbContextOptionsBuilder builder) =>
        builder.UseAtriumPostgres(_container.GetConnectionString());
}
