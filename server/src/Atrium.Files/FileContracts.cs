using Atrium.Core.Files;

namespace Atrium.Files;

/// <summary>
/// Who is asking, as far as files care. Built from the access token on
/// every request; the server checks it for every operation (DESIGN.md 6.3).
/// </summary>
/// <param name="ReadOnlySession">A device approved as read-only.</param>
/// <param name="HidePersonal">A device approved without access to the personal library.</param>
public sealed record FileActor(Guid UserId, bool IsGuest, bool ReadOnlySession, bool HidePersonal)
{
    public bool CanWriteAnything => !IsGuest && !ReadOnlySession;
}

/// <summary>
/// Error codes of file operations. They are sent to clients, which
/// translate them, so each needs a message in every language.
/// </summary>
public static class FileErrors
{
    /// <summary>No storage location is registered, so there is nowhere to put files.</summary>
    public const string NoStorage = "files.no_storage";
    public const string QuotaExceeded = "files.quota_exceeded";
    public const string NotFound = "resource.not_found";
    public const string VersionConflict = "request.version_conflict";
    public const string InvalidName = "node.invalid_name";
    public const string NameConflict = "node.name_conflict";
    public const string NotAFolder = "node.not_a_folder";
    public const string NotAFile = "node.not_a_file";
    public const string MoveIntoItself = "node.move_into_itself";
    public const string ReadOnly = "node.read_only";
    public const string InTrash = "node.in_trash";
    public const string NotInTrash = "node.not_in_trash";
    public const string IsRoot = "node.is_root";
    public const string CrossLibrary = "node.cross_library";
    public const string TooManyItems = "node.too_many_items";

    /// <summary>The operation exists, but not for this kind of library (copying inside an external folder).</summary>
    public const string NotSupported = "node.not_supported";
}

/// <summary>The outcome of a file operation: a value, or an error code with optional parameters.</summary>
public sealed record FileResult<T>(T? Value, string? Error, IReadOnlyDictionary<string, object?>? Params = null)
{
    public bool Ok => Error is null;

    public static FileResult<T> Success(T value) => new(value, null);

    public static FileResult<T> Fail(string error, IReadOnlyDictionary<string, object?>? parameters = null) =>
        new(default, error, parameters);
}

/// <summary>A node with what the asker may do with it.</summary>
/// <param name="InTrash">The node or one of its ancestors is in the trash.</param>
public sealed record AccessibleNode(Node Node, Library Library, bool CanWrite, bool InTrash);

public enum ChildSort
{
    Name,
    Modified,
    Size,
    Type,
}

/// <summary>What to do when a name is already taken by a sibling.</summary>
public enum ConflictPolicy
{
    Fail,

    /// <summary>Keep both: the new item gets a numbered name.</summary>
    Rename,

    /// <summary>For a file only: the existing file gets the new content, and its old content is kept as a version (D-019).</summary>
    Replace,
}

public sealed record UsageSummary(long UsedBytes, long FilesBytes, long TrashBytes, long VersionsBytes, long? QuotaBytes);

/// <param name="Location">The folders the item was in, from the library's top down to its parent.</param>
public sealed record TrashItem(Node Node, long Bytes, int ItemCount, DateTimeOffset DeleteAt, IReadOnlyList<Node> Location);
