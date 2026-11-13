namespace Atrium.Core.Settings;

/// <summary>Settings rows that belong to one person: their menu and saved views (D-041).</summary>
public static class UserPreferenceKeys
{
    public static string Navigation(Guid userId) => $"prefs.nav:{userId:N}";

    public static string Views(Guid userId) => $"prefs.views:{userId:N}";

    /// <summary>Every key of the person, removed with the account.</summary>
    public static IReadOnlyList<string> All(Guid userId) => [Navigation(userId), Views(userId)];
}
