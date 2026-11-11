using System.Text.Json;
using System.Text.Json.Serialization;
using Atrium.Api.Auth;
using Atrium.Api.Endpoints;
using Atrium.Api.Hosting;
using Atrium.Api.Setup;
using Atrium.Api.Storage;
using Atrium.Data;

var builder = WebApplication.CreateBuilder(args);

var bootstrap = BootstrapOptions.From(builder.Configuration);
Directory.CreateDirectory(bootstrap.ConfigDir);

builder.Services.AddSingleton(bootstrap);
builder.Services.AddAtriumDatabase(bootstrap);
builder.Services.AddAtriumAuth(bootstrap);
builder.Services.AddSingleton<SetupService>();
builder.Services.AddScoped<StorageLocationService>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi("v1");

// Enums travel as names, so adding a member later never changes the
// meaning of a value an older client already knows.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

var app = builder.Build();

await app.MigrateDatabaseAsync();
await app.LoadRevokedSessionsAsync();

// On a server with no admin yet, print the setup token to the log.
await using (var scope = app.Services.CreateAsyncScope())
{
    await app.Services.GetRequiredService<SetupService>().AnnounceAsync(
        scope.ServiceProvider.GetRequiredService<AtriumDbContext>(), app.Lifetime.ApplicationStopping);
}

app.UseSecurityHeaders();

// Before authorization: the web app's own files are public. They contain
// no user data, and the sign-in page has to load for someone signed out.
app.UseStaticFiles();

// Routing is placed here on purpose. If it ran first (the default), the
// web app's catch-all route would already be chosen for a script or style
// file, and the static file middleware would step aside for it.
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi().AllowAnonymous();
app.MapWebApp();

var v1 = app.MapGroup("/api/v1");
v1.MapSystemEndpoints();
v1.MapSetupEndpoints();
v1.MapAuthEndpoints();
v1.MapSessionEndpoints();
v1.MapPairingEndpoints();
v1.MapMeEndpoints();
v1.MapUserEndpoints();
v1.MapStorageLocationEndpoints();

await app.RunAsync();

/// <summary>Entry point, exposed so integration tests can host the server in memory.</summary>
public partial class Program;
