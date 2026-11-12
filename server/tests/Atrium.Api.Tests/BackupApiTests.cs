using System.Net;
using Atrium.Api.Endpoints;
using Atrium.Api.Hosting;
using Atrium.Core.Identity;
using Atrium.Data.Backup;
using Atrium.Files.Backups;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Api.Tests;

public sealed class BackupApiTests : IDisposable
{
    private readonly AtriumFactory _factory = new();
    private readonly HttpClient _client;

    public BackupApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<string> WaitForBackupAsync(string reason)
    {
        var directory = Path.Combine(_factory.ConfigDir, "backups");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (BackupFiles.List(directory).FirstOrDefault(f => f.Reason == reason) is { } file)
            {
                return file.Path;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No {reason} backup appeared.");
    }

    [Fact]
    public async Task Only_an_admin_can_list_or_start_backups()
    {
        await _factory.CreateUserAsync("ana", TestApi.Password);
        var token = (await _client.SignInAsync("ana")).AccessToken;

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(HttpMethod.Get, "/api/v1/admin/backups", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(HttpMethod.Post, "/api/v1/admin/backups", token)).StatusCode);
    }

    [Fact]
    public async Task A_backup_is_written_locally_and_copied_into_each_storage_location()
    {
        await _factory.AddLocalStorageAsync();
        await _factory.CreateUserAsync("root", TestApi.Password, UserRole.Admin);
        var token = (await _client.SignInAsync("root")).AccessToken;

        var started = await _client.SendAsync(HttpMethod.Post, "/api/v1/admin/backups", token);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var path = await WaitForBackupAsync(MetadataBackupHandler.Manual);

        await using (var file = File.OpenRead(path))
        {
            var manifest = await PortableBackup.VerifyAsync(file, CancellationToken.None);
            Assert.Equal(1, manifest.Tables.Single(t => t.Name == "Users").Rows);
        }

        var copy = Path.Combine(_factory.StorageRoot, "main", "atrium", "backups", Path.GetFileName(path));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(copy) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Equal(await File.ReadAllBytesAsync(path), await File.ReadAllBytesAsync(copy));
        var listed = await (await _client.SendAsync(HttpMethod.Get, "/api/v1/admin/backups", token)).ReadAsync<List<BackupInfo>>();
        Assert.Contains(listed, b => b.Name == Path.GetFileName(path) && b.Reason == MetadataBackupHandler.Manual);
    }

    [Fact]
    public async Task A_restored_backup_brings_back_the_accounts_on_a_new_install()
    {
        await _factory.CreateUserAsync("root", TestApi.Password, UserRole.Admin);
        var token = (await _client.SignInAsync("root")).AccessToken;
        await _client.SendAsync(HttpMethod.Post, "/api/v1/admin/backups", token);
        var path = await WaitForBackupAsync(MetadataBackupHandler.Manual);

        var newConfig = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(newConfig);

        // An install that was started once already: its database is moved aside, never deleted.
        await File.WriteAllTextAsync(Path.Combine(newConfig, "atrium.db"), "an older database");
        var bootstrap = BootstrapOptions.From(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [BootstrapOptions.ConfigDirKey] = newConfig })
            .Build());
        var services = new ServiceCollection().AddLogging().AddAtriumDatabase(bootstrap).BuildServiceProvider();
        var output = new StringWriter();

        var exit = await RestoreCommand.RunAsync(services, path, bootstrap, output);

        Assert.True(exit == 0, output.ToString());
        Assert.Single(Directory.GetFiles(newConfig, "atrium.db.before-restore-*"));
        await services.DisposeAsync();
        SqliteConnection.ClearAllPools();

        using var restored = new AtriumFactory(newConfig, new MutableTime(), ownsConfigDir: true);
        var client = restored.CreateClient();
        var login = await client.LoginAsync("root");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task A_damaged_backup_is_refused_and_nothing_is_changed()
    {
        var path = Path.Combine(_factory.ConfigDir, "not-a-backup.zip");
        await File.WriteAllTextAsync(path, "not a zip");
        var bootstrap = BootstrapOptions.From(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [BootstrapOptions.ConfigDirKey] = Path.Combine(_factory.ConfigDir, "elsewhere") })
            .Build());
        await using var services = new ServiceCollection().AddLogging().AddAtriumDatabase(bootstrap).BuildServiceProvider();

        var output = new StringWriter();

        var exit = await RestoreCommand.RunAsync(services, path, bootstrap, output);

        Assert.Equal(1, exit);
        Assert.StartsWith("Restore refused", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_factory.ConfigDir, "elsewhere", "atrium.db")));
    }
}
