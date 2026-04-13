using OpenModulePlatform.Web.Shared.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using System.Globalization;

namespace OpenModulePlatform.Web.Shared.Localization;

/// <summary>
/// Tracks the user's global preferred culture separately from the effective culture supported by the current web app.
/// </summary>
public sealed class CultureSelectionService
{
    public const string PreferredCultureCookieName = "OMP.PreferredCulture";

    public CultureSelectionResult Resolve(WebAppOptions options, HttpRequest request)
    {
        var supportedCultures = GetSupportedCultures(options);
        var preferredCulture = GetPreferredCulture(request, options);
        var effectiveCulture = ResolveEffectiveCulture(preferredCulture, supportedCultures, options);

        return new CultureSelectionResult
        {
            PreferredCulture = preferredCulture,
            EffectiveCulture = effectiveCulture
        };
    }

    public CultureSelectionResult ResolveFromCurrentCulture(WebAppOptions options)
    {
        var currentCulture = string.IsNullOrWhiteSpace(CultureInfo.CurrentUICulture.Name)
            ? GetDefaultCulture(options)
            : CultureInfo.CurrentUICulture.Name;

        return new CultureSelectionResult
        {
            PreferredCulture = currentCulture,
            EffectiveCulture = currentCulture
        };
    }

    public string NormalizePreferredCulture(string? culture, WebAppOptions options)
    {
        var normalized = NormalizeCultureName(culture);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        var currentCulture = NormalizeCultureName(CultureInfo.CurrentUICulture.Name);
        return currentCulture ?? GetDefaultCulture(options);
    }

    public string ResolveEffectiveCulture(string preferredCulture, WebAppOptions options)
        => ResolveEffectiveCulture(preferredCulture, GetSupportedCultures(options), options);

    public void ApplyCookies(HttpResponse response, string preferredCulture, string effectiveCulture)
    {
        var cookieOptions = new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddYears(1),
            IsEssential = true,
            HttpOnly = false,
            SameSite = SameSiteMode.Lax,
            Secure = true,
            Path = "/"
        };

        response.Cookies.Append(PreferredCultureCookieName, preferredCulture, cookieOptions);
        response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(effectiveCulture)),
            cookieOptions);
    }

    private string GetPreferredCulture(HttpRequest request, WebAppOptions options)
    {
        if (request.Cookies.TryGetValue(PreferredCultureCookieName, out var preferredCookie))
        {
            var normalizedPreferred = NormalizeCultureName(preferredCookie);
            if (!string.IsNullOrWhiteSpace(normalizedPreferred))
            {
                return normalizedPreferred;
            }
        }

        if (request.Cookies.TryGetValue(CookieRequestCultureProvider.DefaultCookieName, out var requestCultureCookie))
        {
            var parsed = CookieRequestCultureProvider.ParseCookieValue(requestCultureCookie);
            var fromCookie = NormalizeCultureName(parsed?.UICultures.FirstOrDefault().Value)
                ?? NormalizeCultureName(parsed?.Cultures.FirstOrDefault().Value);

            if (!string.IsNullOrWhiteSpace(fromCookie))
            {
                return fromCookie;
            }
        }

        var currentCulture = NormalizeCultureName(CultureInfo.CurrentUICulture.Name);
        return currentCulture ?? GetDefaultCulture(options);
    }

    private static string[] GetSupportedCultures(WebAppOptions options)
    {
        var configured = options.SupportedCultures ?? [];
        var cultures = configured
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => NormalizeCultureName(x))
            .OfType<string>()
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return cultures.Length == 0
            ? [GetDefaultCulture(options)]
            : cultures!;
    }

    private static string ResolveEffectiveCulture(string preferredCulture, string[] supportedCultures, WebAppOptions options)
    {
        var normalizedPreferred = NormalizeCultureName(preferredCulture) ?? GetDefaultCulture(options);

        var directMatch = supportedCultures.FirstOrDefault(c => string.Equals(c, normalizedPreferred, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(directMatch))
        {
            return directMatch;
        }

        var preferredLanguage = TryGetLanguage(normalizedPreferred);
        if (!string.IsNullOrWhiteSpace(preferredLanguage))
        {
            var languageMatch = supportedCultures.FirstOrDefault(c => string.Equals(TryGetLanguage(c), preferredLanguage, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(languageMatch))
            {
                return languageMatch;
            }
        }

        var englishMatch = supportedCultures.FirstOrDefault(c => string.Equals(c, "en-US", StringComparison.OrdinalIgnoreCase))
            ?? supportedCultures.FirstOrDefault(c => string.Equals(TryGetLanguage(c), "en", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(englishMatch))
        {
            return englishMatch;
        }

        var defaultCulture = NormalizeCultureName(options.DefaultCulture);
        var defaultMatch = supportedCultures.FirstOrDefault(c => string.Equals(c, defaultCulture, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(defaultMatch))
        {
            return defaultMatch;
        }

        return supportedCultures[0];
    }

    private static string GetDefaultCulture(WebAppOptions options)
        => NormalizeCultureName(options.DefaultCulture) ?? "en-US";

    private static string? NormalizeCultureName(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(culture.Trim()).Name;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static string? TryGetLanguage(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(culture).TwoLetterISOLanguageName;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}
