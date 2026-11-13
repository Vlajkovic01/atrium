using System.Net;
using Atrium.Api.Endpoints;
using Atrium.Core.Identity;
using Atrium.Core.Jobs;

namespace Atrium.Api.Tests;

public sealed class BackgroundWorkApiTests : IDisposable
{
    private const string Url = "/api/v1/admin/background-work";
    private readonly AtriumFactory _factory = new();
    private readonly HttpClient _client;

    public BackgroundWorkApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<string> AdminAsync()
    {
        await _factory.CreateUserAsync("root", TestApi.Password, UserRole.Admin);
        return (await _client.SignInAsync("root")).AccessToken;
    }

    [Fact]
    public async Task Only_an_admin_sees_or_changes_background_work()
    {
        await _factory.CreateUserAsync("ana", TestApi.Password);
        var token = (await _client.SignInAsync("ana")).AccessToken;

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(HttpMethod.Get, Url, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(HttpMethod.Post, $"{Url}/pause", token, new { minutes = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(HttpMethod.Put, $"{Url}/limits", token, new { limits = Array.Empty<object>() })).StatusCode);
    }

    [Fact]
    public async Task The_profile_and_a_limit_for_every_class_are_reported()
    {
        var token = await AdminAsync();

        var info = await (await _client.SendAsync(HttpMethod.Get, Url, token)).ReadAsync<BackgroundWorkInfo>();

        Assert.True(info.ProcessorCount >= 1);
        Assert.True(info.MemoryBytes > 0);
        Assert.Equal(Enum.GetValues<JobResourceClass>().Length, info.Classes.Count);
        Assert.All(info.Classes, c =>
        {
            Assert.Null(c.Override);
            Assert.Equal(c.Default, c.Effective);
        });
        Assert.False(info.Paused);
    }

    [Fact]
    public async Task An_admin_overrides_a_limit_and_resets_it()
    {
        var token = await AdminAsync();

        var changed = await (await _client.SendAsync(HttpMethod.Put, $"{Url}/limits", token, new
        {
            limits = new[] { new { resourceClass = "cpu", limit = (int?)3 } },
        })).ReadAsync<BackgroundWorkInfo>();
        var cpu = changed.Classes.Single(c => c.ResourceClass == JobResourceClass.Cpu);
        Assert.Equal(3, cpu.Override);
        Assert.Equal(3, cpu.Effective);

        var reset = await (await _client.SendAsync(HttpMethod.Put, $"{Url}/limits", token, new
        {
            limits = new[] { new { resourceClass = "cpu", limit = (int?)null } },
        })).ReadAsync<BackgroundWorkInfo>();
        Assert.Null(reset.Classes.Single(c => c.ResourceClass == JobResourceClass.Cpu).Override);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public async Task A_limit_out_of_range_is_refused(int limit)
    {
        var token = await AdminAsync();

        var response = await _client.SendAsync(HttpMethod.Put, $"{Url}/limits", token, new
        {
            limits = new[] { new { resourceClass = "io", limit } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Background_work_pauses_for_a_while_or_until_resumed()
    {
        var token = await AdminAsync();

        var timed = await (await _client.SendAsync(HttpMethod.Post, $"{Url}/pause", token, new { minutes = 60 })).ReadAsync<BackgroundWorkInfo>();
        Assert.True(timed.Paused);
        Assert.True(timed.PausedByAdmin);
        Assert.NotNull(timed.PausedUntil);

        var open = await (await _client.SendAsync(HttpMethod.Post, $"{Url}/pause", token, new { minutes = (int?)null })).ReadAsync<BackgroundWorkInfo>();
        Assert.True(open.Paused);
        Assert.Null(open.PausedUntil);

        var resumed = await (await _client.SendAsync(HttpMethod.Post, $"{Url}/resume", token)).ReadAsync<BackgroundWorkInfo>();
        Assert.False(resumed.Paused);
        Assert.False(resumed.PausedByAdmin);
    }

    [Fact]
    public async Task A_pause_longer_than_a_week_is_refused()
    {
        var token = await AdminAsync();

        var response = await _client.SendAsync(HttpMethod.Post, $"{Url}/pause", token, new { minutes = BackgroundWorkEndpoints.MaxPauseMinutes + 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
