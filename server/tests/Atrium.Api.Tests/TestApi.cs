using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Atrium.Api.Endpoints;

namespace Atrium.Api.Tests;

/// <summary>Small helpers for talking to the server the way a client does.</summary>
internal static class TestApi
{
    public const string Password = "correct horse battery";

    public static async Task<HttpResponseMessage> LoginAsync(
        this HttpClient client, string name, string password = Password, string kind = "mobile") =>
        await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            name,
            password,
            device = new { name = "Test device", kind },
        });

    public static async Task<TokenResponse> SignInAsync(this HttpClient client, string name, string password = Password)
    {
        var response = await client.LoginAsync(name, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(AtriumFactory.Json);
        Assert.Equal(LoginStatus.SignedIn, login!.Status);
        return login.Tokens!;
    }

    public static async Task<HttpResponseMessage> SendAsync(
        this HttpClient client, HttpMethod method, string url, string? accessToken, object? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(AtriumFactory.Json))!;

    public static async Task<string?> ProblemCodeAsync(this HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
