// File: OpenModulePlatform.Web.Shared/Options/WebAppOptionsValidator.cs
using Microsoft.Extensions.Options;
using System.Globalization;

namespace OpenModulePlatform.Web.Shared.Options;

/// <summary>
/// Validates the shared web app options early so invalid portal URLs fail fast during startup.
/// </summary>
public sealed class WebAppOptionsValidator : IValidateOptions<WebAppOptions>
{
    private readonly string _sectionName;

    public WebAppOptionsValidator()
        : this(WebAppOptions.DefaultSectionName)
    {
    }

    /// <param name="sectionName">
    /// The configuration section the options are bound from, so failure messages name the
    /// key the operator must change (for example "Portal" for apps that bind that section).
    /// </param>
    public WebAppOptionsValidator(string sectionName)
    {
        _sectionName = string.IsNullOrWhiteSpace(sectionName)
            ? WebAppOptions.DefaultSectionName
            : sectionName;
    }

    public ValidateOptionsResult Validate(string? name, WebAppOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("WebApp options are required.");
        }

        var portalBaseUrl = options.PortalTopBar?.PortalBaseUrl ?? "/";
        if (!IsValidPortalBaseUrl(portalBaseUrl))
        {
            return ValidateOptionsResult.Fail(
                $"{_sectionName}:PortalTopBar:PortalBaseUrl must be an absolute URL or an app-root-relative path starting with '/'.");
        }

        foreach (var cultureName in options.SupportedCultures ?? [])
        {
            if (string.IsNullOrWhiteSpace(cultureName))
            {
                continue;
            }

            try
            {
                _ = new CultureInfo(cultureName);
            }
            catch (CultureNotFoundException)
            {
                // Request-cookie parsing deliberately ignores invalid culture names,
                // but invalid startup configuration must not be silently filtered by
                // CultureSelectionService while RequestLocalization throws elsewhere.
                return ValidateOptionsResult.Fail(
                    $"{_sectionName}:SupportedCultures contains invalid culture name '{cultureName}'.");
            }
        }

        return ValidateOptionsResult.Success;
    }

    private static bool IsValidPortalBaseUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return true;
        }

        return value.StartsWith("/", StringComparison.Ordinal)
            && !value.StartsWith("//", StringComparison.Ordinal);
    }
}
