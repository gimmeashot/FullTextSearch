using System.Globalization;
using System.Text;

namespace FullTextSearch.Core;

public static class TextAnalyzer
{
    public static IReadOnlyList<string> Analyze(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var normalized = text.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var tokens = new List<string>();
        var current = new StringBuilder();

        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsLetterOrDigit(rune) || category == UnicodeCategory.NonSpacingMark)
            {
                current.Append(rune);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }
}
