using Microsoft.Extensions.DependencyInjection;

namespace Atrium.Files;

public static class FilesSetup
{
    public static IServiceCollection AddAtriumFiles(this IServiceCollection services)
    {
        services.AddScoped<LibraryService>();
        return services;
    }
}
