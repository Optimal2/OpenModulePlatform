// File: OpenModulePlatform.WorkerManager.WindowsService/Models/WorkerManagerSettingsValidator.cs
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.WorkerManager.WindowsService.Models;

/// <summary>
/// Runs the WorkerManager settings rules at startup through the standard
/// options-validation chain. The rules stay on <see cref="WorkerManagerSettings.Validate"/>;
/// this validator only adapts them to <see cref="ValidateOptionsResult"/>.
/// </summary>
public sealed class WorkerManagerSettingsValidator : IValidateOptions<WorkerManagerSettings>
{
    public ValidateOptionsResult Validate(string? name, WorkerManagerSettings options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("Worker manager settings are required.");
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
