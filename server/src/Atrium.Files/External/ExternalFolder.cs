using System.Security.Cryptography;
using System.Text;

namespace Atrium.Files.External;

/// <summary>One entry of a folder in an external library.</summary>
/// <param name="RelativePath">Relative to the library's folder, with forward slashes.</param>
public sealed record ExternalEntry(string Name, string RelativePath, bool IsFolder, long Size, DateTimeOffset ModifiedAt);

/// <summary>
/// The only way Atrium reads an external library's folder (DESIGN.md 3.5,
/// 4.4). Every path is resolved against the library's folder and must stay
/// inside it; symbolic links and other reparse points are neither listed
/// nor followed, so nothing outside the folder can be reached through
/// one. Files are opened for reading only, sharing with other programs.
/// </summary>
public sealed class ExternalFolder
{
    /// <summary>Atrium's own trash inside a library with writes enabled; never listed as content.</summary>
    public const string TrashFolderName = ".atrium-trash";

    /// <summary>Folders and files that belong to the system or to other software, not to the user's media.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        TrashFolderName, "@eaDir", "#recycle", "#snapshot", "$RECYCLE.BIN", "System Volume Information",
        "lost+found", "Thumbs.db", "desktop.ini", ".DS_Store",
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public ExternalFolder(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    /// <summary>The folder's absolute, normalized path.</summary>
    public string Root { get; }

    /// <summary>Whether a name is skipped: hidden entries, Atrium's trash and system folders.</summary>
    public static bool IsIgnored(string name) => name.StartsWith('.') || Ignored.Contains(name);

    /// <summary>The SHA-256 of a relative path, which the index looks a file up by.</summary>
    public static byte[] HashPath(string relativePath) => SHA256.HashData(Encoding.UTF8.GetBytes(relativePath));

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or inside it.</summary>
    public static bool IsSameOrInside(string path, string root)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return p.Equals(r, PathComparison)
            || p.StartsWith(r + Path.DirectorySeparatorChar, PathComparison)
            || (Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar && p.StartsWith(r + Path.AltDirectorySeparatorChar, PathComparison));
    }

    /// <summary>
    /// The absolute path of an entry, or null when the relative path is not
    /// acceptable: absolute, with "." or ".." parts, outside the folder, or
    /// passing through a symbolic link.
    /// </summary>
    public string? Resolve(string relativePath)
    {
        if (relativePath.Length == 0)
        {
            return Root;
        }

        var parts = relativePath.Split('/');
        if (Path.IsPathRooted(relativePath) || parts.Any(p => p.Length == 0 || p is "." or ".." || p.Contains('\\') || p.Contains('\0')))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine([Root, .. parts]));
        if (!IsSameOrInside(full, Root))
        {
            return null;
        }

        // No step on the way may be a link: it could lead anywhere.
        var current = Root;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
            {
                return null;
            }
        }

        return full;
    }

    /// <summary>The folder exists and can be listed.</summary>
    public bool IsReachable()
    {
        try
        {
            var root = new DirectoryInfo(Root);
            if (!root.Exists)
            {
                return false;
            }

            using var entries = root.EnumerateFileSystemInfos().GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// The entries of one folder, sorted by name. Links, ignored names and
    /// entries that cannot be read are left out.
    /// </summary>
    public List<ExternalEntry> List(string relativeFolder)
    {
        var folder = Resolve(relativeFolder) ?? throw new IOException($"'{relativeFolder}' is not a folder of this library.");
        var result = new List<ExternalEntry>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            RecurseSubdirectories = false,
        };
        foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
        {
            if (IsIgnored(info.Name) || info.LinkTarget is not null)
            {
                continue;
            }

            var relative = relativeFolder.Length == 0 ? info.Name : relativeFolder + "/" + info.Name;
            var isFolder = info is DirectoryInfo;
            result.Add(new ExternalEntry(
                info.Name,
                relative,
                isFolder,
                isFolder ? 0 : ((FileInfo)info).Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>Size and modification time of a file, or null when it is gone or not a plain file.</summary>
    public ExternalEntry? Stat(string relativePath)
    {
        if (Resolve(relativePath) is not { } full)
        {
            return null;
        }

        var info = new FileInfo(full);
        return info.Exists && info.LinkTarget is null
            ? new ExternalEntry(info.Name, relativePath, false, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero))
            : null;
    }

    /// <summary>Opens a file for reading. Other programs may keep writing, renaming or deleting it meanwhile.</summary>
    /// <exception cref="FileNotFoundException">Gone, or not inside the folder.</exception>
    public FileStream OpenRead(string relativePath)
    {
        var full = Resolve(relativePath) ?? throw new FileNotFoundException("Not a file of this library.", relativePath);
        return new FileStream(
            full,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
}
