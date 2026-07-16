// File: OpenModulePlatform.Web.Shared/Options/OmpAuthOptionsValidator.cs
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.Shared.Options;

/// <summary>
/// Validates the shared OMP authentication options early so a misconfigured
/// OIDC provider fails fast during startup instead of being silently ignored.
/// The rules mirror what the OIDC sign-in flow requires when the provider is
/// enabled; the cookie settings all have safe fallbacks and need no rules.
/// </summary>
public sealed class OmpAuthOptionsValidator : IValidateOptions<OmpAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, OmpAuthOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("OMP auth options are required.");
        }

        var oidc = options.Oidc;
        if (oidc is null || !oidc.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(oidc.Authority) &&
            string.IsNullOrWhiteSpace(oidc.MetadataAddress))
        {
            return ValidateOptionsResult.Fail("OmpAuth:Oidc requires Authority or MetadataAddress.");
        }

        if (string.IsNullOrWhiteSpace(oidc.ClientId))
        {
            return ValidateOptionsResult.Fail("OmpAuth:Oidc requires ClientId.");
        }

        if (string.IsNullOrWhiteSpace(oidc.ClientSecret))
        {
            return ValidateOptionsResult.Fail("OmpAuth:Oidc requires ClientSecret.");
        }

        if (!string.Equals(oidc.ResponseType?.Trim(), "code", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail("OmpAuth:Oidc only supports authorization-code response type.");
        }

        return ValidateOptionsResult.Success;
    }
}
