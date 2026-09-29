using System.Text;

namespace Aevo.CoreApi.Data;

/// <summary>
/// Deterministic Thai/English/alias normalization for V1 matching and search
/// fixtures. It intentionally does not transliterate or infer meaning.
/// </summary>
public static class PlaceTextNormalizer
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (char.IsPunctuation(character) || char.IsSymbol(character)) continue;
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(character);
        }

        return builder.ToString().Trim();
    }

    public static IReadOnlyList<string> NormalizeAliases(IEnumerable<string> values)
    {
        return values
            .Select(Normalize)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
