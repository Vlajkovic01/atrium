using Atrium.Core;
using Atrium.Core.Files;
using Atrium.Core.Identity;
using Atrium.Core.Jobs;
using Atrium.Core.Media;
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
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Library> Libraries => Set<Library>();
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<Blob> Blobs => Set<Blob>();
    public DbSet<NodeVersion> NodeVersions => Set<NodeVersion>();
    public DbSet<Upload> Uploads => Set<Upload>();
    public DbSet<LibraryGrant> LibraryGrants => Set<LibraryGrant>();
    public DbSet<MediaInfo> MediaInfos => Set<MediaInfo>();
    public DbSet<Album> Albums => Set<Album>();
    public DbSet<AlbumItem> AlbumItems => Set<AlbumItem>();

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
        configurationBuilder.Properties<JobState>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<JobResourceClass>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<LibraryKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<LibraryMode>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<NodeKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<BlobState>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<UploadState>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<LibraryState>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<LibraryAccess>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<MediaKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ThumbnailState>().HaveConversion<string>().HaveMaxLength(32);

        // The one enum stored by number: workers order the queue by it.
        configurationBuilder.Properties<JobPriority>().HaveConversion<int>();
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

        modelBuilder.Entity<Job>(e =>
        {
            e.Property(j => j.Type).HasMaxLength(64);
            e.Property(j => j.DedupeKey).HasMaxLength(256);
            e.Property(j => j.ActiveDedupeKey).HasMaxLength(256);
            e.HasIndex(j => j.ActiveDedupeKey).IsUnique();
            e.Property(j => j.LeaseOwner).HasMaxLength(64);
            e.Property(j => j.ErrorCode).HasMaxLength(128);
            e.Property(j => j.ErrorDetail).HasMaxLength(4000);
            e.Ignore(j => j.IsActive);
            e.HasOne<User>().WithMany().HasForeignKey(j => j.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Job>().WithMany().HasForeignKey(j => j.ParentJobId).OnDelete(DeleteBehavior.Cascade);

            // The claim query: the next queued job by priority and time.
            e.HasIndex(j => new { j.State, j.Priority, j.RunAfter });
            e.HasIndex(j => new { j.Type, j.State });
        });

        modelBuilder.Entity<Library>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(128);
            e.Property(l => l.Version).IsConcurrencyToken();
            e.HasOne<StorageLocation>().WithMany().HasForeignKey(l => l.StorageLocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(l => l.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(l => l.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<User>().WithMany().HasForeignKey(l => l.WritesEnabledByUserId).OnDelete(DeleteBehavior.SetNull);
            e.Property(l => l.ExternalPath).HasMaxLength(4096);
            e.Property(l => l.StateReason).HasMaxLength(128);

            // At most one personal library per user.
            e.HasIndex(l => l.OwnerUserId).IsUnique();
        });

        modelBuilder.Entity<LibraryGrant>(e =>
        {
            e.HasKey(g => new { g.LibraryId, g.UserId });
            e.HasOne<Library>().WithMany().HasForeignKey(g => g.LibraryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(g => g.UserId);
        });

        modelBuilder.Entity<Node>(e =>
        {
            e.Property(n => n.Name).HasMaxLength(NodeName.MaxLength);
            e.Property(n => n.NameKey).HasMaxLength(NodeName.MaxLength * 2);
            e.Property(n => n.ActiveNameKey).HasMaxLength(NodeName.MaxLength * 2);
            e.Property(n => n.MediaType).HasMaxLength(128);
            e.Property(n => n.Version).IsConcurrencyToken();
            e.Ignore(n => n.IsFolder);
            e.HasOne<Library>().WithMany().HasForeignKey(n => n.LibraryId).OnDelete(DeleteBehavior.Cascade);

            // Removing a folder row removes everything below it. Rows are only
            // removed when the trash is emptied or its retention ends; content
            // is not touched, blobs are collected separately (DESIGN.md 4.3).
            e.HasOne<Node>().WithMany().HasForeignKey(n => n.ParentId).OnDelete(DeleteBehavior.Cascade);

            // A blob is never deleted while a node points at it.
            e.HasOne<Blob>().WithMany().HasForeignKey(n => n.BlobId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(n => n.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);

            // Siblings that are not in the trash have distinct names (D-011).
            e.HasIndex(n => new { n.ParentId, n.ActiveNameKey }).IsUnique();
            e.HasIndex(n => new { n.LibraryId, n.TrashedAt });

            // An external file is found again by its path at every scan.
            e.Property(n => n.ExternalPath).HasMaxLength(4096);
            e.Property(n => n.ExternalPathHash).HasMaxLength(32);
            e.HasIndex(n => new { n.LibraryId, n.ExternalPathHash }).IsUnique();

            // The timeline: photos and videos of some libraries, newest first.
            e.Property(n => n.SearchKey).HasMaxLength(NodeName.MaxLength * 2);
            e.HasIndex(n => new { n.LibraryId, n.MediaKind, n.CapturedAt });
        });

        modelBuilder.Entity<MediaInfo>(e =>
        {
            // Facts about content go when the content goes.
            e.HasOne<Blob>().WithMany().HasForeignKey(m => m.BlobId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Node>().WithMany().HasForeignKey(m => m.NodeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => m.BlobId).IsUnique();
            e.HasIndex(m => m.NodeId).IsUnique();
            e.Property(m => m.Format).HasMaxLength(64);
            e.Property(m => m.VideoCodec).HasMaxLength(64);
            e.Property(m => m.AudioCodec).HasMaxLength(64);
            e.Property(m => m.CameraMake).HasMaxLength(128);
            e.Property(m => m.CameraModel).HasMaxLength(128);
            e.Property(m => m.ErrorCode).HasMaxLength(128);
            e.ToTable(t => t.HasCheckConstraint(
                "CK_MediaInfos_OneSource",
                "(\"BlobId\" IS NULL) <> (\"NodeId\" IS NULL)"));
        });

        modelBuilder.Entity<Album>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(NodeName.MaxLength);
            e.Property(a => a.Version).IsConcurrencyToken();
            e.HasOne<User>().WithMany().HasForeignKey(a => a.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Node>().WithMany().HasForeignKey(a => a.CoverNodeId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(a => a.OwnerUserId);
        });

        modelBuilder.Entity<AlbumItem>(e =>
        {
            e.HasKey(i => new { i.AlbumId, i.NodeId });
            e.HasOne<Album>().WithMany().HasForeignKey(i => i.AlbumId).OnDelete(DeleteBehavior.Cascade);

            // An item removed from the trash for good leaves every album.
            e.HasOne<Node>().WithMany().HasForeignKey(i => i.NodeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(i => i.NodeId);
        });

        modelBuilder.Entity<Blob>(e =>
        {
            e.Property(b => b.Sha256).HasMaxLength(32);
            e.HasIndex(b => new { b.StorageLocationId, b.Sha256 }).IsUnique();
            e.HasIndex(b => new { b.State, b.PendingDeleteSince });
            e.Ignore(b => b.Key);
            e.HasOne<StorageLocation>().WithMany().HasForeignKey(b => b.StorageLocationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NodeVersion>(e =>
        {
            e.HasOne<Node>().WithMany().HasForeignKey(v => v.NodeId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Blob>().WithMany().HasForeignKey(v => v.BlobId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(v => v.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(v => new { v.NodeId, v.CreatedAt });
        });

        modelBuilder.Entity<Upload>(e =>
        {
            e.Property(u => u.Name).HasMaxLength(NodeName.MaxLength);
            e.Property(u => u.MediaType).HasMaxLength(128);
            e.Property(u => u.ConflictPolicy).HasMaxLength(32);
            e.Property(u => u.ExpectedSha256).HasMaxLength(32);
            e.Property(u => u.StagingKey).HasMaxLength(256);
            e.Property(u => u.MultipartUploadId).HasMaxLength(1024);
            e.Property(u => u.ErrorCode).HasMaxLength(128);
            e.Property(u => u.Version).IsConcurrencyToken();
            e.HasOne<User>().WithMany().HasForeignKey(u => u.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Library>().WithMany().HasForeignKey(u => u.LibraryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<StorageLocation>().WithMany().HasForeignKey(u => u.StorageLocationId).OnDelete(DeleteBehavior.Restrict);

            // Not a foreign key: the target folder may be trashed and removed
            // while the upload runs; finishing then reports it.
            e.HasIndex(u => new { u.UserId, u.State });
            e.HasIndex(u => new { u.State, u.ExpiresAt });
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BumpVersions();
        DeriveNodeFields();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BumpVersions();
        DeriveNodeFields();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // Fields that follow from others are set here, in one place, so no code
    // path that saves a node can forget them. Bulk updates bypass this and
    // must set them themselves.
    private void DeriveNodeFields()
    {
        foreach (var entry in ChangeTracker.Entries<Node>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var node = entry.Entity;
            var added = entry.State == EntityState.Added;
            if (added || entry.Property(n => n.Name).IsModified)
            {
                node.SearchKey = NodeName.SearchKeyOf(node.Name);
            }

            if (added || entry.Property(n => n.MediaType).IsModified)
            {
                node.MediaKind = node.IsFolder ? null : MediaKinds.FromMediaType(node.MediaType);
            }

            // New content is dated anew once its metadata is read.
            if (!added && entry.Property(n => n.BlobId).IsModified)
            {
                node.CapturedAt = null;
            }

            // Until metadata is read, a photo sorts by its modification time.
            if (node.MediaKind is null)
            {
                node.CapturedAt = null;
            }
            else if (node.CapturedAt is null)
            {
                node.CapturedAt = new DateTimeOffset(node.ModifiedAt.UtcDateTime, TimeSpan.Zero);
            }
        }
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
