using System.Net;
using Atrium.Api.Hosting;

namespace Atrium.Api.Tests;

public sealed class InstanceLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));

    public InstanceLockTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void A_second_server_on_the_same_config_directory_is_refused_with_an_explanation()
    {
        using var first = InstanceLock.Acquire(_dir, TimeSpan.Zero);

        var error = Assert.Throws<InvalidOperationException>(() => InstanceLock.Acquire(_dir, TimeSpan.FromMilliseconds(300)));

        Assert.Contains("already running", error.Message, StringComparison.Ordinal);
        Assert.Contains(_dir, error.Message, StringComparison.Ordinal);
        Assert.Contains(BootstrapOptions.ConfigDirKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_directory_is_free_again_once_the_first_server_stops()
    {
        InstanceLock.Acquire(_dir, TimeSpan.Zero).Dispose();

        using var second = InstanceLock.Acquire(_dir, TimeSpan.Zero);
    }

    [Fact]
    public async Task A_server_that_is_just_shutting_down_is_waited_for()
    {
        var first = InstanceLock.Acquire(_dir, TimeSpan.Zero);
        var release = Task.Run(async () =>
        {
            await Task.Delay(500);
            first.Dispose();
        });

        // Would fail at once without the wait; a container restart looks like this.
        using var second = InstanceLock.Acquire(_dir, TimeSpan.FromSeconds(10));

        await release;
    }

    [Fact]
    public void Servers_on_different_directories_do_not_block_each_other()
    {
        var other = Path.Combine(_dir, "other");
        Directory.CreateDirectory(other);

        using var first = InstanceLock.Acquire(_dir, TimeSpan.Zero);
        using var second = InstanceLock.Acquire(other, TimeSpan.Zero);
    }

    [Fact]
    public async Task A_running_server_holds_its_config_directory()
    {
        using var factory = new AtriumFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/health/live")).StatusCode);

        Assert.Throws<InvalidOperationException>(() => InstanceLock.Acquire(factory.ConfigDir, TimeSpan.FromMilliseconds(300)));
    }
}
