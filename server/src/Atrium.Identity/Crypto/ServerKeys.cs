using System.Security.Cryptography;
using System.Text;

namespace Atrium.Identity.Crypto;

/// <summary>
/// The server's master key (DESIGN.md 6.7). Created once at first start and
/// kept in the config directory with owner-only permissions. Every other key
/// (token signing, URL signing, secret encryption) is derived from it per
/// purpose, so one use can never be turned against another.
/// </summary>
public sealed class ServerKeys
{
    public const string FileName = "server.key";
    private const int MasterKeyLength = 32;

    private readonly byte[] _master;

    private ServerKeys(byte[] master) => _master = master;

    /// <summary>Derives the key for one purpose. The same purpose always gives the same key.</summary>
    public byte[] Derive(string purpose, int length = 32)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, _master, length, salt: null, info: Encoding.UTF8.GetBytes(purpose));
    }

    public static ServerKeys LoadOrCreate(string keysDirectory)
    {
        var path = Path.Combine(keysDirectory, FileName);
        if (!File.Exists(path))
        {
            Create(keysDirectory, path);
        }

        var master = File.ReadAllBytes(path);
        if (master.Length != MasterKeyLength)
        {
            // Never replace a damaged key with a new one: everything encrypted
            // with the old key (storage credentials, TOTP secrets) would
            // become unreadable without anyone being told why.
            throw new InvalidDataException(
                $"Server key file '{path}' is damaged (expected {MasterKeyLength} bytes, found {master.Length}). " +
                "Restore it from your backup of the config directory.");
        }

        return new ServerKeys(master);
    }

    private static void Create(string keysDirectory, string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(keysDirectory);
        }
        else
        {
            Directory.CreateDirectory(keysDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Write to a temporary file and move it into place, so a crash never
        // leaves a partly written key behind.
        var temp = Path.Combine(keysDirectory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(RandomNumberGenerator.GetBytes(MasterKeyLength));
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temp, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another process created the key first; use theirs.
            }
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
