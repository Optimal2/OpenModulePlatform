// File: OpenModulePlatform.Web.Shared/Options/WebAppOptionsValidator.cs
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.Shared.Options;

/// <summary>
/// Validates the shared web app options early so invalid portal URLs fail fast during startup.
/// </summary>
public sealed class WebAppOptionsValidator : IValidateOptions<WebAppOptions>
{
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
                "WebApp:PortalTopBar:PortalBaseUrl must be an absolute URL or an app-root-relative path starting with '/'.");
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
