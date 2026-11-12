namespace Atrium.Api.Hosting;

/// <summary>
/// Makes sure only one server runs on a config directory (DESIGN.md 1.1).
/// Two servers on the same data would each keep their own list of revoked
/// sessions and their own job workers, and could undo each other's work.
/// The second one refuses to start.
/// </summary>
/// <remarks>
/// The lock is an open file handle. The operating system releases it when
/// the process ends, however it ends, so a crash never leaves a stale lock
/// that someone has to delete by hand.
/// </remarks>
public sealed class InstanceLock : IDisposable
{
    public const string FileName = "atrium.lock";

    /// <summary>How long to wait for a server that is just shutting down, as in a container restart.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);

    private readonly FileStream _handle;

    private InstanceLock(FileStream handle) => _handle = handle;

    public static InstanceLock Acquire(string configDir) => Acquire(configDir, DefaultWait);

    public static InstanceLock Acquire(string configDir, TimeSpan wait)
    {
        var path = Path.Combine(configDir, FileName);
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                return new InstanceLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
            }
            catch (IOException e)
            {
                throw new InvalidOperationException(
                    $"Another Atrium server is already running on '{configDir}'. " +
                    "Only one server may use a config directory at a time. Stop the other one, " +
                    "or give this one its own directory with ATRIUM_CONFIG_DIR.",
                    e);
            }
        }
    }

    public void Dispose() => _handle.Dispose();
}
