using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atrium.Api.Tests;

/// <summary>
/// The OpenAPI document is the contract between the server and every
/// client; the clients are generated from the copy committed in
/// <c>packages/shared/openapi</c>. This test fails when the server's API
/// changed and that copy did not, so an API change always shows up in a
/// pull request as a change to the contract file.
/// </summary>
/// <remarks>
/// To update the copy after an intended API change, run the tests once with
/// the environment variable ATRIUM_UPDATE_OPENAPI set to 1.
/// </remarks>
public sealed class OpenApiContractTests : IDisposable
{
    private const string UpdateVariable = "ATRIUM_UPDATE_OPENAPI";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly AtriumFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_committed_contract_matches_the_server()
    {
        using var client = _factory.CreateClient();
        var actual = Normalize(await client.GetStringAsync("/openapi/v1.json"));
        var path = Path.Combine(RepositoryRoot(), "packages", "shared", "openapi", "atrium-v1.json");

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"{path} is missing. Run the tests once with {UpdateVariable}=1 to create it.");
        var committed = (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n");
        Assert.True(
            committed == actual,
            $"The server's API no longer matches packages/shared/openapi/atrium-v1.json. " +
            $"If the change is intended, run the tests once with {UpdateVariable}=1, regenerate the client " +
            "(pnpm --filter @atrium/shared generate) and commit both.");
    }

    [Fact]
    public async Task Every_operation_has_a_name_for_the_generated_client()
    {
        using var client = _factory.CreateClient();
        var document = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json"))!;

        var unnamed = new List<string>();
        foreach (var (path, item) in document["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                if (operation?["operationId"] is null)
                {
                    unnamed.Add($"{method.ToUpperInvariant()} {path}");
                }
            }
        }

        Assert.Empty(unnamed);
    }

    // The document names the host it was served from, which differs between
    // machines and says nothing about the API.
    private static string Normalize(string json)
    {
        var document = JsonNode.Parse(json)!.AsObject();
        document.Remove("servers");
        return document.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !(Directory.Exists(Path.Combine(directory.FullName, "server"))
            && Directory.Exists(Path.Combine(directory.FullName, "docs"))))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
