// File: OpenModulePlatform.WorkerProcessHost/Models/WorkerProcessSettingsValidator.cs
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.WorkerProcessHost.Models;

/// <summary>
/// Runs the worker process settings rules at startup through the standard
/// options-validation chain. The rules stay on <see cref="WorkerProcessSettings.Validate"/>;
/// this validator only adapts them to <see cref="ValidateOptionsResult"/>. Validation
/// also applies the deliberate normalizations in <see cref="WorkerProcessSettings.Validate"/>
/// (default WorkerInstanceId, canonical ShutdownEventName) to the cached options instance.
/// </summary>
public sealed class WorkerProcessSettingsValidator : IValidateOptions<WorkerProcessSettings>
{
    public ValidateOptionsResult Validate(string? name, WorkerProcessSettings options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("Worker process settings are required.");
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
