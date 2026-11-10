namespace Atrium.Core.Identity;

/// <summary>Languages every client ships (DESIGN.md 9, D-018).</summary>
public static class SupportedLocales
{
    public const string Default = "en";

    public static IReadOnlyList<string> All { get; } = ["en", "sr-Latn", "sr-Cyrl"];

    public static bool IsSupported(string? locale) => locale is not null && All.Contains(locale, StringComparer.Ordinal);
}
