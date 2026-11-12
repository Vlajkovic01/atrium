using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Atrium.Api.Endpoints;
using Atrium.Api.Files;
using Atrium.Core.Files;
using Microsoft.EntityFrameworkCore;

namespace Atrium.Api.Tests;

public sealed class UploadApiTests : IDisposable
{
    private readonly AtriumFactory _factory = new();
    private readonly HttpClient _client;

    public UploadApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<(string Token, LibraryInfo Library)> SignInAsync(AtriumFactory factory, HttpClient client, string name)
    {
        var any = false;
        await factory.WithDbAsync(async db => any = await db.StorageLocations.AnyAsync());
        if (!any)
        {
            await factory.AddLocalStorageAsync();
        }

        await factory.CreateUserAsync(name, TestApi.Password);
        var token = (await client.SignInAsync(name)).AccessToken;
        var libraries = await (await client.SendAsync(HttpMethod.Get, "/api/v1/libraries", token)).ReadAsync<List<LibraryInfo>>();
        return (token, Assert.Single(libraries));
    }

    private Task<(string Token, LibraryInfo Library)> SignInAsync(string name) => SignInAsync(_factory, _client, name);

    private static string Metadata(params (string Key, string Value)[] pairs) =>
        string.Join(',', pairs.Select(p => $"{p.Key} {Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Value))}"));

