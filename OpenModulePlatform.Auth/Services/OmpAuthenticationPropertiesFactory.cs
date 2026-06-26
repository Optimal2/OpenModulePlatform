using Microsoft.AspNetCore.Authentication;
using OpenModulePlatform.Auth.Models;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Auth.Services;

public sealed class OmpAuthenticationPropertiesFactory
{
    private readonly OmpConfigurationService _configuration;
    private readonly ILogger<OmpAuthenticationPropertiesFactory> _log;

    public OmpAuthenticationPropertiesFactory(
        OmpConfigurationService configuration,
        ILogger<OmpAuthenticationPropertiesFactory> log)
    {
        _configuration = configuration;
        _log = log;
    }

    public async Task<AuthenticationProperties> CreateAsync(
        OmpAuthenticatedUser user,
        CancellationToken ct)
    {
        var properties = new AuthenticationProperties();
        await ApplyAsync(properties, user, ct);
        return properties;
    }

    public async Task ApplyAsync(
        AuthenticationProperties properties,
        OmpAuthenticatedUser user,
        CancellationToken ct)
    {
        var rawValue = await _configuration.GetGlobalStringAsync(
            OmpAuthDefaults.ConfigurationCategory,
            OmpAuthSessionLifetimeDefaults.ProviderSessionLifetimesSetting,
            ct);
        var config = OmpAuthSessionLifetimeConfig.Parse(rawValue);
        var lifetimeMinutes = config.ResolveMinutes(user.ProviderId);
        var issuedUtc = DateTimeOffset.UtcNow;

        properties.IsPersistent = true;
        properties.IssuedUtc = issuedUtc;
        properties.ExpiresUtc = issuedUtc.AddMinutes(lifetimeMinutes);

        if (config.UsedWholeSettingFallback && !string.IsNullOrWhiteSpace(rawValue))
        {
            _log.LogWarning(
                "OMP auth session lifetime setting {ConfigCategory}/{ConfigSetting} is invalid; using the built-in default of {DefaultMinutes} minutes.",
                OmpAuthDefaults.ConfigurationCategory,
                OmpAuthSessionLifetimeDefaults.ProviderSessionLifetimesSetting,
                OmpAuthSessionLifetimeDefaults.BuiltInDefaultMinutes);
        }

        if (config.IgnoredEntryCount > 0)
        {
            _log.LogDebug(
                "Ignored {IgnoredEntryCount} invalid OMP auth session lifetime entr{EntrySuffix}.",
                config.IgnoredEntryCount,
                config.IgnoredEntryCount == 1 ? "y" : "ies");
        }
    }
}
