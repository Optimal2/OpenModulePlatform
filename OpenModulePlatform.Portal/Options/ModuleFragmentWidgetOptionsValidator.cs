using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Portal.Options;

/// <summary>
/// Rejects unsafe fragment endpoint configuration at startup and on options reload.
/// </summary>
public sealed class ModuleFragmentWidgetOptionsValidator : IValidateOptions<ModuleFragmentWidgetOptions>
{
    public ValidateOptionsResult Validate(string? name, ModuleFragmentWidgetOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.InternalBaseUrl))
        {
            return ValidateOptionsResult.Success;
        }

        if (!Uri.TryCreate(options.InternalBaseUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return ValidateOptionsResult.Fail(
                "ModuleFragmentWidgets:InternalBaseUrl must be an absolute HTTP or HTTPS URL.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return ValidateOptionsResult.Fail(
                "ModuleFragmentWidgets:InternalBaseUrl must not contain user information (username or password).");
        }

        return ValidateOptionsResult.Success;
    }
}
