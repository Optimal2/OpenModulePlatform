using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenModulePlatform.Web.Shared.Services;

/// <summary>Presentation and calendar boundaries only; stored values and API instants remain UTC.</summary>
public sealed class OmpTime
{
    public const string ConfigurationKey = "OmpTime:TimeZoneId";
    private readonly TimeZoneInfo _zone;
    private readonly TimeProvider _clock;
    private readonly bool _centralEuropean;
    private readonly ILogger<OmpTime>? _logger;
    private readonly bool _rejectLocalPresentation;

    public OmpTime(string timeZoneId = "UTC", TimeProvider? clock = null)
        : this(timeZoneId, clock, null, rejectLocalPresentation: true)
    {
    }

    internal OmpTime(string timeZoneId, TimeProvider? clock, ILogger<OmpTime>? logger, bool rejectLocalPresentation)
    {
        try
        {
            _zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            if (timeZoneId != "UTC" && !_zone.HasIanaId)
            {
                throw new TimeZoneNotFoundException("Use an IANA identifier, not a Windows time zone name.");
            }
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            throw new InvalidOperationException($"Invalid {ConfigurationKey} '{timeZoneId}'. Configure an installed IANA time zone such as Europe/Stockholm, or UTC.", ex);
        }

        TimeZoneId = timeZoneId;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
        _rejectLocalPresentation = rejectLocalPresentation;
        TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var windowsId);
        _centralEuropean = windowsId is "W. Europe Standard Time" or "Central Europe Standard Time" or "Central European Standard Time" or "Romance Standard Time";
    }

    public string TimeZoneId { get; }
    public DateOnly Today => DateOnly.FromDateTime(ToDisplayTime(_clock.GetUtcNow()).DateTime);

    public string UtcIso(DateTime? utc)
        => utc is { } value && !MarkInvalidPresentation(value)
            ? ToDisplayTime(value).UtcDateTime.ToString("O", CultureInfo.InvariantCulture) : string.Empty;

    public DateTimeOffset ToDisplayTime(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, _zone);

    /// <summary>SQL datetime values have Unspecified kind but represent UTC. Local kind is rejected.</summary>
    public DateTimeOffset ToDisplayTime(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Local)
        {
            throw new ArgumentException("Expected UTC or a SQL UTC value with Unspecified kind.", nameof(utc));
        }

        return ToDisplayTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));
    }

    public string Format(DateTime? utc, string format = "yyyy-MM-dd HH:mm:ss", IFormatProvider? culture = null)
        => utc is { } value ? Format(value, format, culture) : string.Empty;

    public string Format(DateTime utc, string format = "yyyy-MM-dd HH:mm:ss", IFormatProvider? culture = null)
        => MarkInvalidPresentation(utc) ? "[Invalid time: Local]" : FormatDisplay(ToDisplayTime(utc), format, culture);

    // Only text presentation may degrade. Conversions always reject Local values,
    // and an invalid value must never acquire a plausible timestamp or sort key.
    private bool MarkInvalidPresentation(DateTime value)
    {
        if (value.Kind != DateTimeKind.Local || _rejectLocalPresentation || _logger is null) return false;
        _logger.LogWarning("Rejected DateTimeKind.Local in time presentation for {TimeZoneId}; expected UTC or SQL UTC with Unspecified kind.", TimeZoneId);
        return true;
    }

    public string Format(DateTimeOffset utc, string format = "yyyy-MM-dd HH:mm:ss", IFormatProvider? culture = null)
        => FormatDisplay(ToDisplayTime(utc), format, culture);

    private string FormatDisplay(DateTimeOffset local, string format, IFormatProvider? culture)
        => $"{local.ToString(format, culture ?? CultureInfo.CurrentCulture)} {Abbreviation(local)}";

    private string Abbreviation(DateTimeOffset local)
    {
        if (_centralEuropean && local.Offset == TimeSpan.FromHours(1)) return "CET";
        if (_centralEuropean && local.Offset == TimeSpan.FromHours(2)) return "CEST";
        if (local.Offset == TimeSpan.Zero) return "UTC";
        // Other zones use an unambiguous offset rather than inventing an abbreviation.
        return "UTC" + local.ToString("zzz", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Converts a calendar input to UTC. A repeated minute selects the first occurrence
    /// for a lower bound and the last for an upper bound. Missing minutes are rejected.
    /// </summary>
    public DateTime ToUtc(DateTime wallTime, bool upperBound = false)
    {
        if (wallTime.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException("Calendar input must have Unspecified kind.", nameof(wallTime));
        }
        if (_zone.IsInvalidTime(wallTime))
        {
            throw new ArgumentException($"The selected time does not exist in {TimeZoneId}.", nameof(wallTime));
        }
        if (_zone.IsAmbiguousTime(wallTime))
        {
            var offsets = _zone.GetAmbiguousTimeOffsets(wallTime);
            return new DateTimeOffset(wallTime, upperBound ? offsets.Min() : offsets.Max()).UtcDateTime;
        }
        return TimeZoneInfo.ConvertTimeToUtc(wallTime, _zone);
    }

    public DateTime StartOfDayUtc(DateOnly date)
    {
        var start = date.ToDateTime(TimeOnly.MinValue);
        // Some zones advance at midnight (or skip a whole date). A day boundary
        // means the first existing minute, unlike a manually entered missing time.
        while (_zone.IsInvalidTime(start)) start = start.AddMinutes(1);
        return ToUtc(start);
    }
}

public static class OmpTimeServiceExtensions
{
    /// <summary>Validate eagerly during startup; a configured invalid zone never falls back to UTC.</summary>
    public static IServiceCollection AddOmpTime(this IServiceCollection services, IConfiguration configuration)
    {
        var timeZoneId = configuration[OmpTime.ConfigurationKey] ?? "UTC";
        _ = new OmpTime(timeZoneId); // Keep configuration validation eager.
        services.AddSingleton(provider =>
        {
            var environment = provider.GetService<IHostEnvironment>();
            var hostedPresentation = environment is not null && (environment.IsProduction() || environment.IsStaging());
            return new OmpTime(timeZoneId, null, provider.GetService<ILogger<OmpTime>>(),
                rejectLocalPresentation: !hostedPresentation);
        });
        return services;
    }
}
