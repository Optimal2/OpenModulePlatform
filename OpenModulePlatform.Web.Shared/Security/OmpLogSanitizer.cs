namespace OpenModulePlatform.Web.Shared.Security;

/// <summary>
/// Makes request-supplied text safe to place in a log entry.
/// </summary>
/// <remarks>
/// CR, LF and every other control character become a space, so a client can
/// never fabricate additional log lines or hide a line behind a carriage
/// return, and the value is capped so an oversized body cannot flood the log.
/// Introduced for CodeQL cs/log-forging on the anonymous CSP report endpoint
/// (alerts #11 and #12, 2026-09-03), where both the content type and the body
/// come straight from the browser.
/// </remarks>
public static class OmpLogSanitizer
{
    public const string TruncationMarker = "...[truncated]";

    public static string ForLog(string? value, int maxLength = 512)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (maxLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, "maxLength must be at least 1.");
        }

        // The explicit CR/LF replacements are what a log-forging analysis looks
        // for; the control-character pass covers the rest (VT, FF, BEL, ESC ...).
        var text = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            // Control covers C0 and C1 (including NEL, U+0085); the Unicode line and
            // paragraph separators (U+2028, U+2029) are categorised separately and
            // are line breaks to some log viewers, so they go the same way.
            if (char.IsControl(chars[i]) || IsUnicodeLineBreak(chars[i]))
            {
                chars[i] = ' ';
            }
        }

        text = new string(chars);
        return text.Length <= maxLength
            ? text
            : string.Concat(text.AsSpan(0, maxLength), TruncationMarker);
    }

    private static bool IsUnicodeLineBreak(char c)
    {
        var category = char.GetUnicodeCategory(c);
        return category is System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator;
    }
}
