using System.Text;

namespace Atrium.Storage;

public sealed class InvalidStorageKeyException(string key, string reason)
    : StorageException($"Storage key is not valid ({reason}).")
{
    /// <summary>The offending key. Kept out of the message so it is not logged by accident.</summary>
    public string Key { get; } = key;

    public string Reason { get; } = reason;
}

/// <summary>
/// The one definition of a valid storage key (DESIGN.md 3.5). Every
/// provider validates every key with it before touching storage, so path
/// traversal is stopped in one place, whatever the caller did.
/// </summary>
/// <remarks>
/// The rules are the strictest common denominator of the supported
/// backends, including Windows filesystems, so a key valid here means the
/// same object everywhere.
/// </remarks>
public static class StorageKey
{
    public const int MaxLength = 1024;
    public const int MaxSegmentBytes = 255;

    /// <summary>Top-level name providers use for their own temporary files. Never a valid key.</summary>
    public const string ReservedPrefix = ".atrium-tmp";

    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static void Validate(string key)
    {
        if (Check(key, allowEmpty: false, allowTrailingSlash: false) is { } reason)
        {
            throw new InvalidStorageKeyException(key, reason);
        }
    }

    /// <summary>A listing prefix: empty, or a key optionally ending in a slash.</summary>
    public static void ValidatePrefix(string prefix)
    {
        if (Check(prefix, allowEmpty: true, allowTrailingSlash: true) is { } reason)
        {
            throw new InvalidStorageKeyException(prefix, reason);
        }
    }

    public static bool IsValid(string? key) => key is not null && Check(key, allowEmpty: false, allowTrailingSlash: false) is null;

    private static string? Check(string? key, bool allowEmpty, bool allowTrailingSlash)
    {
        if (key is null)
        {
            return "missing";
        }

        if (key.Length == 0)
        {
            return allowEmpty ? null : "empty";
        }

        if (key.Length > MaxLength)
        {
            return "too long";
        }

        var body = allowTrailingSlash && key.EndsWith('/') ? key[..^1] : key;
        var segments = body.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            if (CheckSegment(segments[i]) is { } reason)
            {
                return reason;
            }
        }

        return string.Equals(segments[0], ReservedPrefix, StringComparison.OrdinalIgnoreCase) ? "reserved name" : null;
    }

    private static string? CheckSegment(string segment)
    {
        if (segment.Length == 0)
        {
            // Covers a leading slash (absolute path) and doubled slashes.
            return "empty segment";
        }

        if (segment is "." or "..")
        {
            return "relative segment";
        }

        foreach (var c in segment)
        {
            if (char.IsControl(c))
            {
                return "control character";
            }

            // Backslash is a separator on Windows; colon opens drive letters
            // and alternate data streams there; the rest are wildcards or
            // otherwise unusable in Windows file names.
            if (c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                return "forbidden character";
            }
        }

        // Windows silently drops trailing dots and spaces, which would make
        // two different keys name the same file.
        if (segment[^1] is '.' or ' ')
        {
            return "trailing dot or space";
        }

        var stem = segment.Split('.')[0];
        if (WindowsDeviceNames.Contains(stem))
        {
            return "reserved device name";
        }

        return Encoding.UTF8.GetByteCount(segment) > MaxSegmentBytes ? "segment too long" : null;
    }
}
