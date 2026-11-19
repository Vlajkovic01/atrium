using System.Text.Json;
using System.Text.Json.Serialization;
using Atrium.Core.Media;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Atrium.Data;

/// <summary>
/// The streams of a video as one JSON text column, the same on SQLite and
/// PostgreSQL and in the portable backup (D-008). Names are camel case and
/// enums are words, so the stored text stays readable and survives a
/// reordering of an enum.
/// </summary>
internal static class MediaStreamsJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static readonly ValueConverter<IReadOnlyList<MediaStream>, string> Converter = new(
        streams => JsonSerializer.Serialize(streams, Options),
        text => Read(text));

    public static readonly ValueComparer<IReadOnlyList<MediaStream>> Comparer = new(
        (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
        streams => streams.Aggregate(0, (hash, stream) => HashCode.Combine(hash, stream.GetHashCode())),
        streams => streams.ToList());

    private static IReadOnlyList<MediaStream> Read(string text) =>
        string.IsNullOrEmpty(text) ? [] : JsonSerializer.Deserialize<List<MediaStream>>(text, Options) ?? [];
}
