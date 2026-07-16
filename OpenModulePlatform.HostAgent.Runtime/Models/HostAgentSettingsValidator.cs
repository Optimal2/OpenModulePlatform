using Microsoft.Extensions.Options;

namespace OpenModulePlatform.HostAgent.Runtime.Models;

/// <summary>
/// Runs the HostAgent settings rules at startup through the standard options-validation
/// chain. The rules stay on <see cref="HostAgentSettings.Validate"/> because the
/// Bootstrapper (no DI) calls them directly; this validator only adapts them to
/// <see cref="ValidateOptionsResult"/>.
/// </summary>
public sealed class HostAgentSettingsValidator : IValidateOptions<HostAgentSettings>
{
    public ValidateOptionsResult Validate(string? name, HostAgentSettings options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("Host agent settings are required.");
        }

        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }
}
