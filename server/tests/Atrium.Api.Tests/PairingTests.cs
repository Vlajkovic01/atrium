using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Atrium.Api.Endpoints;
using Atrium.Core.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Tests;

public sealed class PairingTests : IDisposable
{
    private const string Password = "correct horse battery";
    private const string SessionsUrl = "/api/v1/sessions";

    private readonly AtriumFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static object Tv() => new { name = "Living room TV", kind = "tv", platform = "Android TV", appVersion = "0.1.0" };

    private static async Task<TokenResponse> SignInAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            name,
            password = Password,
            device = new { name = "Phone", kind = "mobile" },
        });
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(AtriumFactory.Json);
        return login!.Tokens!;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, string? accessToken, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static async Task<StartPairingResponse> StartAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/pairing", new { device = Tv() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<StartPairingResponse>(AtriumFactory.Json))!;
    }

    private static async Task<PairingTokenResponse> PollAsync(HttpClient client, string deviceCode)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/pairing/token", new { deviceCode });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PairingTokenResponse>(AtriumFactory.Json))!;
    }

    [Fact]
    public async Task A_tv_is_signed_in_after_a_signed_in_user_approves_its_code()
    {
        var ana = await _factory.CreateUserAsync("ana", Password);
        using var tv = NewClient();
        using var phone = NewClient();
        var phoneTokens = await SignInAsync(phone, "ana");

        var started = await StartAsync(tv);
        Assert.Matches("^[BCDFGHJKLMNPQRSTVWXZ]{4}-[BCDFGHJKLMNPQRSTVWXZ]{4}$", started.UserCode);
        Assert.EndsWith($"/pair?code={started.UserCode}", started.VerificationUrl, StringComparison.Ordinal);
        Assert.Equal(600, started.ExpiresInSeconds);
        Assert.Equal(PairingStatus.Pending, (await PollAsync(tv, started.DeviceCode)).Status);

        // The approver sees which device is asking before deciding.
        var details = await SendAsync(phone, HttpMethod.Get, $"/api/v1/auth/pairing/{started.UserCode}", phoneTokens.AccessToken);
        var shown = await details.Content.ReadFromJsonAsync<PairingDetails>(AtriumFactory.Json);
        Assert.Equal("Living room TV", shown!.DeviceName);
        Assert.Equal(DeviceKind.Tv, shown.DeviceKind);

        var approve = await SendAsync(phone, HttpMethod.Post, $"/api/v1/auth/pairing/{started.UserCode}/approve", phoneTokens.AccessToken, new { });
        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);

        var result = await PollAsync(tv, started.DeviceCode);
        Assert.Equal(PairingStatus.Approved, result.Status);
        Assert.Equal(ana.Id, result.Tokens!.User.Id);
        Assert.False(string.IsNullOrEmpty(result.Tokens.RefreshToken));
        var sessions = await (await SendAsync(tv, HttpMethod.Get, SessionsUrl, result.Tokens.AccessToken))
            .Content.ReadFromJsonAsync<List<SessionInfo>>(AtriumFactory.Json);
        Assert.Contains(sessions!, s => s.Current && s.DeviceName == "Living room TV" && s.DeviceKind == DeviceKind.Tv);
    }

    [Fact]
    public async Task The_start_response_does_not_let_the_device_skip_approval()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var tv = NewClient();

        var started = await StartAsync(tv);

        // Neither code is a credential by itself.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(tv, HttpMethod.Get, SessionsUrl, started.DeviceCode)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(tv, HttpMethod.Get, SessionsUrl, started.UserCode)).StatusCode);
        // Approving needs a signed-in user.
        var approve = await SendAsync(tv, HttpMethod.Post, $"/api/v1/auth/pairing/{started.UserCode}/approve", accessToken: null, new { });
        Assert.Equal(HttpStatusCode.Unauthorized, approve.StatusCode);
        Assert.Equal(PairingStatus.Pending, (await PollAsync(tv, started.DeviceCode)).Status);
    }

    [Fact]
    public async Task The_device_code_is_stored_only_as_a_hash()
    {
        using var tv = NewClient();
        var started = await StartAsync(tv);

        await _factory.WithDbAsync(async db =>
        {
            var request = await db.PairingRequests.SingleAsync();
            Assert.Equal(32, request.DeviceCodeHash.Length);
            Assert.DoesNotContain(started.DeviceCode, Convert.ToBase64String(request.DeviceCodeHash), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task An_approval_gives_exactly_one_session()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var tv = NewClient();
        using var phone = NewClient();
        var phoneTokens = await SignInAsync(phone, "ana");
        var started = await StartAsync(tv);
        await SendAsync(phone, HttpMethod.Post, $"/api/v1/auth/pairing/{started.UserCode}/approve", phoneTokens.AccessToken, new { });

        var polls = await Task.WhenAll(PollAsync(tv, started.DeviceCode), PollAsync(tv, started.DeviceCode));
        var later = await PollAsync(tv, started.DeviceCode);

        Assert.Single(polls, p => p.Status == PairingStatus.Approved);
        Assert.Equal(PairingStatus.Expired, later.Status);
        var sessions = await (await SendAsync(phone, HttpMethod.Get, SessionsUrl, phoneTokens.AccessToken))
            .Content.ReadFromJsonAsync<List<SessionInfo>>(AtriumFactory.Json);
        Assert.Single(sessions!, s => s.DeviceKind == DeviceKind.Tv);
    }

    [Fact]
    public async Task A_denied_request_tells_the_device_and_cannot_be_approved_afterwards()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var tv = NewClient();
        using var phone = NewClient();
        var phoneTokens = await SignInAsync(phone, "ana");
        var started = await StartAsync(tv);

        var deny = await SendAsync(phone, HttpMethod.Post, $"/api/v1/auth/pairing/{started.UserCode}/deny", phoneTokens.AccessToken);
        var approve = await SendAsync(phone, HttpMethod.Post, $"/api/v1/auth/pairing/{started.UserCode}/approve", phoneTokens.AccessToken, new { });

        Assert.Equal(HttpStatusCode.NoContent, deny.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, approve.StatusCode);
        Assert.Equal(PairingStatus.Denied, (await PollAsync(tv, started.DeviceCode)).Status);
    }

    [Fact]
    public async Task A_request_expires_after_ten_minutes()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var tv = NewClient();
        using var phone = NewClient();
        var phoneTokens = await SignInAsync(phone, "ana");
        var started = await StartAsync(tv);

        _factory.Time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        // The phone's access token expired too; get a fresh one.
        phoneTokens = await SignInAsync(phone, "ana");

        var approve = await SendAsync(phone, HttpMethod.Post, $"/api/v1/auth/pairing/{started.UserCode}/approve", phoneTokens.AccessToken, new { });
        Assert.Equal(HttpStatusCode.NotFound, approve.StatusCode);
        Assert.Equal(PairingStatus.Expired, (await PollAsync(tv, started.DeviceCode)).Status);
    }

    [Fact]
    public async Task An_approved_request_that_is_not_collected_in_time_gives_nothing()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var tv = NewClient();
        using var phone = NewClient();
        var phoneTokens = await SignInAsync(phone, "ana");
        var started = await StartAsync(tv);
        await SendAsync(phone, HttpMethod.Post, $"/api/v1/auth/pairing/{started.UserCode}/approve", phoneTokens.AccessToken, new { });

        _factory.Time.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(PairingStatus.Expired, (await PollAsync(tv, started.DeviceCode)).Status);
    }

    [Fact]
    public async Task The_code_can_be_typed_in_lower_case_without_the_dash()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var tv = NewClient();
        using var phone = NewClient();
        var phoneTokens = await SignInAsync(phone, "ana");
        var started = await StartAsync(tv);
        var typed = started.UserCode.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

        var approve = await SendAsync(phone, HttpMethod.Post, $"/api/v1/auth/pairing/{typed}/approve", phoneTokens.AccessToken, new { });

        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);
        Assert.Equal(PairingStatus.Approved, (await PollAsync(tv, started.DeviceCode)).Status);
    }

    [Fact]
    public async Task Unknown_and_malformed_codes_are_not_found()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var phone = NewClient();
        var phoneTokens = await SignInAsync(phone, "ana");

        foreach (var code in new[] { "BCDF-GHJK", "nonsense", "BCDF-GH1K" })
        {
            var details = await SendAsync(phone, HttpMethod.Get, $"/api/v1/auth/pairing/{code}", phoneTokens.AccessToken);
            Assert.Equal(HttpStatusCode.NotFound, details.StatusCode);
        }

        Assert.Equal(PairingStatus.Expired, (await PollAsync(phone, "made-up-device-code")).Status);
    }

    [Fact]
    public async Task Searching_for_other_peoples_codes_is_blocked_after_a_few_misses()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var tv = NewClient();
        using var phone = NewClient();
        var phoneTokens = await SignInAsync(phone, "ana");
        var started = await StartAsync(tv);
        for (var i = 0; i < 11; i++)
        {
            await SendAsync(phone, HttpMethod.Get, "/api/v1/auth/pairing/BBBB-BBBB", phoneTokens.AccessToken);
        }

        var blocked = await SendAsync(phone, HttpMethod.Get, $"/api/v1/auth/pairing/{started.UserCode}", phoneTokens.AccessToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }

    [Fact]
    public async Task One_address_cannot_flood_the_server_with_open_requests()
    {
        using var tv = NewClient();
        for (var i = 0; i < 10; i++)
        {
            await StartAsync(tv);
        }

        var eleventh = await tv.PostAsJsonAsync("/api/v1/auth/pairing", new { device = Tv() });

        Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);

        // Expired requests free their slots.
        _factory.Time.Advance(TimeSpan.FromMinutes(11));
        await StartAsync(tv);
    }

    [Fact]
    public async Task Restrictions_chosen_at_approval_apply_to_the_new_session()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var tv = NewClient();
        using var phone = NewClient();
        var phoneTokens = await SignInAsync(phone, "ana");
        var started = await StartAsync(tv);

        await SendAsync(phone, HttpMethod.Post, $"/api/v1/auth/pairing/{started.UserCode}/approve", phoneTokens.AccessToken,
            new { readOnly = true, hidePersonalLibrary = true });
        var tvTokens = (await PollAsync(tv, started.DeviceCode)).Tokens!;

        var sessions = await (await SendAsync(tv, HttpMethod.Get, SessionsUrl, tvTokens.AccessToken))
            .Content.ReadFromJsonAsync<List<SessionInfo>>(AtriumFactory.Json);
        Assert.True(sessions!.Single(s => s.Current).ReadOnly);

        // The restrictions survive a token refresh.
        var refreshed = await (await tv.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = tvTokens.RefreshToken }))
            .Content.ReadFromJsonAsync<TokenResponse>(AtriumFactory.Json);
        var linkAfterRefresh = await SendAsync(tv, HttpMethod.Post, "/api/v1/auth/link-codes", refreshed!.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, linkAfterRefresh.StatusCode);
    }

    [Fact]
    public async Task A_restricted_session_cannot_create_other_sessions()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var tv = NewClient();
        using var secondTv = NewClient();
        using var phone = NewClient();
        var phoneTokens = await SignInAsync(phone, "ana");
        var started = await StartAsync(tv);
        await SendAsync(phone, HttpMethod.Post, $"/api/v1/auth/pairing/{started.UserCode}/approve", phoneTokens.AccessToken,
            new { readOnly = true });
        var tvTokens = (await PollAsync(tv, started.DeviceCode)).Tokens!;
        var second = await StartAsync(secondTv);

        var approve = await SendAsync(tv, HttpMethod.Post, $"/api/v1/auth/pairing/{second.UserCode}/approve", tvTokens.AccessToken, new { });
        var details = await SendAsync(tv, HttpMethod.Get, $"/api/v1/auth/pairing/{second.UserCode}", tvTokens.AccessToken);
        var link = await SendAsync(tv, HttpMethod.Post, "/api/v1/auth/link-codes", tvTokens.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, details.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, link.StatusCode);
        Assert.Equal(PairingStatus.Pending, (await PollAsync(secondTv, second.DeviceCode)).Status);
    }

    [Fact]
    public async Task A_device_approved_by_a_user_who_is_then_disabled_is_not_signed_in()
    {
        var ana = await _factory.CreateUserAsync("ana", Password);
        using var tv = NewClient();
        using var phone = NewClient();
        var phoneTokens = await SignInAsync(phone, "ana");
        var started = await StartAsync(tv);
        await SendAsync(phone, HttpMethod.Post, $"/api/v1/auth/pairing/{started.UserCode}/approve", phoneTokens.AccessToken, new { });

        await _factory.WithDbAsync(db => db.Users.Where(u => u.Id == ana.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Disabled, true)));

        var result = await PollAsync(tv, started.DeviceCode);
        Assert.Equal(PairingStatus.Denied, result.Status);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task A_phone_signs_in_with_a_link_code_exactly_once()
    {
        var ana = await _factory.CreateUserAsync("ana", Password);
        using var web = NewClient();
        using var phone = NewClient();
        var webTokens = await SignInAsync(web, "ana");

        var created = await SendAsync(web, HttpMethod.Post, "/api/v1/auth/link-codes", webTokens.AccessToken);
        var link = await created.Content.ReadFromJsonAsync<LinkCodeResponse>(AtriumFactory.Json);
        Assert.Equal(120, link!.ExpiresInSeconds);

        var body = new { code = link.Code, device = new { name = "New phone", kind = "mobile" } };
        var first = await phone.PostAsJsonAsync("/api/v1/auth/link-codes/redeem", body);
        var second = await phone.PostAsJsonAsync("/api/v1/auth/link-codes/redeem", body);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var tokens = await first.Content.ReadFromJsonAsync<TokenResponse>(AtriumFactory.Json);
        Assert.Equal(ana.Id, tokens!.User.Id);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(phone, HttpMethod.Get, SessionsUrl, tokens.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task A_link_code_expires_after_two_minutes_and_needs_a_signed_in_user_to_create()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var web = NewClient();
        var webTokens = await SignInAsync(web, "ana");
        var link = await (await SendAsync(web, HttpMethod.Post, "/api/v1/auth/link-codes", webTokens.AccessToken))
            .Content.ReadFromJsonAsync<LinkCodeResponse>(AtriumFactory.Json);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(web, HttpMethod.Post, "/api/v1/auth/link-codes", accessToken: null)).StatusCode);

        _factory.Time.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        var late = await web.PostAsJsonAsync("/api/v1/auth/link-codes/redeem",
            new { code = link!.Code, device = new { name = "New phone", kind = "mobile" } });

        Assert.Equal(HttpStatusCode.Unauthorized, late.StatusCode);
    }
}
