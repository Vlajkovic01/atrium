using System.Net;
using System.Net.Http.Json;
using Atrium.Api.Endpoints;
using Atrium.Core.Identity;
using Atrium.Identity.Totp;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Atrium.Api.Tests;

public sealed class MeTests : IDisposable
{
    private const string Password = TestApi.Password;
    private const string NewPassword = "a brand new passphrase";

    private readonly AtriumFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private string CodeFor(string base32Secret)
    {
        Assert.True(Base32.TryDecode(base32Secret, out var secret));
        return TotpGenerator.CodeAt(secret, TotpGenerator.StepAt(_factory.Time.GetUtcNow()));
    }

    [Fact]
    public async Task The_profile_shows_the_account_and_nothing_secret()
    {
        var ana = await _factory.CreateUserAsync("Ana", Password, UserRole.Admin);
        using var client = NewClient();
        var tokens = await client.SignInAsync("ana");

        var response = await client.SendAsync(HttpMethod.Get, "/api/v1/me", tokens.AccessToken);

        var profile = await response.ReadAsync<UserProfile>();
        Assert.Equal(ana.Id, profile.Id);
        Assert.Equal("Ana", profile.Name);
        Assert.Equal(UserRole.Admin, profile.Role);
        Assert.Equal("en", profile.Locale);
        Assert.False(profile.TotpEnabled);
        Assert.False(profile.PasswordResetByAdmin);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("argon2", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Display_name_and_language_can_be_changed_and_are_validated()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        var tokens = await client.SignInAsync("ana");

        var updated = await client.SendAsync(HttpMethod.Patch, "/api/v1/me", tokens.AccessToken,
            new { displayName = "  Ana Anić ", locale = "sr-Latn" });
        var badLocale = await client.SendAsync(HttpMethod.Patch, "/api/v1/me", tokens.AccessToken, new { locale = "sr-Cyrl" });
        var emptyName = await client.SendAsync(HttpMethod.Patch, "/api/v1/me", tokens.AccessToken, new { displayName = "   " });

        var profile = await updated.ReadAsync<UserProfile>();
        Assert.Equal("Ana Anić", profile.DisplayName);
        Assert.Equal("sr-Latn", profile.Locale);
        Assert.Equal("user.invalid_locale", await badLocale.ProblemCodeAsync());
        Assert.Equal("user.invalid_display_name", await emptyName.ProblemCodeAsync());
        var after = await (await client.SendAsync(HttpMethod.Get, "/api/v1/me", tokens.AccessToken)).ReadAsync<UserProfile>();
        Assert.Equal("Ana Anić", after.DisplayName);
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_and_signs_out_other_devices()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        var laptop = await client.SignInAsync("ana");
        var phone = await client.SignInAsync("ana");

        var wrong = await client.SendAsync(HttpMethod.Post, "/api/v1/me/password", laptop.AccessToken,
            new { currentPassword = "not my password", newPassword = NewPassword });
        var weak = await client.SendAsync(HttpMethod.Post, "/api/v1/me/password", laptop.AccessToken,
            new { currentPassword = Password, newPassword = "short" });
        var changed = await client.SendAsync(HttpMethod.Post, "/api/v1/me/password", laptop.AccessToken,
            new { currentPassword = Password, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal("auth.wrong_password", await wrong.ProblemCodeAsync());
        Assert.Equal("user.weak_password", await weak.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        // The device that made the change stays signed in; the other is out.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(HttpMethod.Get, "/api/v1/me", laptop.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(HttpMethod.Get, "/api/v1/me", phone.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = phone.RefreshToken })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.LoginAsync("ana", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync("ana", NewPassword)).StatusCode);
    }

    [Fact]
    public async Task The_change_password_form_cannot_be_used_to_guess_the_password()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        var tokens = await client.SignInAsync("ana");
        for (var i = 0; i < 6; i++)
        {
            await client.SendAsync(HttpMethod.Post, "/api/v1/me/password", tokens.AccessToken,
                new { currentPassword = $"guess {i}", newPassword = NewPassword });
        }

        var blocked = await client.SendAsync(HttpMethod.Post, "/api/v1/me/password", tokens.AccessToken,
            new { currentPassword = Password, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.LoginAsync("ana", Password)).StatusCode);
    }

    private async Task<(TokenResponse Tokens, string Secret, IReadOnlyList<string> RecoveryCodes)> EnrollAsync(HttpClient client)
    {
        await _factory.CreateUserAsync("ana", Password);
        var tokens = await client.SignInAsync("ana");
        var begin = await (await client.SendAsync(HttpMethod.Post, "/api/v1/me/totp", tokens.AccessToken)).ReadAsync<TotpEnrollmentResponse>();
        var confirm = await client.SendAsync(HttpMethod.Post, "/api/v1/me/totp/confirm", tokens.AccessToken, new { code = CodeFor(begin.Secret) });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        return (tokens, begin.Secret, (await confirm.ReadAsync<RecoveryCodesResponse>()).Codes);
    }

    [Fact]
    public async Task Two_step_verification_is_enforced_only_after_the_authenticator_is_proven()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        var tokens = await client.SignInAsync("ana");

        var begin = await (await client.SendAsync(HttpMethod.Post, "/api/v1/me/totp", tokens.AccessToken)).ReadAsync<TotpEnrollmentResponse>();
        Assert.StartsWith("otpauth://totp/Atrium:ana?secret=" + begin.Secret, begin.ProvisioningUri, StringComparison.Ordinal);

        // Not confirmed yet: sign-in still works with the password alone, so
        // an abandoned setup cannot lock the user out.
        await client.SignInAsync("ana");
        var wrong = await client.SendAsync(HttpMethod.Post, "/api/v1/me/totp/confirm", tokens.AccessToken, new { code = "000000" });
        Assert.Equal("auth.invalid_code", await wrong.ProblemCodeAsync());
        await client.SignInAsync("ana");

        var confirm = await client.SendAsync(HttpMethod.Post, "/api/v1/me/totp/confirm", tokens.AccessToken, new { code = CodeFor(begin.Secret) });
        var codes = (await confirm.ReadAsync<RecoveryCodesResponse>()).Codes;
        Assert.Equal(10, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[BCDFGHJKLMNPQRSTVWXZ]{5}-[BCDFGHJKLMNPQRSTVWXZ]{5}$", c));

        var login = await (await client.LoginAsync("ana")).ReadAsync<LoginResponse>();
        Assert.Equal(LoginStatus.TotpRequired, login.Status);
        var profile = await (await client.SendAsync(HttpMethod.Get, "/api/v1/me", tokens.AccessToken)).ReadAsync<UserProfile>();
        Assert.True(profile.TotpEnabled);
        var again = await client.SendAsync(HttpMethod.Post, "/api/v1/me/totp", tokens.AccessToken);
        Assert.Equal("auth.totp_already_enabled", await again.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_recovery_code_signs_in_exactly_once()
    {
        using var client = NewClient();
        var (_, _, codes) = await EnrollAsync(client);
        var challenge = (await (await client.LoginAsync("ana")).ReadAsync<LoginResponse>()).TotpChallenge;
        var typed = codes[0].ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal);

        var first = await client.PostAsJsonAsync("/api/v1/auth/login/totp",
            new { challenge, code = typed, device = new { name = "Phone", kind = "mobile" } });
        var second = await client.PostAsJsonAsync("/api/v1/auth/login/totp",
            new { challenge, code = codes[0], device = new { name = "Phone", kind = "mobile" } });
        var other = await client.PostAsJsonAsync("/api/v1/auth/login/totp",
            new { challenge, code = codes[1], device = new { name = "Phone", kind = "mobile" } });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Turning_two_step_verification_off_needs_the_password()
    {
        using var client = NewClient();
        var (tokens, _, codes) = await EnrollAsync(client);

        var wrong = await client.SendAsync(HttpMethod.Post, "/api/v1/me/totp/disable", tokens.AccessToken, new { password = "nope nope" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(LoginStatus.TotpRequired, (await (await client.LoginAsync("ana")).ReadAsync<LoginResponse>()).Status);

        var disabled = await client.SendAsync(HttpMethod.Post, "/api/v1/me/totp/disable", tokens.AccessToken, new { password = Password });
        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        await client.SignInAsync("ana");

        // Old recovery codes die with the credential: enrolling again and
        // presenting one of them must fail.
        var begin = await (await client.SendAsync(HttpMethod.Post, "/api/v1/me/totp", tokens.AccessToken)).ReadAsync<TotpEnrollmentResponse>();
        _factory.Time.Advance(TimeSpan.FromSeconds(90));
        tokens = await client.SignInAsync("ana");
        await client.SendAsync(HttpMethod.Post, "/api/v1/me/totp/confirm", tokens.AccessToken, new { code = CodeFor(begin.Secret) });
        var challenge = (await (await client.LoginAsync("ana")).ReadAsync<LoginResponse>()).TotpChallenge;
        var stale = await client.PostAsJsonAsync("/api/v1/auth/login/totp",
            new { challenge, code = codes[0], device = new { name = "Phone", kind = "mobile" } });
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
    }

    [Fact]
    public async Task A_restricted_session_can_read_the_profile_but_not_change_the_account()
    {
        await _factory.CreateUserAsync("ana", Password);
        using var client = NewClient();
        var phone = await client.SignInAsync("ana");
        var started = await (await client.PostAsJsonAsync("/api/v1/auth/pairing",
            new { device = new { name = "TV", kind = "tv" } })).ReadAsync<StartPairingResponse>();
        await client.SendAsync(HttpMethod.Post, $"/api/v1/auth/pairing/{started.UserCode}/approve", phone.AccessToken, new { readOnly = true });
        var tv = (await (await client.PostAsJsonAsync("/api/v1/auth/pairing/token", new { deviceCode = started.DeviceCode }))
            .ReadAsync<PairingTokenResponse>()).Tokens!;

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(HttpMethod.Get, "/api/v1/me", tv.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(HttpMethod.Patch, "/api/v1/me", tv.AccessToken, new { displayName = "Hacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(HttpMethod.Post, "/api/v1/me/password", tv.AccessToken,
                new { currentPassword = Password, newPassword = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(HttpMethod.Post, "/api/v1/me/totp", tv.AccessToken)).StatusCode);
    }
}
