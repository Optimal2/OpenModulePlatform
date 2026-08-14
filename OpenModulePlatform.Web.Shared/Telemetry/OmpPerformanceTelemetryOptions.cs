// File: OpenModulePlatform.Web.Shared/Telemetry/OmpPerformanceTelemetryOptions.cs
namespace OpenModulePlatform.Web.Shared.Telemetry;

/// <summary>
/// Settings for the application performance telemetry, bound from <c>Portal:Telemetry</c>.
/// </summary>
/// <remarks>
/// Enabled by default. The data is only useful if it starts accumulating on the first day
/// an installation is used -- switching it on after someone asks a performance question
/// means the baseline that question needs no longer exists.
/// </remarks>
public sealed class OmpPerformanceTelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>Master switch. Off means no middleware, no hosted service, no writes.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often accumulated buckets are written. Short enough that a restart loses little.</summary>
    public int FlushIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// How long hourly rows are kept before being folded into the daily rollup. Two weeks
    /// answers "what is slow right now" with room to look back over a quiet period.
    /// </summary>
    public int RetainHours { get; set; } = 336;

    /// <summary>
    /// How long the daily rollup is kept. Deliberately long: the question this exists to
    /// answer is how load changed as an installation was taken into use, and that plays out
    /// over months, not days.
    /// </summary>
    public int RetainDays { get; set; } = 400;

    /// <summary>
    /// Requests faster than this are counted but contribute no duration sample, so the
    /// static-file noise floor does not drown the pages worth measuring. Zero measures
    /// everything.
    /// </summary>
    public int MinimumDurationMsToRecord { get; set; }

    public void Validate()
    {
        if (FlushIntervalSeconds < 5)
        {
            throw new InvalidOperationException("Telemetry:FlushIntervalSeconds must be at least 5.");
        }

        if (RetainHours < 1)
        {
            throw new InvalidOperationException("Telemetry:RetainHours must be at least 1.");
        }

        if (RetainDays < 1)
        {
            throw new InvalidOperationException("Telemetry:RetainDays must be at least 1.");
        }

        if (MinimumDurationMsToRecord < 0)
        {
            throw new InvalidOperationException("Telemetry:MinimumDurationMsToRecord cannot be negative.");
        }
    }
}
