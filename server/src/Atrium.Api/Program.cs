using Atrium.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi("v1");

var app = builder.Build();

app.MapOpenApi();

var v1 = app.MapGroup("/api/v1");
v1.MapSystemEndpoints();

app.Run();

/// <summary>Entry point, exposed so integration tests can host the server in memory.</summary>
public partial class Program;
