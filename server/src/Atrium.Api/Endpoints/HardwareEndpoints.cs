using Atrium.Api.Auth;
using Atrium.Jobs;
using Atrium.Media.Streaming;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Atrium.Api.Endpoints;

/// <summary>What the machine is and which video encoders work on it (DESIGN.md 7.5, D-064).</summary>
/// <param name="Encoders">Each candidate's test encode, software last; empty until the first detection has run.</param>
/// <param name="Chosen">The encoder streams use.</param>
/// <param name="LastFailure">The last time a hardware encoder failed during a stream and software took over.</param>
public sealed record HardwareView(
    int ProcessorCount,
    long MemoryBytes,
    HardwareProfile Profile,
    IReadOnlyList<EncoderTest> Encoders,
    VideoEncoderKind Chosen,
    DateTimeOffset? DetectedAt,
    string? LastFailure,
    int MaxSoftwareEncodes,
    int MaxHardwareEncodes);

public static class HardwareEndpoints
{
    public static IEndpointRouteBuilder MapHardwareEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/admin/hardware")
            .WithTags("Administration")
            .RequireAuthorization(AuthSetup.AdminPolicy, AuthSetup.FullSessionPolicy);
        group.MapGet("/", Get).WithName("GetHardware");
        group.MapPost("/detect", DetectAsync)
            .WithName("DetectHardware")
            .WithDescription("Tests every candidate video encoder again with a one-second encode, as after changing the machine or its drivers.");
        return routes;
    }

    private static Ok<HardwareView> Get(HardwareInfo hardware, VideoEncoders encoders, StreamLimits limits) =>
        TypedResults.Ok(View(hardware, encoders, limits));

    private static async Task<Ok<HardwareView>> DetectAsync(HardwareInfo hardware, VideoEncoders encoders, StreamLimits limits, CancellationToken ct)
    {
        await encoders.DetectAsync(ct);
        return TypedResults.Ok(View(hardware, encoders, limits));
    }

    private static HardwareView View(HardwareInfo hardware, VideoEncoders encoders, StreamLimits limits) => new(
        hardware.ProcessorCount,
        hardware.MemoryBytes,
        hardware.Profile,
        encoders.Tests,
        encoders.Chosen,
        encoders.DetectedAt,
        encoders.LastFailure,
        limits.MaxEncodes,
        limits.MaxHardwareEncodes);
}
