using System.Reflection;
using System.Text.Json;
using Atrium.Media.Playback;

namespace Atrium.Media.Tests;

/// <summary>Every reason the server gives has a text in each client language (packages/shared).</summary>
public sealed class PlaybackReasonTextTests
{
    [Theory]
    [InlineData("en.json")]
    [InlineData("sr-Latn.json")]
    public void Every_reason_has_a_text(string locale)
    {
        var path = Path.Combine(RepositoryRoot(), "packages", "shared", "src", "i18n", "locales", locale);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var texts = document.RootElement.GetProperty("playback").GetProperty("reason");

        var codes = typeof(PlaybackReasons).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

        Assert.NotEmpty(codes);
        Assert.All(codes, code => Assert.True(texts.TryGetProperty(code, out _), $"{locale} has no text for {code}"));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pnpm-workspace.yaml")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
