using System.Text.Json;
using System.Text.Json.Serialization;
using Atrium.Api.Hosting;
using Atrium.Api.Storage;
using Atrium.Core.Identity;
using Atrium.Core.Storage;
using Atrium.Data;
using Atrium.Files.Storage;
using Atrium.Identity.Crypto;
using Atrium.Identity.Passwords;
using Atrium.Identity.SignIn;
using Atrium.Identity.Totp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Api.Tests;

/// <summary>A clock the tests move by hand, so expiry and lockout need no waiting.</summary>
public sealed class MutableTime : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// Hosts the real server in memory against a real SQLite database in its own
/// temporary config directory, so tests never share state.
/// </summary>
public sealed class AtriumFactory : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly bool _ownsConfigDir;

    public AtriumFactory()
        : this(Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N")), new MutableTime(), ownsConfigDir: true)
    {
    }

    /// <summary>A second server over an existing config directory: a restart.</summary>
    public AtriumFactory(string configDir, MutableTime time, bool ownsConfigDir = false)
    {
        ConfigDir = configDir;
        Time = time;
        _ownsConfigDir = ownsConfigDir;
    }

    public string ConfigDir { get; }

    public MutableTime Time { get; }

    /// <summary>
    /// Extra settings for this server, set before it is first used. A test
    /// that needs a differently configured server sets this instead of
    /// deriving a second server from the factory: only one server may run
    /// on a config directory.
    /// </summary>
    public Action<IWebHostBuilder>? Configure { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(BootstrapOptions.ConfigDirKey, ConfigDir);
        Configure?.Invoke(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);

            // Real Argon2id, at a cost that keeps the suite fast.
            services.AddSingleton(new Argon2Settings(MemoryKib: 1024, Passes: 1));
        });
    }

    public async Task<User> CreateUserAsync(string name, string password, UserRole role = UserRole.User, bool disabled = false)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtriumDbContext>();
        var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
        var now = Time.GetUtcNow();
        var user = new User
        {
            Name = name,
            NameKey = UserName.KeyOf(name),
            DisplayName = name,
            PasswordHash = await passwords.HashAsync(password, CancellationToken.None),
            Role = role,
            Disabled = disabled,
            CreatedAt = now,
            PasswordChangedAt = now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Gives the user a confirmed TOTP credential and returns its secret.</summary>
    public async Task<byte[]> EnableTotpAsync(User user)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AtriumDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<SecretProtector>();
        var secret = TotpGenerator.NewSecret();
        db.TotpCredentials.Add(new TotpCredential
        {
            UserId = user.Id,
            EncryptedSecret = protector.Protect(secret, SignInService.TotpProtectionPurpose),
            CreatedAt = Time.GetUtcNow(),
            ConfirmedAt = Time.GetUtcNow(),
        });
        await db.SaveChangesAsync();
        return secret;
    }

    /// <summary>Where <see cref="AddLocalStorageAsync"/> puts files: outside the config directory, as the server requires.</summary>
    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N") + "-storage");

    /// <summary>Registers a real local storage location, the way the setup wizard does.</summary>
    public async Task<StorageLocation> AddLocalStorageAsync(string name = "main")
    {
        await using var scope = Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<StorageLocationService>();
        var outcome = await service.CreateAsync(
            name,
            StorageProviderKind.LocalFileSystem,
            new StorageLocationConfig(new LocalStorageSettings(Path.Combine(StorageRoot, name)), null),
            createDirectory: true,
            CancellationToken.None);
        Assert.True(outcome.Test.Ok, outcome.Test.ErrorCode);
        return outcome.Location!;
    }

    public async Task<T> WithDbResultAsync<T>(Func<AtriumDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AtriumDbContext>());
    }

    public async Task WithDbAsync(Func<AtriumDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AtriumDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || !_ownsConfigDir)
        {
            return;
        }

        // Pooled connections keep the database file open on Windows.
        SqliteConnection.ClearAllPools();
        foreach (var directory in new[] { ConfigDir, StorageRoot })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Leftover temp files must not fail a test run.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