    private static async Task<HttpResponseMessage> CreateAsync(HttpClient client, string token, long length, string metadata)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/uploads");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Tus-Resumable", "1.0.0");
        request.Headers.Add("Upload-Length", length.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("Upload-Metadata", metadata);
        return await client.SendAsync(request);
    }

    private Task<HttpResponseMessage> CreateAsync(string token, long length, string metadata) => CreateAsync(_client, token, length, metadata);

    private static async Task<HttpResponseMessage> PatchAsync(
        HttpClient client, string token, string location, long offset, byte[] chunk, string? checksum = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, location);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Tus-Resumable", "1.0.0");
        request.Headers.Add("Upload-Offset", offset.ToString(CultureInfo.InvariantCulture));
        if (checksum is not null)
        {
            request.Headers.Add("Upload-Checksum", checksum);
        }

        request.Content = new ByteArrayContent(chunk);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        return await client.SendAsync(request);
    }

    private Task<HttpResponseMessage> PatchAsync(string token, string location, long offset, byte[] chunk, string? checksum = null) =>
        PatchAsync(_client, token, location, offset, chunk, checksum);

    private static async Task<HttpResponseMessage> HeadAsync(HttpClient client, string token, string location)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, location);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Tus-Resumable", "1.0.0");
        return await client.SendAsync(request);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.Single() : throw new KeyNotFoundException(name);

    /// <summary>Reads the stored content of a file straight from the storage location, bypassing the server.</summary>
    private async Task<byte[]> StoredContentAsync(Guid nodeId)
    {
        byte[] hash = [];
        await _factory.WithDbAsync(async db =>
        {
            var node = await db.Nodes.SingleAsync(n => n.Id == nodeId);
            hash = (await db.Blobs.SingleAsync(b => b.Id == node.BlobId)).Sha256;
        });
        return await File.ReadAllBytesAsync(Path.Combine(_factory.StorageRoot, "main", Blob.KeyFor(hash)));
    }

    [Fact]
    public async Task Discovery_describes_the_protocol_without_signing_in()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/uploads");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("1.0.0", Header(response, "Tus-Version"));
        Assert.Contains("checksum", Header(response, "Tus-Extension"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_without_the_protocol_version_is_refused()
    {
        var (token, library) = await SignInAsync("ana");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/uploads");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Upload-Length", "3");
        request.Headers.Add("Upload-Metadata", Metadata(("parentId", library.RootNodeId.ToString()), ("name", "a.txt")));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task An_upload_in_chunks_resumes_from_the_reported_offset_and_becomes_a_file()
    {
        var (token, library) = await SignInAsync("ana");
        var content = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog.");
        var modified = new DateTimeOffset(2024, 5, 1, 10, 0, 0, TimeSpan.Zero);
        var created = await CreateAsync(token, content.Length, Metadata(
            ("parentId", library.RootNodeId.ToString()),
            ("name", "fox.txt"),
            ("modifiedAt", modified.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
            ("sha256", Convert.ToHexStringLower(SHA256.HashData(content)))));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var location = created.Headers.Location!.ToString();
        Assert.Equal("0", Header(created, "Upload-Offset"));

        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(token, location, 0, content[..10])).StatusCode);

        // The connection drops; the client asks where to continue.
        var head = await HeadAsync(_client, token, location);
        Assert.Equal("10", Header(head, "Upload-Offset"));

        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(token, location, 10, content[10..30])).StatusCode);
        var last = await PatchAsync(token, location, 30, content[30..]);

        Assert.Equal(HttpStatusCode.NoContent, last.StatusCode);
        Assert.Equal(content.Length.ToString(CultureInfo.InvariantCulture), Header(last, "Upload-Offset"));
        var nodeId = Guid.Parse(Header(last, UploadEndpoints.NodeIdHeader));
        var node = await (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{nodeId}", token)).ReadAsync<NodeInfo>();
        Assert.Equal("fox.txt", node.Name);
        Assert.Equal(content.Length, node.Size);
        Assert.Equal("text/plain", node.MediaType);
        Assert.Equal(modified, node.ModifiedAt);
        Assert.Equal(content, await StoredContentAsync(nodeId));
        Assert.Equal("Completed", Header(await HeadAsync(_client, token, location), UploadEndpoints.StateHeader));
    }

    [Fact]
    public async Task A_chunk_at_the_wrong_offset_is_refused_with_the_right_one()
    {
        var (token, library) = await SignInAsync("ana");
        var location = (await CreateAsync(token, 10, Metadata(("parentId", library.RootNodeId.ToString()), ("name", "a.bin")))).Headers.Location!.ToString();
        await PatchAsync(token, location, 0, new byte[4]);

        var wrong = await PatchAsync(token, location, 2, new byte[4]);

        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal("upload.offset_mismatch", await wrong.ProblemCodeAsync());
        Assert.Equal("4", Header(wrong, "Upload-Offset"));
    }

    [Fact]
    public async Task A_chunk_that_fails_its_checksum_is_discarded_and_can_be_sent_again()
    {
        var (token, library) = await SignInAsync("ana");
        var content = Encoding.UTF8.GetBytes("checksummed!");
        var location = (await CreateAsync(token, content.Length, Metadata(("parentId", library.RootNodeId.ToString()), ("name", "c.txt")))).Headers.Location!.ToString();
        var wrongSum = "sha1 " + Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes("something else")));
        var rightSum = "sha1 " + Convert.ToBase64String(SHA1.HashData(content));

        var bad = await PatchAsync(token, location, 0, content, wrongSum);
        Assert.Equal(460, (int)bad.StatusCode);
        Assert.Equal("0", Header(await HeadAsync(_client, token, location), "Upload-Offset"));

        var good = await PatchAsync(token, location, 0, content, rightSum);
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
        Assert.Equal(content, await StoredContentAsync(Guid.Parse(Header(good, UploadEndpoints.NodeIdHeader))));
    }

    [Fact]
    public async Task Content_that_differs_from_the_declared_hash_never_becomes_a_file()
    {
        var (token, library) = await SignInAsync("ana");
        var location = (await CreateAsync(token, 5, Metadata(
            ("parentId", library.RootNodeId.ToString()),
            ("name", "x.txt"),
            ("sha256", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("other")))))))
            .Headers.Location!.ToString();

        var response = await PatchAsync(token, location, 0, Encoding.UTF8.GetBytes("12345"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("upload.content_mismatch", await response.ProblemCodeAsync());
        var page = await (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{library.RootNodeId}/children", token)).ReadAsync<NodePage>();
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task A_taken_name_is_refused_kept_both_or_replaced_with_a_version()
    {
        var (token, library) = await SignInAsync("ana");
        async Task<HttpResponseMessage> UploadAsync(string text, string? onConflict)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var metadata = new List<(string, string)> { ("parentId", library.RootNodeId.ToString()), ("name", "report.txt") };
            if (onConflict is not null)
            {
                metadata.Add(("onConflict", onConflict));
            }

            var created = await CreateAsync(token, bytes.Length, Metadata([.. metadata]));
            return created.StatusCode == HttpStatusCode.Created
                ? await PatchAsync(token, created.Headers.Location!.ToString(), 0, bytes)
                : created;
        }

        var first = await UploadAsync("one", null);
        var refused = await UploadAsync("two", "fail");
        var both = await UploadAsync("three", "rename");
        var replaced = await UploadAsync("four", "replace");

        var original = Guid.Parse(Header(first, UploadEndpoints.NodeIdHeader));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("node.name_conflict", await refused.ProblemCodeAsync());
        var kept = await (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{Header(both, UploadEndpoints.NodeIdHeader)}", token)).ReadAsync<NodeInfo>();
        Assert.Equal("report (2).txt", kept.Name);
        Assert.Equal(original, Guid.Parse(Header(replaced, UploadEndpoints.NodeIdHeader)));
        Assert.Equal(Encoding.UTF8.GetBytes("four"), await StoredContentAsync(original));
        var versions = await (await _client.SendAsync(HttpMethod.Get, $"/api/v1/nodes/{original}/versions", token)).ReadAsync<List<VersionInfo>>();
        Assert.Equal(3, Assert.Single(versions).Size);
    }

    [Fact]
    public async Task An_upload_larger_than_the_quota_is_refused_before_any_byte_is_sent()
    {
        var (token, library) = await SignInAsync("ana");
        await _factory.WithDbAsync(db => db.Users.Where(u => u.Name == "ana").ExecuteUpdateAsync(s => s.SetProperty(u => u.QuotaBytes, 100L)));

        var response = await CreateAsync(token, 101, Metadata(("parentId", library.RootNodeId.ToString()), ("name", "big.bin")));

        Assert.Equal(HttpStatusCode.InsufficientStorage, response.StatusCode);
    }

    [Fact]
    public async Task A_cancelled_upload_accepts_nothing_more()
    {
        var (token, library) = await SignInAsync("ana");
        var location = (await CreateAsync(token, 10, Metadata(("parentId", library.RootNodeId.ToString()), ("name", "a.bin")))).Headers.Location!.ToString();
        await PatchAsync(token, location, 0, new byte[4]);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, location);
        delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        delete.Headers.Add("Tus-Resumable", "1.0.0");
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(delete)).StatusCode);

        Assert.Equal(HttpStatusCode.Gone, (await HeadAsync(_client, token, location)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await PatchAsync(token, location, 4, new byte[6])).StatusCode);
    }

    [Fact]
    public async Task Another_users_upload_does_not_exist_for_you()
    {
        var (anaToken, library) = await SignInAsync("ana");
        var (bobToken, _) = await SignInAsync("bob");
        var location = (await CreateAsync(anaToken, 10, Metadata(("parentId", library.RootNodeId.ToString()), ("name", "a.bin")))).Headers.Location!.ToString();

        Assert.Equal(HttpStatusCode.NotFound, (await HeadAsync(_client, bobToken, location)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(bobToken, location, 0, new byte[10])).StatusCode);
        var intoAnas = await CreateAsync(bobToken, 1, Metadata(("parentId", library.RootNodeId.ToString()), ("name", "x")));
        Assert.Equal(HttpStatusCode.NotFound, intoAnas.StatusCode);
    }

    [Fact]
    public async Task An_empty_file_is_finished_at_creation()
    {
        var (token, library) = await SignInAsync("ana");

        var created = await CreateAsync(token, 0, Metadata(("parentId", library.RootNodeId.ToString()), ("name", "empty.txt")));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var nodeId = Guid.Parse(Header(created, UploadEndpoints.NodeIdHeader));
        Assert.Empty(await StoredContentAsync(nodeId));
    }

    [Fact]
    public async Task Bad_metadata_is_refused()
    {
        var (token, library) = await SignInAsync("ana");

        var noName = await CreateAsync(token, 3, Metadata(("parentId", library.RootNodeId.ToString())));
        var badName = await CreateAsync(token, 3, Metadata(("parentId", library.RootNodeId.ToString()), ("name", "../evil")));
        var badHash = await CreateAsync(token, 3, Metadata(("parentId", library.RootNodeId.ToString()), ("name", "a"), ("sha256", "zz")));

        Assert.Equal("upload.invalid_metadata", await noName.ProblemCodeAsync());
        Assert.Equal("node.invalid_name", await badName.ProblemCodeAsync());
        Assert.Equal("upload.invalid_metadata", await badHash.ProblemCodeAsync());
    }

    [Fact]
    public async Task The_hash_check_answers_only_about_your_own_files()
    {
        var (anaToken, anaLibrary) = await SignInAsync("ana");
        var (bobToken, _) = await SignInAsync("bob");
        var content = Encoding.UTF8.GetBytes("a photo only ana has");
        var location = (await CreateAsync(anaToken, content.Length, Metadata(("parentId", anaLibrary.RootNodeId.ToString()), ("name", "p.jpg")))).Headers.Location!.ToString();
        await PatchAsync(anaToken, location, 0, content);
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var unknown = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("nobody has this")));

        var ana = await (await _client.SendAsync(HttpMethod.Post, "/api/v1/uploads/check", anaToken, new { hashes = new[] { hash, unknown } })).ReadAsync<HashCheckResponse>();
        var bob = await (await _client.SendAsync(HttpMethod.Post, "/api/v1/uploads/check", bobToken, new { hashes = new[] { hash } })).ReadAsync<HashCheckResponse>();

        Assert.Equal([hash], ana.Owned);
        Assert.Empty(bob.Owned);
    }

    [Fact]
    public async Task An_upload_continues_after_the_server_restarts()
    {
        var configDir = Path.Combine(Path.GetTempPath(), "atrium-tests", Guid.NewGuid().ToString("N"));
        var time = new MutableTime();
        var content = Encoding.UTF8.GetBytes("survives a restart of the whole server");
        string location;
        string storageRoot;
        using (var before = new AtriumFactory(configDir, time))
        {
            var client = before.CreateClient();
            var (token, library) = await SignInAsync(before, client, "ana");
            location = (await CreateAsync(client, token, content.Length, Metadata(("parentId", library.RootNodeId.ToString()), ("name", "r.txt")))).Headers.Location!.ToString();
            await PatchAsync(client, token, location, 0, content[..12]);
            storageRoot = before.StorageRoot;
        }

        try
        {
            using var after = new AtriumFactory(configDir, time, ownsConfigDir: true);
            var client = after.CreateClient();
            var token = (await client.SignInAsync("ana")).AccessToken;

            Assert.Equal("12", Header(await HeadAsync(client, token, location), "Upload-Offset"));
            var done = await PatchAsync(client, token, location, 12, content[12..]);

            Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);
            var nodeId = Guid.Parse(Header(done, UploadEndpoints.NodeIdHeader));
            byte[] hash = [];
            await after.WithDbAsync(async db => hash = (await db.Blobs.SingleAsync(b => b.Id == db.Nodes.Single(n => n.Id == nodeId).BlobId)).Sha256);
            Assert.Equal(SHA256.HashData(content), hash);
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(storageRoot, "main", Blob.KeyFor(hash))));
        }
        finally
        {
            try
            {
                Directory.Delete(storageRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
