using Atrium.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Data.Tests;

/// <summary>
/// Behavior every supported database must show. Each provider runs the same
/// tests against a real database, so a difference between SQLite and
/// PostgreSQL shows up here and not in production.
/// </summary>
public abstract class DatabaseContractTests : IAsyncLifetime
{
    protected abstract AtriumDbContext CreateContext();

    protected virtual Task StartDatabaseAsync() => Task.CompletedTask;

    protected virtual Task StopDatabaseAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        await StartDatabaseAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => StopDatabaseAsync();

    protected static readonly DateTimeOffset T0 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    protected static User NewUser(string name, UserRole role = UserRole.User) => new()
    {
        Name = name,
        NameKey = UserName.KeyOf(name),
        DisplayName = name,
        PasswordHash = "not-a-real-hash",
        Role = role,
        CreatedAt = T0,
        PasswordChangedAt = T0,
    };

    private static Session NewSession(Guid userId, DateTimeOffset lastSeen) => new()
    {
        UserId = userId,
        DeviceName = "Test device",
        DeviceKind = DeviceKind.Web,
        CreatedAt = T0,
        LastSeenAt = lastSeen,
    };

    [Fact]
    public async Task Migrating_an_up_to_date_database_changes_nothing()
    {
        await using var db = CreateContext();

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task A_user_round_trips_with_all_fields()
    {
        var user = NewUser("Ana", UserRole.Admin);
        user.QuotaBytes = 5L * 1024 * 1024 * 1024 * 1024;
        user.Locale = "sr-Cyrl";
        user.PasswordChangedAt = T0.AddMilliseconds(123);

        await using (var db = CreateContext())
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            var loaded = await db.Users.SingleAsync(u => u.Id == user.Id);
            Assert.Equal("Ana", loaded.Name);
            Assert.Equal("ana", loaded.NameKey);
            Assert.Equal(UserRole.Admin, loaded.Role);
            Assert.Equal(user.QuotaBytes, loaded.QuotaBytes);
            Assert.Equal("sr-Cyrl", loaded.Locale);
            Assert.Equal(T0, loaded.CreatedAt);
            Assert.Equal(T0.AddMilliseconds(123), loaded.PasswordChangedAt);
            Assert.False(loaded.Disabled);
        }
    }

    [Fact]
    public async Task Timestamps_are_filtered_and_sorted_by_the_database()
    {
        var user = NewUser("Ana");
        await using (var db = CreateContext())
        {
            db.Users.Add(user);
            db.Sessions.AddRange(
                NewSession(user.Id, T0.AddHours(1)),
                NewSession(user.Id, T0.AddHours(3)),
                NewSession(user.Id, T0.AddHours(2)),
                NewSession(user.Id, T0.AddHours(-5)));
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            var recent = await db.Sessions
                .Where(s => s.LastSeenAt > T0)
                .OrderByDescending(s => s.LastSeenAt)
                .Select(s => s.LastSeenAt)
                .ToListAsync();

            Assert.Equal([T0.AddHours(3), T0.AddHours(2), T0.AddHours(1)], recent);
        }
    }

    [Fact]
    public async Task Two_users_cannot_have_the_same_name_key()
    {
        await using var db = CreateContext();
        db.Users.Add(NewUser("Ana"));
        await db.SaveChangesAsync();

        db.Users.Add(NewUser("ANA"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task An_update_based_on_stale_data_is_rejected()
    {
        var user = NewUser("Ana");
        await using (var db = CreateContext())
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        await using var first = CreateContext();
        await using var second = CreateContext();
        var a = await first.Users.SingleAsync(u => u.Id == user.Id);
        var b = await second.Users.SingleAsync(u => u.Id == user.Id);

        a.DisplayName = "Ana A.";
        await first.SaveChangesAsync();

        b.DisplayName = "Ana B.";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var check = CreateContext();
        var stored = await check.Users.SingleAsync(u => u.Id == user.Id);
        Assert.Equal("Ana A.", stored.DisplayName);
        Assert.Equal(1, stored.Version);
    }

    [Fact]
    public async Task Deleting_a_user_removes_their_sessions_and_tokens()
    {
        var user = NewUser("Ana");
        var session = NewSession(user.Id, T0);
        await using (var db = CreateContext())
        {
            db.Users.Add(user);
            db.Sessions.Add(session);
            db.RefreshTokens.Add(new RefreshToken
            {
                SessionId = session.Id,
                TokenHash = new byte[32],
                IssuedAt = T0,
                ExpiresAt = T0.AddDays(30),
            });
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            // Delete without loading children, so the database itself must cascade.
            await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();
        }

        await using (var db = CreateContext())
        {
            Assert.False(await db.Sessions.AnyAsync());
            Assert.False(await db.RefreshTokens.AnyAsync());
        }
    }

    [Fact]
    public async Task A_refresh_token_hash_can_exist_only_once()
    {
        var user = NewUser("Ana");
        var session = NewSession(user.Id, T0);
        var hash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        await using var db = CreateContext();
        db.Users.Add(user);
        db.Sessions.Add(session);
        db.RefreshTokens.Add(new RefreshToken { SessionId = session.Id, TokenHash = hash, IssuedAt = T0, ExpiresAt = T0 });
        await db.SaveChangesAsync();

        db.RefreshTokens.Add(new RefreshToken { SessionId = session.Id, TokenHash = hash, IssuedAt = T0, ExpiresAt = T0 });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Enums_are_stored_by_name()
    {
        await using var db = CreateContext();
        db.Users.Add(NewUser("Ana", UserRole.Guest));
        await db.SaveChangesAsync();

        var stored = await db.Database
            .SqlQueryRaw<string>("SELECT \"Role\" AS \"Value\" FROM \"Users\"")
            .SingleAsync();

        Assert.Equal("Guest", stored);
    }
}
