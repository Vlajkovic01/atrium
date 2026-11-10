using System.Text.Json;
using System.Text.Json.Serialization;
using Atrium.Api.Auth;
using Atrium.Api.Endpoints;
using Atrium.Api.Hosting;

var builder = WebApplication.CreateBuilder(args);

var bootstrap = BootstrapOptions.From(builder.Configuration);
Directory.CreateDirectory(bootstrap.ConfigDir);

builder.Services.AddSingleton(bootstrap);
builder.Services.AddAtriumDatabase(bootstrap);
builder.Services.AddAtriumAuth(bootstrap);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi("v1");

// Enums travel as names, so adding a member later never changes the
// meaning of a value an older client already knows.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

var app = builder.Build();

await app.MigrateDatabaseAsync();
await app.LoadRevokedSessionsAsync();

app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi().AllowAnonymous();

var v1 = app.MapGroup("/api/v1");
v1.MapSystemEndpoints();
v1.MapAuthEndpoints();
v1.MapSessionEndpoints();
v1.MapPairingEndpoints();

await app.RunAsync();

/// <summary>Entry point, exposed so integration tests can host the server in memory.</summary>
public partial class Program;
