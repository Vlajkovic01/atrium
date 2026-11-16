using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Atrium.Api.Endpoints;
using Atrium.Api.Storage;
using Atrium.Files;
using Atrium.Jobs;
using Atrium.Storage;

namespace Atrium.Api.Tests;

/// <summary>
/// The server sends error codes and the clients translate them. This test
/// ties the two together: every code the server can send must have a
/// message in every language the clients ship, so a new error can never
/// reach a user as "Something went wrong".
/// </summary>
public sealed partial class ErrorCodeTranslationTests
{
    // Every class that declares error codes. A new class of codes is added here.
    private static readonly Type[] CodeSources =
    [
        typeof(ApiProblem),
        typeof(SetupEndpoints),
        typeof(StorageErrors),
        typeof(StorageHealthCodes),
        typeof(JobEndpoints),
        typeof(JobErrorCodes),
        typeof(FileErrors),
        typeof(Atrium.Files.Uploads.UploadErrors),
        typeof(UploadEndpoints),
        typeof(Atrium.Files.External.LibraryErrors),
        typeof(Atrium.Media.MediaErrors),
        typeof(Atrium.Media.Photos.AlbumErrors),
        typeof(NavigationEndpoints),
    ];

    public static TheoryData<string> Locales() => ["en", "sr-Latn"];

    private static SortedSet<string> ServerCodes()
    {
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in CodeSources)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                if (field is { IsLiteral: true } && field.GetRawConstantValue() is string value && CodePattern().IsMatch(value))
                {
                    codes.Add(value);
                }
            }
        }

        return codes;
    }

    [Fact]
    public void The_server_declares_its_error_codes_where_this_test_can_find_them()
    {
        var codes = ServerCodes();

        // A few known ones from each source, so an empty or broken scan fails loudly.
        Assert.Contains("auth.invalid_credentials", codes);
        Assert.Contains("user.last_admin", codes);
        Assert.Contains("setup.token_required", codes);
        Assert.Contains("storage.location_in_use", codes);
        Assert.Contains("storage.bucket_not_found", codes);
        Assert.True(codes.Count >= 30, $"Only {codes.Count} codes found.");
    }

    [Theory]
    [MemberData(nameof(Locales))]
    public void Every_error_code_has_a_message(string locale)
    {
        using var messages = JsonDocument.Parse(File.ReadAllText(LocaleFile(locale)));
        var errors = messages.RootElement.GetProperty("errors");

        var missing = new List<string>();
        foreach (var code in ServerCodes())
        {
            // "storage.root_missing" lives at errors.storage.root_missing.
            var parts = code.Split('.');
            if (!errors.TryGetProperty(parts[0], out var group)
                || !group.TryGetProperty(parts[1], out var message)
                || string.IsNullOrWhiteSpace(message.GetString()))
            {
                missing.Add(code);
            }
        }

        Assert.True(missing.Count == 0, $"No {locale} message for: {string.Join(", ", missing)}");
    }

    private static string LocaleFile(string locale)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "packages", "shared")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "packages", "shared", "src", "i18n", "locales", $"{locale}.json");
    }

    [GeneratedRegex("^[a-z]+\\.[a-z_]+$")]
    private static partial Regex CodePattern();
}
