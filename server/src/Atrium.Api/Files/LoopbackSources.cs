using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;

namespace Atrium.Api.Files;

/// <summary>
/// URLs on this machine through which FFmpeg reads a file kept on remote
/// storage, such as S3 (DESIGN.md 7.4, D-063). FFmpeg is given a URL, never
/// storage credentials, and the server reads the storage for it with range
/// requests, as for any client. Each URL names one file by a random token
/// that lasts as long as the stream it was made for, and is answered only
/// to a connection from this machine.
/// </summary>
public sealed class LoopbackSources(IServer server, TimeProvider time) : Atrium.Media.Streaming.IToolInputs
{
    public const string Route = "/api/v1/internal/sources";

    /// <summary>A token not yet given to a stream lasts this long, for the keyframe scan before the stream opens.</summary>
    private static readonly TimeSpan Unbound = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// The server's own address as seen from this machine: the first plain
    /// HTTP address it listens on, with a wildcard host turned into
    /// loopback. Null when it listens on none, as under a test host.
    /// </summary>
    public string? BaseAddress
    {
        get
        {
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
            foreach (var address in addresses.OrderBy(a => a.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
            {
                var text = address.Replace("://+", "://localhost", StringComparison.Ordinal).Replace("://*", "://localhost", StringComparison.Ordinal);
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                {
                    continue;
                }

                var host = uri.Host switch
                {
                    "0.0.0.0" or "localhost" => "127.0.0.1",
                    "[::]" or "::" => "[::1]",
                    _ => uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.Host.Trim('[', ']')}]" : uri.Host,
                };
                return string.Create(CultureInfo.InvariantCulture, $"{uri.Scheme}://{host}:{uri.Port}");
            }

            return null;
        }
    }

    /// <summary>A URL FFmpeg can read the node's content from, or null when the server has no address to give.</summary>
    public (string Url, string Token)? Register(Guid nodeId)
    {
        if (BaseAddress is not { } address)
        {
            return null;
        }

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(18));
        _entries[token] = new Entry(nodeId, null, time.GetUtcNow());
        return ($"{address}{Route}/{token}", token);
    }

    /// <summary>Ties a token to the stream it is for: it lasts while <paramref name="alive"/> says the stream does.</summary>
    public void Bind(string token, Func<bool> alive)
    {
        if (_entries.TryGetValue(token, out var entry))
        {
            _entries[token] = entry with { Alive = alive };
        }
    }

    public void Remove(string token) => _entries.TryRemove(token, out _);

    /// <summary>For background tools: a URL that lasts while <paramref name="alive"/> says so.</summary>
    public string? UrlFor(Guid nodeId, Func<bool> alive)
    {
        if (Register(nodeId) is not { } registered)
        {
            return null;
        }

        Bind(registered.Token, alive);
        return registered.Url;
    }

    /// <summary>The node a token is for, while it lasts; a token that ended is forgotten.</summary>
    public Guid? Find(string token)
    {
        if (!_entries.TryGetValue(token, out var entry))
        {
            return null;
        }

        var lasts = entry.Alive is { } alive ? alive() : time.GetUtcNow() - entry.Created < Unbound;
        if (!lasts)
        {
            _entries.TryRemove(token, out _);
            return null;
        }

        return entry.NodeId;
    }

    /// <summary>Only FFmpeg on this machine may read through these URLs.</summary>
    public static bool FromThisMachine(HttpContext http) =>
        http.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote);

    private sealed record Entry(Guid NodeId, Func<bool>? Alive, DateTimeOffset Created);
}
