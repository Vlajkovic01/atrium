using Atrium.Core;
using Atrium.Core.Identity;
using Atrium.Core.Settings;
using Atrium.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Atrium.Data;

public sealed class AtriumDbContext(DbContextOptions<AtriumDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PairingRequest> PairingRequests => Set<PairingRequest>();
    public DbSet<LinkCode> LinkCodes => Set<LinkCode>();
    public DbSet<TotpCredential> TotpCredentials => Set<TotpCredential>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<StorageLocation> StorageLocations => Set<StorageLocation>();
    public DbSet<Setting> Settings => Set<Setting>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Timestamps are stored as UTC milliseconds since the Unix epoch on
        // both databases (D-020). SQLite cannot compare or sort its native
        // DateTimeOffset text form, and one representation keeps the schema
        // and the portable backup identical across providers.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UnixMillisecondsConverter>();

        // Enums are stored by name so that reordering or inserting a member
        // can never change the meaning of existing rows.
        configurationBuilder.Properties<UserRole>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<DeviceKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<PairingState>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<StorageProviderKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<StorageLocationState>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.Property(u => u.Name).HasMaxLength(UserName.MaxLength);
            e.Property(u => u.NameKey).HasMaxLength(UserName.MaxLength);
            e.HasIndex(u => u.NameKey).IsUnique();
            e.Property(u => u.DisplayName).HasMaxLength(128);
            e.Property(u => u.PasswordHash).HasMaxLength(512);
            e.Property(u => u.Locale).HasMaxLength(16);
            e.Property(u => u.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<Session>(e =>
        {
            e.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(s => s.DeviceName).HasMaxLength(128);
            e.Property(s => s.Platform).HasMaxLength(64);
            e.Property(s => s.AppVersion).HasMaxLength(32);
            e.Property(s => s.LastIp).HasMaxLength(64);
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.HasOne<Session>().WithMany().HasForeignKey(t => t.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(t => t.TokenHash).HasMaxLength(32);
            e.HasIndex(t => t.TokenHash).IsUnique();

            // Two requests presenting the same token at the same moment must
            // not both rotate it: the second sees that UsedAt changed and fails.
            e.Property(t => t.UsedAt).IsConcurrencyToken();
        });

        modelBuilder.Entity<PairingRequest>(e =>
        {
            e.Property(p => p.DeviceCodeHash).HasMaxLength(32);
            e.HasIndex(p => p.DeviceCodeHash).IsUnique();
            e.Property(p => p.UserCode).HasMaxLength(16);
            e.HasIndex(p => p.UserCode).IsUnique();
            e.Property(p => p.DeviceName).HasMaxLength(128);
            e.Property(p => p.Platform).HasMaxLength(64);
            e.Property(p => p.AppVersion).HasMaxLength(32);
            e.Property(p => p.RequestIp).HasMaxLength(64);
            e.HasOne<User>().WithMany().HasForeignKey(p => p.ApprovedByUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Session>().WithMany().HasForeignKey(p => p.SessionId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(p => p.ExpiresAt);

            // A request changes state once per step (approve, deny, redeem).
            // Two requests racing for the same step must not both succeed.
            e.Property(p => p.State).IsConcurrencyToken();
        });

        modelBuilder.Entity<LinkCode>(e =>
        {
            e.Property(c => c.CodeHash).HasMaxLength(32);
            e.HasIndex(c => c.CodeHash).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(c => c.UsedAt).IsConcurrencyToken();
        });

        modelBuilder.Entity<TotpCredential>(e =>
        {
            e.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(t => t.UserId).IsUnique();
            e.Property(t => t.LastUsedStep).IsConcurrencyToken();
        });

        modelBuilder.Entity<RecoveryCode>(e =>
        {
            e.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(c => c.CodeHash).HasMaxLength(32);
        });

        modelBuilder.Entity<StorageLocation>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(128);
            e.HasIndex(l => l.Name).IsUnique();
            e.Property(l => l.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<Setting>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(128);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BumpVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BumpVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // The update's WHERE clause still carries the version that was read, so a
    // concurrent change makes this save fail instead of overwriting it.
    private void BumpVersions()
    {
        foreach (var entry in ChangeTracker.Entries<IVersioned>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.Version++;
            }
        }
    }

    private sealed class UnixMillisecondsConverter() : ValueConverter<DateTimeOffset, long>(
        v => v.ToUnixTimeMilliseconds(),
        v => DateTimeOffset.FromUnixTimeMilliseconds(v));
}
