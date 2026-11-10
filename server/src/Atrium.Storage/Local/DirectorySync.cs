using System.Runtime.InteropServices;

namespace Atrium.Storage.Local;

/// <summary>
/// Makes a rename durable. On Linux and macOS a rename lives in the
/// directory, and the directory must be flushed for the rename to survive
/// power loss; .NET has no API for that, so this calls the C library.
/// Windows makes renames durable through its own journal.
/// </summary>
internal static class DirectorySync
{
    private const int ReadOnly = 0;

    public static void Flush(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Best effort: some network filesystems refuse to sync a directory.
        // The file's own content was already flushed before the rename.
        var descriptor = Open(directory, ReadOnly);
        if (descriptor < 0)
        {
            return;
        }

        try
        {
            _ = Sync(descriptor);
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Sync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Close(int descriptor);
}
