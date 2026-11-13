using System.Text.Json;
using System.Text.Json.Serialization;
using Atrium.Api.Auth;
using Atrium.Api.Endpoints;
using Atrium.Api.Files;
using Atrium.Api.Hosting;
using Atrium.Api.Setup;
using Atrium.Api.Storage;
using Atrium.Data;
using Atrium.Files;
using Atrium.Files.External;
using Atrium.Jobs;
using Atrium.Media;

var builder = WebApplication.CreateBuilder(args);

// The container's health check runs this same program with one argument.
if (args.Contains(HealthCheckCommand.Argument))
{
    return await HealthCheckCommand.RunAsync(builder.Configuration);
}

var bootstrap = BootstrapOptions.From(builder.Configuration);

// Checks the media tools without starting the server or opening any data.
if (args.Contains(MediaSelfCheck.Argument))
{
    return await MediaSelfCheck.RunAsync(new MediaOptions { CacheDir = bootstrap.CacheDir }, Console.Out);
}
Directory.CreateDirectory(bootstrap.ConfigDir);

// Held until the process ends. A second server on the same directory stops here.
using var instanceLock = InstanceLock.Acquire(bootstrap.ConfigDir);

builder.Services.AddSingleton(bootstrap);
builder.Services.AddAtriumDatabase(bootstrap);
builder.Services.AddAtriumAuth(bootstrap);
builder.Services.AddSingleton<SetupService>();
builder.Services.AddScoped<StorageLocationService>();
builder.Services.AddAtriumJobs();
builder.Services.AddJobWorker();

// External libraries must stay clear of Atrium's own directories.
builder.Services.AddSingleton(new ExternalLibraryOptions([bootstrap.ConfigDir, bootstrap.CacheDir]));
builder.Services.AddSingleton(new MediaTypeResolver(MediaTypes.For));
builder.Services.AddAtriumFiles();
builder.Services.AddAtriumMedia(new MediaOptions { CacheDir = bootstrap.CacheDir });
builder.Services.AddSingleton<ThumbnailGate>();
builder.Services.AddSingleton<SignedUrls>();
builder.Services.AddSingleton<ArchiveRequests>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi("v1");

// Enums travel as names, so adding a member later never changes the
// meaning of a value an older client already knows.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

var app = builder.Build();

// Restoring a backup replaces starting the server, under the same lock.
if (RestoreCommand.TryGetPath(args, out var backupPath))
{
    return await RestoreCommand.RunAsync(app.Services, backupPath, bootstrap, Console.Out);
}

await app.MigrateDatabaseAsync();
await app.LoadRevokedSessionsAsync();

// On a server with no admin yet, print the setup token to the log.
await using (var scope = app.Services.CreateAsyncScope())
{
    await app.Services.GetRequiredService<SetupService>().AnnounceAsync(
        scope.ServiceProvider.GetRequiredService<AtriumDbContext>(), app.Lifetime.ApplicationStopping);
}

// First in the pipeline, so everything after it sees the real client
// address and scheme. Only applied for proxies the installer listed.
app.UseTrustedProxies(bootstrap);

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
v1.MapJobEndpoints();
v1.MapLibraryEndpoints();
v1.MapNodeEndpoints();
v1.MapTrashEndpoints();
v1.MapUploadEndpoints();
v1.MapContentEndpoints();
v1.MapBackupEndpoints();
v1.MapIntegrityEndpoints();
v1.MapBackgroundWorkEndpoints();
v1.MapAdminLibraryEndpoints();
v1.MapMediaEndpoints();
v1.MapPhotoEndpoints();

await app.RunAsync();
return 0;

/// <summary>Entry point, exposed so integration tests can host the server in memory.</summary>
public partial class Program;
