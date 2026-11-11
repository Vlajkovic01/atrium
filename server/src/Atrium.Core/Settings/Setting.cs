namespace Atrium.Core.Settings;

/// <summary>One server setting, edited through the wizard or admin UI.</summary>
public sealed class Setting
{
    public required string Key { get; init; }

    /// <summary>JSON. Encrypted with the server key when <see cref="IsSecret"/> is set.</summary>
    public required string Value { get; set; }

    /// <summary>Secret values are write-only through the API and never logged.</summary>
    public bool IsSecret { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}
