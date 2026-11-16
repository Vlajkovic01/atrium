using Atrium.Core.Files;
using Atrium.Core.Media;
using Atrium.Data;
using Atrium.Files;
using Atrium.Files.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atrium.Media;

/// <summary>Error codes recorded on media that could not be processed.</summary>
public static class MediaErrors
{
    public const string Unsupported = "media.unsupported";
    public const string ToolMissing = "media.tool_missing";
    public const string NoThumbnail = "media.no_thumbnail";
}

/// <summary>
/// Reads a photo's or video's metadata and makes its grid thumbnail
/// (DESIGN.md 7.1). Facts belong to the content: one blob shared by
/// several files is read once.
/// </summary>
public sealed class MediaService(
    AtriumDbContext db,
    MediaFiles files,
    MetadataReader reader,
    Thumbnailer thumbnails,
    TimeProvider time,
    ILogger<MediaService> logger)
{
    /// <summary>Makes sure a node's facts are read and its small thumbnail exists. Safe to run again.</summary>
    public async Task ProcessAsync(Guid nodeId, bool background, CancellationToken ct)
    {
        var node = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(n => n.Id == nodeId, ct);
        if (node is not { MediaKind: { } kind, MissingSince: null } || node.IsFolder)
        {
            return;
        }

        var library = await db.Libraries.AsNoTracking().SingleAsync(l => l.Id == node.LibraryId, ct);
        LocalMediaFile file;
        try
        {
            file = await files.OpenAsync(node, library, ct);
        }
        catch (ContentUnavailableException)
        {
            // Gone since it was queued; the next scan notices.
            return;
        }

        await using (file)
        {
            var info = await FindInfoAsync(node, ct) ?? NewInfo(node, kind);
            var stale = info.ExtractorVersion < MediaInfo.CurrentExtractorVersion
                || info.SourceSize != file.Source.Size
                || info.SourceModifiedAt != file.Source.ModifiedAt;
            if (stale)
            {
                await ReadFactsAsync(info, file, kind, node.ModifiedAt, background, ct);
                await ApplyCaptureTimeAsync(node, info, ct);
            }

            if (info.ThumbnailState == ThumbnailState.Pending || stale)
            {
                info.ThumbnailState = await MakeAsync(file, kind, ThumbnailVariant.Small, background, ct) is { } error
                    ? (error == MediaErrors.Unsupported ? ThumbnailState.Unsupported : ThumbnailState.Failed)
                    : ThumbnailState.Ready;
                info.ErrorCode ??= info.ThumbnailState == ThumbnailState.Ready ? null : MediaErrors.Unsupported;
            }

            await SaveAsync(info, ct);
        }
    }

    /// <summary>
    /// The path of a thumbnail, made now if it does not exist yet. Null when
    /// this file cannot have one.
    /// </summary>
    public async Task<string?> ThumbnailAsync(Node node, Library library, ThumbnailVariant variant, bool background, CancellationToken ct)
    {
        if (node.MediaKind is not { } kind)
        {
            return null;
        }

        LocalMediaFile file;
        try
        {
            file = await files.OpenAsync(node, library, ct);
        }
        catch (ContentUnavailableException)
        {
            return null;
        }

        await using (file)
        {
            var path = thumbnails.PathFor(file.Source.Key, variant);
            if (File.Exists(path))
            {
                return path;
            }

            // Content known to be undecodable is not tried again for every request.
            var info = await FindInfoAsync(node, ct);
            if (info?.ThumbnailState == ThumbnailState.Unsupported && info.SourceSize == file.Source.Size && variant != ThumbnailVariant.Preview)
            {
                return null;
            }

            return await MakeAsync(file, kind, variant, background, ct) is null ? path : null;
        }
    }

    /// <summary>Makes one variant; returns null on success or the error code.</summary>
    private async Task<string?> MakeAsync(LocalMediaFile file, MediaKind kind, ThumbnailVariant variant, bool background, CancellationToken ct)
    {
        try
        {
            await thumbnails.MakeAsync(file.Path, kind, file.Source.Key, variant, background, ct);
            return null;
        }
        catch (UnsupportedMediaException e)
        {
            logger.LogInformation("No {Variant} thumbnail for content {Key}: {Reason}", variant, file.Source.Key, e.Message);
            return MediaErrors.Unsupported;
        }
        catch (MediaToolException e) when (e.Missing)
        {
            logger.LogWarning("{Message} Videos get no thumbnails until it is installed.", e.Message);
            return MediaErrors.ToolMissing;
        }
    }

    private async Task ReadFactsAsync(MediaInfo info, LocalMediaFile file, MediaKind kind, DateTimeOffset modifiedAt, bool background, CancellationToken ct)
    {
        MediaFacts facts;
        try
        {
            facts = await reader.ReadAsync(file.Path, kind, background, ct);
            info.ErrorCode = null;
        }
        catch (MediaToolException e)
        {
            // Without facts the file still shows, dated by its modification time.
            facts = new MediaFacts(kind, null, null, null, null, null, null, null, null, null, null, null, null);
            info.ErrorCode = e.Missing ? MediaErrors.ToolMissing : MediaErrors.Unsupported;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException)
        {
            facts = new MediaFacts(kind, null, null, null, null, null, null, null, null, null, null, null, null);
            info.ErrorCode = MediaErrors.Unsupported;
        }

        info.Kind = facts.Kind;
        info.Width = facts.Width;
        info.Height = facts.Height;
        info.DurationMs = facts.DurationMs;
        info.Format = facts.Format;
        info.VideoCodec = facts.VideoCodec;
        info.AudioCodec = facts.AudioCodec;
        info.CapturedAt = reader.CapturedAt(facts, modifiedAt);
        info.CapturedOffsetMinutes = facts.OffsetMinutes;
        info.CameraMake = facts.CameraMake;
        info.CameraModel = facts.CameraModel;
        info.Orientation = facts.Orientation;
        info.SourceSize = file.Source.Size;
        info.SourceModifiedAt = file.Source.ModifiedAt;
        info.ExtractorVersion = MediaInfo.CurrentExtractorVersion;
        info.ExtractedAt = time.GetUtcNow();
    }

    /// <summary>Every file with this content moves to its place on the timeline.</summary>
    private async Task ApplyCaptureTimeAsync(Node node, MediaInfo info, CancellationToken ct)
    {
        var captured = info.CapturedAt;
        var query = node.BlobId is { } blobId && info.BlobId is not null
            ? db.Nodes.Where(n => n.BlobId == blobId && n.MediaKind != null)
            : db.Nodes.Where(n => n.Id == node.Id);
        await query.ExecuteUpdateAsync(s => s.SetProperty(n => n.CapturedAt, captured), ct);
    }

    private Task<MediaInfo?> FindInfoAsync(Node node, CancellationToken ct) =>
        node.BlobId is { } blobId
            ? db.MediaInfos.SingleOrDefaultAsync(m => m.BlobId == blobId, ct)
            : db.MediaInfos.SingleOrDefaultAsync(m => m.NodeId == node.Id, ct);

    private static MediaInfo NewInfo(Node node, MediaKind kind) => new()
    {
        BlobId = node.BlobId,
        NodeId = node.BlobId is null ? node.Id : null,
        Kind = kind,
        ThumbnailState = ThumbnailState.Pending,
    };

    private async Task SaveAsync(MediaInfo info, CancellationToken ct)
    {
        if (db.Entry(info).State == EntityState.Detached)
        {
            db.MediaInfos.Add(info);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another job read the same content at the same moment; its row stands.
            db.ChangeTracker.Clear();
        }
    }
}
