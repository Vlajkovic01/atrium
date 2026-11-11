namespace Atrium.Core.Identity;

/// <summary>Languages every client ships: English and Serbian in Latin script (DESIGN.md 9, D-022).</summary>
public static class SupportedLocales
{
    public const string Default = "en";

    public static IReadOnlyList<string> All { get; } = ["en", "sr-Latn"];

    public static bool IsSupported(string? locale) => locale is not null && All.Contains(locale, StringComparer.Ordinal);
}
