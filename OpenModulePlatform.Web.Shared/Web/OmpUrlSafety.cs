// File: OpenModulePlatform.Web.Shared/Web/OmpUrlSafety.cs
namespace OpenModulePlatform.Web.Shared.Web;

/// <summary>
/// The single canonical place where a URL that came out of the database, a configuration
/// value or an HTTP payload is decided to be safe to emit as an href, a src or a redirect.
/// </summary>
/// <remarks>
/// This class exists because the same three-line check was duplicated across the platform and
/// hardened one copy at a time in five separate rounds -- R3-E6, R4-E2, R5S-E1, R6-E1 and
/// R7-E3/S1 -- and every round found another copy that had been missed. R8's pattern sweep
/// found a fifth server-side copy in PortalEntryService that no round had ever touched, plus
/// unguarded reads in the iFrame module, the OpenDocViewer base URL and the portal base URL.
/// Duplicating the rule is the defect; callers delegate here instead (R8-P1).
///
/// Three facts about .NET's URI parsing drive the implementation, all verified empirically
/// rather than assumed:
/// <list type="bullet">
/// <item>Uri.TryCreate and Uri.IsWellFormedUriString both succeed for javascript:, data: and
/// vbscript:. Neither is a safety check.</item>
/// <item>Uri.TryCreate("//evil.host/x", UriKind.Absolute, ...) succeeds with scheme "file",
/// so a protocol-relative value never reaches a relative-path branch placed after it.</item>
/// <item>new Uri("javascript:alert(1)").AbsolutePath returns "alert(1)" without throwing, so
/// any authorization check that inspects AbsolutePath silently passes such a value.</item>
/// </list>
/// </remarks>
public static class OmpUrlSafety
{
    /// <summary>
    /// True when an absolute URI uses a scheme we are willing to emit into an href or src.
    /// </summary>
    public static bool IsAllowedAbsoluteScheme(Uri uri)
        => uri.Scheme is "http" or "https";

    /// <summary>
    /// True when <paramref name="value"/> is a same-origin, absolute-path destination —
    /// the only kind we assign to window.location or follow in a redirect.
    /// </summary>
    public static bool IsSafeLocalDestination(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // Must be an absolute path, not "//host", not "/\host", and carry no scheme.
        return value[0] == '/'
            && (value.Length < 2 || (value[1] != '/' && value[1] != '\\'));
    }

    /// <summary>
    /// Returns <paramref name="value"/> when it is safe to emit as an href/src, otherwise null.
    /// Absolute values must use http or https; relative values must not smuggle in a scheme,
    /// a backslash or a protocol-relative authority.
    /// </summary>
    public static string? SanitizeHref(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            return IsAllowedAbsoluteScheme(absolute) ? absolute.ToString() : null;
        }

        return IsSafeRelativeHref(trimmed) ? trimmed : null;
    }

    /// <summary>
    /// True for a relative value that cannot be reinterpreted as an absolute one.
    /// </summary>
    /// <remarks>
    /// The colon check is what stops "javascript:alert(1)" in callers that reach the relative
    /// branch first, and what R7-S1 added to AppLinkBuilder. A leading slash makes a colon
    /// harmless (it is then a path segment), which is why the check is conditional.
    /// </remarks>
    public static bool IsSafeRelativeHref(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();

        if (trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        return !trimmed.Contains(':', StringComparison.Ordinal)
            || trimmed.StartsWith('/');
    }

    /// <summary>
    /// Returns a configured base URL only when it is safe to build hrefs on top of, otherwise
    /// null so the caller can fall back to its own default.
    /// </summary>
    /// <remarks>
    /// Covers omp.Hosts.BaseUrl (R7-E3), PortalBaseUrl and the OpenDocViewer base URL
    /// (R8-P1-5, R8-P1-6). An unknown scheme with "//" is given an authority by .NET, so
    /// "javascript://x" survives GetLeftPart(UriPartial.Authority) unless the scheme is
    /// checked first.
    /// </remarks>
    public static string? SanitizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            return IsAllowedAbsoluteScheme(absolute) ? trimmed : null;
        }

        return IsSafeRelativeHref(trimmed) ? trimmed : null;
    }
}
