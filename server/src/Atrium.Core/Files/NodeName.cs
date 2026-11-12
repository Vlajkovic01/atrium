using System.Text;

namespace Atrium.Core.Files;

/// <summary>
/// Rules for file and folder names (DESIGN.md 3.5, D-011). A name must be
/// usable as a file name on Windows, macOS and Linux, so an export to a
/// normal folder tree never has to rename anything.
/// </summary>
public static class NodeName
{
    public const int MaxLength = 255;

    private static readonly HashSet<string> ReservedOnWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Validates a name and returns it NFC-normalized, with its key: the form
    /// two siblings must not share. Names keep their case; "Report.pdf" and
    /// "report.pdf" cannot sit side by side.
    /// </summary>
    public static bool TryNormalize(string? input, out string name, out string key)
    {
        name = string.Empty;
        key = string.Empty;
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        var normalized = input.Normalize(NormalizationForm.FormC);
        if (normalized.Length > MaxLength || normalized is "." or "..")
        {
            return false;
        }

        foreach (var c in normalized)
        {
            // Separators would make one name look like a path; control
            // characters, NUL included, are invisible and break tools.
            if (c is '/' or '\\' || char.IsControl(c))
            {
                return false;
            }
        }

        // Windows silently drops a trailing dot or space, which would turn
        // two different names into one on export.
        if (normalized[^1] is '.' or ' ' || normalized.Trim().Length == 0)
        {
            return false;
        }

        // "CON" and "con.txt" cannot be created on Windows.
        var stem = normalized.Split('.')[0].TrimEnd(' ');
        if (ReservedOnWindows.Contains(stem))
        {
            return false;
        }

        name = normalized;
        key = KeyOf(normalized);
        return true;
    }

    /// <summary>The comparison key of an already valid name.</summary>
    public static string KeyOf(string name) =>
        name.Normalize(NormalizationForm.FormC).ToUpperInvariant().ToLowerInvariant();

    /// <summary>
    /// "Report.pdf" becomes "Report (2).pdf", "Report (3).pdf" and so on, for
    /// the "keep both" choice. The extension is kept so the file still opens.
    /// </summary>
    public static string Numbered(string name, int number)
    {
        var dot = name.LastIndexOf('.');
        var hasExtension = dot > 0 && dot < name.Length - 1;
        var stem = hasExtension ? name[..dot] : name;
        var extension = hasExtension ? name[dot..] : string.Empty;
        var suffix = $" ({number})";
        var room = MaxLength - suffix.Length - extension.Length;
        if (stem.Length > room)
        {
            stem = stem[..Math.Max(1, room)];

            // Never cut a character made of two UTF-16 units in half.
            if (stem.Length > 1 && char.IsHighSurrogate(stem[^1]))
            {
                stem = stem[..^1];
            }
        }

        return stem + suffix + extension;
    }
}
