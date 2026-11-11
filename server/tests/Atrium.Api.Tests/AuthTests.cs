using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Atrium.Api.Endpoints;
using Atrium.Core.Identity;
using Atrium.Identity.Totp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Tests;

public sealed class AuthTests : IDisposable
{
    private const string Password = "correct horse battery";
    private const string SessionsUrl = "/api/v1/sessions";

    private readonly AtriumFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static object Device(string kind = "mobile") =>
        new { name = "Test device", kind, platform = "tests", appVersion = "0.1.0" };

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string name, string password, string kind = "mobile") =>
        await client.PostAsJsonAsync("/api/v1/auth/login", new { name, password, device = Device(kind) });

    private static async Task<TokenResponse> SignInAsync(HttpClient client, string name, string kind = "mobile")
    {
        var response = await PostLoginAsync(client, name, Password, kind);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(AtriumFactory.Json);
        Assert.Equal(LoginStatus.SignedIn, login!.Status);
        return login.Tokens!;
    }

    private static async Task<HttpResponseMessage> GetWithTokenAsync(HttpClient client, string url, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> RefreshAsync(HttpClient client, string? refreshToken) =>
        await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact]
    public async Task Signing_in_returns_tokens_that_open_protected_endpoints()
    {
        var user = await _factory.CreateUserAsync("Ana", Password, UserRole.Admin);
        using var client = NewClient();

        var tokens = await SignInAsync(client, "ana");

        Assert.Equal(user.Id, tokens.User.Id);
        Assert.Equal("Ana", tokens.User.Name);
        Assert.Equal(UserRole.Admin, tokens.User.Role);
        Assert.Equal(900, tokens.ExpiresInSeconds);
        Assert.False(string.IsNullOrEmpty(tokens.RefreshToken));
        var response = await GetWithTokenAsync(client, SessionsUrl, tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sessions = await response.Content.ReadFromJsonAsync<List<SessionInfo>>(AtriumFactory.Json);
        var session = Assert.Single(sessions!);
        Assert.True(session.Current);
        Assert.Equal(tokens.SessionId, session.Id);
        Assert.Equal("Test device", session.DeviceName);
        Assert.Equal(DeviceKind.Mobile, session.DeviceKind);
    }

    [Fact]
    public async Task Protected_endpoints_refuse_missing_garbage_and_foreign_tokens()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(SessionsUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetWithTokenAsync(client, SessionsUrl, "not.a.token")).StatusCode);

        // A token from another server, signed with another key.
        using var other = new AtriumFactory();
        await other.CreateUserAsync("ana", Password);
        using var otherClient = other.CreateClient();
        var foreign = await SignInAsync(otherClient, "ana");
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetWithTokenAsync(client, SessionsUrl, foreign.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_unknown_user_and_disabled_account_get_the_same_answer()
    {
        await _factory.CreateUserAsync("ana", Password);
        await _factory.CreateUserAsync("disabled", Password, disabled: true);
        using var client = NewClient();

        var wrongPassword = await PostLoginAsync(client, "ana", "wrong password");
        var unknownUser = await PostLoginAsync(client, "nobody", Password);
        var disabled = await PostLoginAsync(client, "disabled", Password);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);
        var body = await BodyWithoutTraceIdAsync(wrongPassword);
        Assert.Equal(body, await BodyWithoutTraceIdAsync(unknownUser));
        Assert.Equal(body, await BodyWithoutTraceIdAsync(disabled));
        Assert.Equal("auth.invalid_credentials", await ProblemCodeAsync(wrongPassword));
    }

    // The trace id names the request, not its outcome, and differs every time.
    private static async Task<string> BodyWithoutTraceIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var fields = document.RootElement.EnumerateObject()
            .Where(p => p.Name != "traceId")
            .ToDictionary(p => p.Name, p => p.Value.GetRawText());
        return JsonSerializer.Serialize(fields);
    }

    [Fact]
    public async Task Sign_in_response_never_contains_the_password_or_its_hash()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();

        var body = await (await PostLoginAsync(client, "ana", Password)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("argon2", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, body, StringComparison.Ordinal);
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{"name":"ana","password":"correct horse battery"}""")]
    [InlineData("""{"name":"","password":"correct horse battery","device":{"name":"d","kind":"mobile"}}""")]
    [InlineData("""{"name":"ana","password":"","device":{"name":"d","kind":"mobile"}}""")]
    [InlineData("""{"name":"ana","password":"correct horse battery","device":{"name":"","kind":"mobile"}}""")]
    public async Task Incomplete_sign_in_requests_are_rejected_as_invalid(string json)
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/v1/auth/login", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("request.invalid", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task An_access_token_stops_working_after_fifteen_minutes_and_refresh_gives_a_new_one()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        var tokens = await SignInAsync(client, "ana");

        _factory.Time.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(HttpStatusCode.OK, (await GetWithTokenAsync(client, SessionsUrl, tokens.AccessToken)).StatusCode);

        _factory.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetWithTokenAsync(client, SessionsUrl, tokens.AccessToken)).StatusCode);

        var refreshed = await (await RefreshAsync(client, tokens.RefreshToken)).Content.ReadFromJsonAsync<TokenResponse>(AtriumFactory.Json);
        Assert.Equal(tokens.SessionId, refreshed!.SessionId);
        Assert.NotEqual(tokens.RefreshToken, refreshed.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, (await GetWithTokenAsync(client, SessionsUrl, refreshed.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task A_rotated_refresh_token_still_works_briefly_so_a_lost_response_does_not_sign_the_user_out()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        var tokens = await SignInAsync(client, "ana");

        // The first response is "lost": the client retries with the same token, twice.
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, tokens.RefreshToken)).StatusCode);
        _factory.Time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, tokens.RefreshToken)).StatusCode);
        _factory.Time.Advance(TimeSpan.FromSeconds(20));
        var third = await RefreshAsync(client, tokens.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        var latest = await third.Content.ReadFromJsonAsync<TokenResponse>(AtriumFactory.Json);
        Assert.Equal(HttpStatusCode.OK, (await GetWithTokenAsync(client, SessionsUrl, latest!.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Replaying_an_old_refresh_token_ends_the_whole_session()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        var tokens = await SignInAsync(client, "ana");
        var rotated = await (await RefreshAsync(client, tokens.RefreshToken)).Content.ReadFromJsonAsync<TokenResponse>(AtriumFactory.Json);

        _factory.Time.Advance(TimeSpan.FromSeconds(61));
        var replay = await RefreshAsync(client, tokens.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("auth.invalid_refresh_token", await ProblemCodeAsync(replay));
        // The thief and the owner are both out: the newest token and the access token die too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, rotated!.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetWithTokenAsync(client, SessionsUrl, rotated.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Two_refreshes_at_the_same_moment_do_not_revoke_the_session()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        var tokens = await SignInAsync(client, "ana");

        var responses = await Task.WhenAll(
            RefreshAsync(client, tokens.RefreshToken),
            RefreshAsync(client, tokens.RefreshToken));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(HttpStatusCode.OK, (await GetWithTokenAsync(client, SessionsUrl, tokens.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Unknown_missing_and_expired_refresh_tokens_are_refused()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        var tokens = await SignInAsync(client, "ana");

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, "made-up-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, null)).StatusCode);
        // An access token is not a refresh token.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, tokens.AccessToken)).StatusCode);

        _factory.Time.Advance(TimeSpan.FromDays(61));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, tokens.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task A_disabled_user_cannot_refresh()
    {
        var user = await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        var tokens = await SignInAsync(client, "ana");

        await _factory.WithDbAsync(db => db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Disabled, true)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, tokens.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Revoking_a_session_cuts_off_its_access_token_at_once()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        var phone = await SignInAsync(client, "ana");
        var laptop = await SignInAsync(client, "ana");

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{SessionsUrl}/{phone.SessionId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", laptop.AccessToken);
        var revoke = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetWithTokenAsync(client, SessionsUrl, phone.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, phone.RefreshToken)).StatusCode);
        var remaining = await (await GetWithTokenAsync(client, SessionsUrl, laptop.AccessToken)).Content
            .ReadFromJsonAsync<List<SessionInfo>>(AtriumFactory.Json);
        Assert.Equal(laptop.SessionId, Assert.Single(remaining!).Id);
    }

    [Fact]
    public async Task A_user_cannot_see_or_revoke_another_users_session()
    {
        await _factory.CreateUserAsync("ana", Password);
        await _factory.CreateUserAsync("marko", Password);
        using var client = NewClient();
        var ana = await SignInAsync(client, "ana");
        var marko = await SignInAsync(client, "marko");

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{SessionsUrl}/{ana.SessionId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", marko.AccessToken);
        var revoke = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetWithTokenAsync(client, SessionsUrl, ana.AccessToken)).StatusCode);
        var markoSessions = await (await GetWithTokenAsync(client, SessionsUrl, marko.AccessToken)).Content
            .ReadFromJsonAsync<List<SessionInfo>>(AtriumFactory.Json);
        Assert.Equal(marko.SessionId, Assert.Single(markoSessions!).Id);
    }

    [Fact]
    public async Task Signing_out_revokes_the_session()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        var tokens = await SignInAsync(client, "ana");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var logout = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetWithTokenAsync(client, SessionsUrl, tokens.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, tokens.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task A_revoked_session_stays_revoked_after_the_server_restarts()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        var tokens = await SignInAsync(client, "ana");
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            await client.SendAsync(request);
        }

        // Same config directory and keys, new process: the in-memory
        // revocation list is gone and must be rebuilt from the database.
        using var restarted = new AtriumFactory(_factory.ConfigDir, _factory.Time);
        using var newClient = restarted.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetWithTokenAsync(newClient, SessionsUrl, tokens.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task A_valid_token_keeps_working_after_the_server_restarts()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        var tokens = await SignInAsync(client, "ana");

        using var restarted = new AtriumFactory(_factory.ConfigDir, _factory.Time);
        using var newClient = restarted.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await GetWithTokenAsync(newClient, SessionsUrl, tokens.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Web_sessions_get_the_refresh_token_only_as_an_http_only_cookie()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();

        var response = await PostLoginAsync(client, "ana", Password, kind: "web");

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(AtriumFactory.Json);
        Assert.Null(login!.Tokens!.RefreshToken);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("atrium_refresh=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/auth", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_web_session_refreshes_with_its_cookie()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(client, "ana", Password, kind: "web")).StatusCode);

        var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { });

        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var tokens = await refresh.Content.ReadFromJsonAsync<TokenResponse>(AtriumFactory.Json);
        Assert.Null(tokens!.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, (await GetWithTokenAsync(client, SessionsUrl, tokens.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Repeated_failures_block_further_attempts_even_with_the_right_password()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostLoginAsync(client, "ana", "wrong")).StatusCode);
        }

        // The sixth failure starts the block.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostLoginAsync(client, "ana", "wrong")).StatusCode);
        var blocked = await PostLoginAsync(client, "ana", Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("auth.too_many_attempts", await ProblemCodeAsync(blocked));
        Assert.True(blocked.Headers.RetryAfter?.Delta > TimeSpan.Zero);

        _factory.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(client, "ana", Password)).StatusCode);
    }

    [Fact]
    public async Task Failures_on_one_account_do_not_block_another()
    {
        await _factory.CreateUserAsync("ana", Password);
        await _factory.CreateUserAsync("marko", Password);
        using var client = NewClient();
        for (var i = 0; i < 6; i++)
        {
            await PostLoginAsync(client, "ana", "wrong");
        }

        Assert.Equal(HttpStatusCode.OK, (await PostLoginAsync(client, "marko", Password)).StatusCode);
    }

    private async Task<(string Challenge, byte[] Secret)> StartTotpSignInAsync(HttpClient client)
    {
        var user = await _factory.CreateUserAsync("ana", Password);
        var secret = await _factory.EnableTotpAsync(user);
        var response = await PostLoginAsync(client, "ana", Password);
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(AtriumFactory.Json);
        Assert.Equal(LoginStatus.TotpRequired, login!.Status);
        Assert.Null(login.Tokens);
        return (login.TotpChallenge!, secret);
    }

    private string CurrentCode(byte[] secret) =>
        TotpGenerator.CodeAt(secret, TotpGenerator.StepAt(_factory.Time.GetUtcNow()));

    private static async Task<HttpResponseMessage> PostTotpAsync(HttpClient client, string challenge, string code) =>
        await client.PostAsJsonAsync("/api/v1/auth/login/totp", new { challenge, code, device = Device() });

    [Fact]
    public async Task With_two_step_verification_the_password_alone_gives_no_tokens()
    {
        using var client = NewClient();
        var (challenge, secret) = await StartTotpSignInAsync(client);

        // The challenge is not an access token.
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetWithTokenAsync(client, SessionsUrl, challenge)).StatusCode);

        var response = await PostTotpAsync(client, challenge, CurrentCode(secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(AtriumFactory.Json);
        Assert.Equal(LoginStatus.SignedIn, login!.Status);
        Assert.Equal(HttpStatusCode.OK, (await GetWithTokenAsync(client, SessionsUrl, login.Tokens!.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task A_wrong_or_reused_code_is_refused()
    {
        using var client = NewClient();
        var (challenge, secret) = await StartTotpSignInAsync(client);
        var code = CurrentCode(secret);
        var wrong = code == "000000" ? "000001" : "000000";

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostTotpAsync(client, challenge, wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostTotpAsync(client, challenge, code)).StatusCode);
        var reuse = await PostTotpAsync(client, challenge, code);

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        Assert.Equal("auth.invalid_credentials", await ProblemCodeAsync(reuse));
    }

    [Fact]
    public async Task The_challenge_expires_and_cannot_be_forged()
    {
        using var client = NewClient();
        var (challenge, secret) = await StartTotpSignInAsync(client);

        var forged = await PostTotpAsync(client, challenge + "x", CurrentCode(secret));
        Assert.Equal("auth.invalid_challenge", await ProblemCodeAsync(forged));

        // An access token from a normal sign-in is not a challenge either.
        await _factory.CreateUserAsync("marko", Password);
        var marko = await SignInAsync(client, "marko");
        var swapped = await PostTotpAsync(client, marko.AccessToken, CurrentCode(secret));
        Assert.Equal("auth.invalid_challenge", await ProblemCodeAsync(swapped));

        _factory.Time.Advance(TimeSpan.FromMinutes(6));
        var expired = await PostTotpAsync(client, challenge, CurrentCode(secret));
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal("auth.invalid_challenge", await ProblemCodeAsync(expired));
    }

    [Fact]
    public async Task Guessing_codes_is_blocked_after_a_few_attempts()
    {
        using var client = NewClient();
        var (challenge, secret) = await StartTotpSignInAsync(client);
        var wrong = CurrentCode(secret) == "000000" ? "000001" : "000000";
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostTotpAsync(client, challenge, wrong)).StatusCode);
        }

        var blocked = await PostTotpAsync(client, challenge, CurrentCode(secret));

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }
}
