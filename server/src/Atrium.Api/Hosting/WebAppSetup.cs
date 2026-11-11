namespace Atrium.Api.Hosting;

/// <summary>
/// Serves the web app from the same process as the API (D-009) and sets
/// the browser security headers (DESIGN.md 6.9).
/// </summary>
public static class WebAppSetup
{
    // Everything the web app loads comes from this server. No third-party
    // origins, no inline script, no framing by other sites. Images may be
    // data: and blob: URLs because QR codes and, later, thumbnails are
    // built in the browser.
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self'; " +
        "img-src 'self' data: blob:; " +
        "media-src 'self' blob:; " +
        "connect-src 'self'; " +
        "font-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            await next();
        });

    /// <summary>
    /// Static files of the built web app, and the app's start page for any
    /// path that is not the API, so a link such as /devices or /pair?code=…
    /// opens the app. Unknown API paths stay 404; they must never be
    /// answered with a web page.
    /// </summary>
    public static void MapWebApp(this WebApplication app)
    {
        var startPage = new StaticFileOptions
        {
            // The start page names the current script files, so it must
            // never be served stale after an upgrade.
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
        };

        // The root needs its own route: the pattern below does not match an empty path.
        app.MapFallbackToFile("/", "index.html", startPage).AllowAnonymous();
        app.MapFallbackToFile("{*path:regex(^(?!api(/|$)|openapi(/|$)).*$)}", "index.html", startPage).AllowAnonymous();
    }
}
