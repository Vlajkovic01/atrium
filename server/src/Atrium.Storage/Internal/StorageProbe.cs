using System.Security.Cryptography;

namespace Atrium.Storage.Internal;

/// <summary>
/// The health check both providers share: write a small random object,
/// read it back, compare, delete. Reading back is the point; a location
/// that accepts writes but returns something else must not pass.
/// </summary>
internal static class StorageProbe
{
    public const string MismatchCode = StorageHealthCodes.ReadBackMismatch;

    public static string NewKey() => $"atrium-probe-{Guid.NewGuid():N}";

    /// <summary>True when what was read equals what was written. Exceptions pass through to the provider.</summary>
    public static async Task<bool> RoundTripAsync(IStorageProvider provider, string key, CancellationToken ct)
    {
        var expected = RandomNumberGenerator.GetBytes(64);
        using (var content = new MemoryStream(expected))
        {
            await provider.WriteAsync(key, content, overwrite: false, ct);
        }

        try
        {
            using var actual = new MemoryStream();
            await using (var read = await provider.OpenReadAsync(key, null, ct))
            {
                await read.CopyToAsync(actual, ct);
            }

            return actual.ToArray().AsSpan().SequenceEqual(expected);
        }
        finally
        {
            await provider.DeleteAsync(key, ct);
        }
    }
}
