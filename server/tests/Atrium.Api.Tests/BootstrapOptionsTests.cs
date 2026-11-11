using Atrium.Api.Hosting;
using Microsoft.Extensions.Configuration;

namespace Atrium.Api.Tests;

public sealed class BootstrapOptionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Defaults_to_sqlite_inside_the_config_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atrium-config");

        var options = BootstrapOptions.From(Config((BootstrapOptions.ConfigDirKey, dir)));

        Assert.Equal(DatabaseProvider.Sqlite, options.DatabaseProvider);
        Assert.Equal(Path.GetFullPath(dir), options.ConfigDir);
        Assert.Contains(Path.Combine(Path.GetFullPath(dir), "atrium.db"), options.ConnectionString, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(Path.GetFullPath(dir), "backups"), options.BackupDir);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("PostgreSQL")]
    public void Postgres_uses_the_given_connection_string(string provider)
    {
        var options = BootstrapOptions.From(Config(
            (BootstrapOptions.DatabaseProviderKey, provider),
            (BootstrapOptions.DatabaseConnectionKey, "Host=db;Database=atrium")));

        Assert.Equal(DatabaseProvider.Postgres, options.DatabaseProvider);
        Assert.Equal("Host=db;Database=atrium", options.ConnectionString);
    }

    [Fact]
    public void Postgres_without_a_connection_string_is_rejected_with_the_variable_name()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            BootstrapOptions.From(Config((BootstrapOptions.DatabaseProviderKey, "postgres"))));

        Assert.Contains(BootstrapOptions.DatabaseConnectionKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_provider_is_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            BootstrapOptions.From(Config((BootstrapOptions.DatabaseProviderKey, "mysql"))));

        Assert.Contains("mysql", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Printing_the_options_does_not_reveal_the_connection_string()
    {
        var options = BootstrapOptions.From(Config(
            (BootstrapOptions.DatabaseProviderKey, "postgres"),
            (BootstrapOptions.DatabaseConnectionKey, "Host=db;Password=hunter2")));

        Assert.DoesNotContain("hunter2", options.ToString(), StringComparison.Ordinal);
    }
}
