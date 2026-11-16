using System.Globalization;
using Atrium.Files;
using Atrium.Storage;
using Microsoft.Net.Http.Headers;

namespace Atrium.Api.Files;

/// <summary>
/// Serves file content with range requests, a content-based ETag and
/// conditional requests, and with the headers that keep an uploaded file
/// from ever running as part of the app (DESIGN.md 6.9).
/// </summary>
public static class FileContent
{
    /// <summary>
    /// Every response carrying user content runs in a sandbox with nothing
    /// allowed: an uploaded HTML or SVG file opened directly can neither run
    /// script nor load anything, and is never same-origin with the app.
    /// </summary>
    public const string PdfPolicy = "default-src 'none'; frame-ancestors 'self'";

    public const string SandboxPolicy = "sandbox; default-src 'none'; img-src 'self' data:; media-src 'self'; style-src 'unsafe-inline'";

    /// <summary>Types that are shown in the browser when asked; everything else is always a download.</summary>
    private static readonly HashSet<string> InlineTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp", "image/avif", "image/heic", "image/bmp",
        "video/mp4", "video/webm", "video/quicktime", "audio/mpeg", "audio/mp4", "audio/ogg", "audio/wav", "audio/flac",
        "application/pdf", "text/plain",
    };

    public static string ETagFor(byte[] sha256) => $"\"{Convert.ToHexStringLower(sha256)}\"";

    public static async Task ServeAsync(
        HttpContext http,
        ContentSource source,
        string fileName,
        string? mediaType,
        bool inline,
        CancellationToken ct)
    {
        var request = http.Request;
        var response = http.Response;
        var etag = $"\"{source.Key}\"";
        var size = source.Size;
        var modifiedAt = source.ModifiedAt;
        var type = mediaType ?? "application/octet-stream";
        var showInline = inline && InlineTypes.Contains(type);

        response.Headers.ETag = etag;
        response.Headers.LastModified = modifiedAt.ToString("R", CultureInfo.InvariantCulture);
        response.Headers.AcceptRanges = "bytes";
        response.Headers.CacheControl = "private, no-cache";
        response.Headers.ContentSecurityPolicy = SandboxPolicy;
        if (showInline && type.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            // The browser's PDF viewer refuses to run in a sandboxed
            // document, and the web app shows the PDF in a frame of its own
            // (D-044). Scripts inside a PDF run in the viewer's own isolated
            // process, never in this origin; nothing else may load.
            response.Headers.ContentSecurityPolicy = PdfPolicy;
            response.Headers.XFrameOptions = "SAMEORIGIN";
        }
        response.Headers.ContentDisposition = Disposition(fileName, showInline);
        response.ContentType = showInline ? type : "application/octet-stream";

        if (request.Headers.IfNoneMatch.ToString() is { Length: > 0 } ifNoneMatch
            && ifNoneMatch.Split(',', StringSplitOptions.TrimEntries).Any(t => t == etag || t == "*"))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        var range = ParseRange(request, size, etag);
        if (range is { Satisfiable: false })
        {
            response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            response.Headers.ContentRange = $"bytes */{size.ToString(CultureInfo.InvariantCulture)}";
            response.Headers.ContentDisposition = default;
            return;
        }

        long offset = 0;
        var length = size;
        if (range is { } r)
        {
            offset = r.From;
            length = r.To - r.From + 1;
            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes {r.From}-{r.To}/{size}");
        }

        response.ContentLength = length;
        if (HttpMethods.IsHead(request.Method) || length == 0)
        {
            return;
        }

        await using var stream = await source.OpenAsync(range is null ? null : new ByteRange(offset, length), ct);
        await CopyAsync(stream, response.Body, length, ct);
    }

    /// <summary>
    /// Copies exactly <paramref name="length"/> bytes, or fewer if the source
    /// ends early. A file in an external folder can be longer than when it
    /// was measured; the response must still match its Content-Length.
    /// </summary>
    public static async Task CopyAsync(Stream source, Stream target, long length, CancellationToken ct)
    {
        var buffer = new byte[81920];
        var left = length;
        while (left > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            left -= read;
        }
    }

    /// <summary>attachment or inline, with an ASCII fallback name and the real name in UTF-8.</summary>
    public static string Disposition(string fileName, bool inline)
    {
        var header = new ContentDispositionHeaderValue(inline ? "inline" : "attachment");
        header.SetHttpFileName(fileName);
        return header.ToString();
    }

    private readonly record struct ParsedRange(long From, long To, bool Satisfiable);

    /// <summary>
    /// One byte range, or null for the whole file. Several ranges are
    /// answered with the whole file, which the standard allows, and so is a
    /// range whose If-Range no longer matches.
    /// </summary>
    private static ParsedRange? ParseRange(HttpRequest request, long size, string etag)
    {
        var header = request.Headers.Range.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (request.Headers.IfRange.ToString() is { Length: > 0 } ifRange && ifRange != etag)
        {
            return null;
        }

        var spec = header[6..].Trim();
        if (spec.Contains(','))
        {
            return null;
        }

        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        if (first.Length == 0)
        {
            // Suffix: the last N bytes.
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix == 0)
            {
                return new ParsedRange(0, 0, false);
            }

            return size == 0 ? new ParsedRange(0, 0, false) : new ParsedRange(Math.Max(0, size - suffix), size - 1, true);
        }

        if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var from))
        {
            return null;
        }

        var to = size - 1;
        if (last.Length > 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out to) || to < from)
            {
                return null;
            }

            to = Math.Min(to, size - 1);
        }

        return from >= size ? new ParsedRange(0, 0, false) : new ParsedRange(from, to, true);
    }
}
