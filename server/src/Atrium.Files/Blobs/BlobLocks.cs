namespace Atrium.Files.Blobs;

/// <summary>
/// Serializes the two operations that must never interleave for one
/// content hash: storing it (which may revive a blob waiting for deletion)
/// and the collector deleting it. Without this, an upload could reuse a
/// blob in the moment its content is being deleted.
/// </summary>
/// <remarks>
/// An in-process lock is enough because exactly one server process owns a
/// database (D-009, InstanceLock). Locks are striped by hash, so unrelated
/// uploads rarely wait for each other.
/// </remarks>
public sealed class BlobLocks
{
    private const int Stripes = 64;

    private readonly SemaphoreSlim[] _stripes = Enumerable.Range(0, Stripes).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<IDisposable> AcquireAsync(Guid locationId, byte[] sha256, CancellationToken ct)
    {
        var index = (int)((uint)HashCode.Combine(locationId, sha256[0], sha256[1], sha256[2], sha256[3]) % Stripes);
        var stripe = _stripes[index];
        await stripe.WaitAsync(ct);
        return new Release(stripe);
    }

    private sealed class Release(SemaphoreSlim stripe) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                stripe.Release();
            }
        }
    }
}
