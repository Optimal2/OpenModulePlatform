// File: OpenModulePlatform.Auth/Services/OmpSelfRegistrationStatusCheck.cs
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Auth.Services;

/// <summary>
/// R7-F17 follow-up: turns the raw auth/selfRegistrationEnabled configuration
/// read into an operator-facing status. Self-registration is opt-in and the
/// seed only inserts the value, so an installation seeded while the default
/// was 'true' keeps running with registration on until an operator changes it.
/// This status feeds the startup warning and the /runtime-versions report so
/// that state is visible instead of silent.
/// </summary>
internal static class OmpSelfRegistrationStatusCheck
{
    public static OmpSelfRegistrationStatus Evaluate(OmpConfigurationRead read)
    {
        if (read.Failed)
        {
            return new OmpSelfRegistrationStatus(
                Enabled: null,
                Warning: "The self-registration setting (auth/selfRegistrationEnabled) could not " +
                    "be read, so the effective state is unknown. Readers fail closed (disabled) " +
                    "until the value can be read.");
        }

        var enabled = OmpAuthDefaults.ParseEnabledConfigValue(read.Value, defaultValue: false);
        if (!enabled)
        {
            return new OmpSelfRegistrationStatus(Enabled: false, Warning: null);
        }

        return new OmpSelfRegistrationStatus(
            Enabled: true,
            Warning: "Self-registration is ENABLED (auth/selfRegistrationEnabled = true): anyone " +
                "can create an OMP account from the login page and account settings. If this is " +
                "not intended, set the value to false; see docs/AUTHENTICATION_AND_RBAC.md, " +
                "\"Operations and Upgrade Notes\".");
    }
}

internal sealed record OmpSelfRegistrationStatus(bool? Enabled, string? Warning);
