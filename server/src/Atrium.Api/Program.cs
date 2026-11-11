using Atrium.Api.Endpoints;
using Atrium.Api.Hosting;

var builder = WebApplication.CreateBuilder(args);

var bootstrap = BootstrapOptions.From(builder.Configuration);
Directory.CreateDirectory(bootstrap.ConfigDir);

builder.Services.AddSingleton(bootstrap);
builder.Services.AddAtriumDatabase(bootstrap);
builder.Services.AddOpenApi("v1");

var app = builder.Build();

await app.MigrateDatabaseAsync();

app.MapOpenApi();

var v1 = app.MapGroup("/api/v1");
v1.MapSystemEndpoints();

await app.RunAsync();

/// <summary>Entry point, exposed so integration tests can host the server in memory.</summary>
public partial class Program;
