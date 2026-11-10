using System.Text;

namespace Atrium.Core.Identity;

/// <summary>Rules for sign-in names.</summary>
public static class UserName
{
    public const int MinLength = 2;
    public const int MaxLength = 64;

    /// <summary>
    /// Validates a sign-in name and produces the form stored for display and
    /// the key used for uniqueness and lookup. Two names that differ only by
    /// letter case or Unicode composition get the same key, so "Ana" and
    /// "ana" cannot both exist.
    /// </summary>
    public static bool TryNormalize(string? input, out string name, out string key)
    {
        name = string.Empty;
        key = string.Empty;
        if (input is null)
        {
            return false;
        }

        var trimmed = input.Trim().Normalize(NormalizationForm.FormC);
        if (trimmed.Length is < MinLength or > MaxLength)
        {
            return false;
        }

        var hasLetterOrDigit = false;
        foreach (var c in trimmed)
        {
            if (char.IsLetterOrDigit(c))
            {
                hasLetterOrDigit = true;
            }
            else if (c is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        // Names made only of punctuation ("..", "--") are confusing and
        // look like path fragments.
        if (!hasLetterOrDigit)
        {
            return false;
        }

        name = trimmed;
        key = KeyOf(trimmed);
        return true;
    }

    /// <summary>Lookup key for a name typed at sign-in. Does not validate.</summary>
    public static string KeyOf(string input) =>
        input.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
}
