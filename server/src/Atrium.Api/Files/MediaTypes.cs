using Microsoft.AspNetCore.StaticFiles;

namespace Atrium.Api.Files;

/// <summary>Media types by file extension: the web server's table, plus phone and camera formats it does not know.</summary>
public static class MediaTypes
{
    public const string Unknown = "application/octet-stream";

    private static readonly FileExtensionContentTypeProvider Provider = CreateProvider();

    public static string For(string fileName) =>
        Provider.TryGetContentType(fileName, out var type) ? type : Unknown;

    private static FileExtensionContentTypeProvider CreateProvider()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".heic"] = "image/heic";
        provider.Mappings[".heif"] = "image/heif";
        provider.Mappings[".avif"] = "image/avif";
        provider.Mappings[".webp"] = "image/webp";
        provider.Mappings[".dng"] = "image/x-adobe-dng";
        provider.Mappings[".mkv"] = "video/x-matroska";
        provider.Mappings[".m4v"] = "video/x-m4v";
        provider.Mappings[".3gp"] = "video/3gpp";
        provider.Mappings[".mts"] = "video/mp2t";
        provider.Mappings[".m2ts"] = "video/mp2t";
        return provider;
    }
}
