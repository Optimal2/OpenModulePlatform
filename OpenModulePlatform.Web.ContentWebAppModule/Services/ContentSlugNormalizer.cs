// File: OpenModulePlatform.Web.ContentWebAppModule/Services/ContentSlugNormalizer.cs
using System.Globalization;
using System.Text;

namespace OpenModulePlatform.Web.ContentWebAppModule.Services;

public static class ContentSlugNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "/")
        {
            return string.Empty;
        }

        var segments = value
            .Trim()
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSegment)
            .Where(segment => segment.Length > 0)
            .ToArray();

        return string.Join('/', segments);
    }

    private static string NormalizeSegment(string segment)
    {
        var normalized = segment.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var previousDash = false;

        foreach (var character in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(character);
            var appendDash = false;
            if (lower is >= 'a' and <= 'z' || lower is >= '0' and <= '9')
            {
                builder.Append(lower);
                previousDash = false;
            }
            else if (lower is '-' or '_' or ' ' or '.')
            {
                appendDash = true;
            }

            if (appendDash && !previousDash && builder.Length > 0)
            {
                builder.Append('-');
                previousDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }
}
