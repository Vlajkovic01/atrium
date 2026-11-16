namespace Atrium.Files;

/// <summary>
/// The media type of a file from its name. The host supplies the mapping
/// (the web server's table plus the camera formats it lacks), so this
/// project needs nothing from HTTP.
/// </summary>
public sealed class MediaTypeResolver(Func<string, string> resolve)
{
    public string For(string fileName) => resolve(fileName);
}
