// File: OpenModulePlatform.Portal/Pages/Admin/SystemLog.cshtml.cs
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// The system log viewer: what the platform's own processes reported at Warn
/// and above, from every host, read from omp.SystemLog (NLog's database target
/// writes it, the HostAgent prunes it). For the people who run the system; the
/// user log is the audit trail of what users did. Filtered by process, level,
/// period and text, the same bar and list as the user log.
/// </summary>
public sealed class SystemLogModel : OmpPortalPageModel
{
    private const int DefaultTake = 200;

    private readonly ISystemLogReader _repo;
    private readonly OmpTime _time;

    public SystemLogModel(IOptions<WebAppOptions> options, RbacService rbac, ISystemLogReader repo, OmpTime time)
        : base(options, rbac)
    {
        _repo = repo;
        _time = time;
    }

    [BindProperty(SupportsGet = true)]
    public string[] Processes { get; set; } = [];

    [BindProperty(SupportsGet = true)]
    public string[] Levels { get; set; } = [];

    /// <summary>Start in the configured calendar zone, to the minute; the picker sets whole days unless a time is typed.</summary>
    [BindNever]
    public DateTime? From { get; set; }

    /// <summary>End in the configured calendar zone, inclusive to the minute.</summary>
    [BindNever]
    public DateTime? To { get; set; }

    // Preserve the wire representation: DateTime binding can discard the offset.
    [BindProperty(Name = "From", SupportsGet = true)]
    public string? FromInput { get; set; }

    [BindProperty(Name = "To", SupportsGet = true)]
    public string? ToInput { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    /// <summary>The period preset key the range control chose (its dates travel as From/To); display only.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Range { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Take { get; set; } = DefaultTake;

    public IReadOnlyList<string> AvailableProcesses { get; private set; } = [];

    public IReadOnlyList<string> AvailableLevels => OmpAdminRepository.SystemLogLevels;

    public IReadOnlyList<SystemLogRow> Entries { get; private set; } = [];

    /// <summary>True when the page was opened with any filter, so an empty result reads as "no match" rather than "nothing logged".</summary>
    public bool HasFilter => Processes.Length > 0 || Levels.Length > 0 || From.HasValue || To.HasValue || !string.IsNullOrWhiteSpace(Q);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var guard = await RequireSystemLogReaderAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("System log");
        Take = Take <= 0 ? DefaultTake : Math.Min(Take, OmpAdminRepository.MaxSystemLogTake);
        AvailableProcesses = await _repo.GetSystemLogProcessesAsync(ct);
        var fromUtc = ResolveBound(FromInput, nameof(From), upperBound: false, out var fromCalendar);
        var toUtc = ResolveBound(ToInput, nameof(To), upperBound: true, out var toCalendar);
        From = fromCalendar;
        To = toCalendar;
        if (!ModelState.IsValid) return Page();

        // A preset key resolves to whole days; typed times only travel with
        // a custom period, and an unknown key leaves them alone.
        if (PeriodPresets.IsPreset(Range))
        {
            var (presetFrom, presetTo) = PeriodPresets.Apply(Range, null, null, _time.Today);
            From = presetFrom?.ToDateTime(TimeOnly.MinValue);
            To = presetTo?.ToDateTime(new TimeOnly(23, 59));
            fromUtc = presetFrom is { } start ? _time.StartOfDayUtc(start) : null;
            toUtc = presetTo is { } end ? _time.StartOfDayUtc(end.AddDays(1)) : null;
        }

        var filter = new SystemLogFilter
        {
            Processes = Processes.Where(p => !string.IsNullOrWhiteSpace(p)).ToList(),
            Levels = Levels.Where(l => !string.IsNullOrWhiteSpace(l)).ToList(),
            FromUtc = fromUtc,
            // Inclusive to the minute: the bound is the start of the next one.
            ToUtc = toUtc,
            Text = Q,
            Take = Take
        };

        Entries = await _repo.SearchSystemLogAsync(filter, ct);
        return Page();
    }

    private DateTime? ResolveBound(string? input, string field, bool upperBound, out DateTime? calendar)
    {
        calendar = null;
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();
        try
        {
            // Every instant format requires an explicit zone; AssumeUniversal only
            // supplies the offset for the literal Z. No machine-local default is used.
            string[] instantFormats = ["yyyy-MM-dd'T'HH:mmzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
                "yyyy-MM-dd'T'HH:mm'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];
            if (DateTimeOffset.TryParseExact(input, instantFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var instant))
            {
                calendar = _time.ToDisplayTime(instant).DateTime;
                // Keep the original occurrence during a repeated clock hour.
                return upperBound ? instant.UtcDateTime.AddMinutes(1) : instant.UtcDateTime;
            }

            string[] calendarFormats = ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];
            if (DateTime.TryParseExact(input, calendarFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var wallTime))
            {
                calendar = wallTime;
                if (input.Length == 10)
                {
                    var date = DateOnly.FromDateTime(wallTime);
                    if (upperBound) calendar = wallTime.AddHours(23).AddMinutes(59);
                    return _time.StartOfDayUtc(upperBound ? date.AddDays(1) : date);
                }
                var utc = _time.ToUtc(wallTime, upperBound);
                return upperBound ? utc.AddMinutes(1) : utc;
            }
        }
        catch (ArgumentException)
        {
            // Missing calendar minutes and out-of-range boundaries are input errors.
        }

        ModelState.AddModelError(field, PortalLocalizer["Enter a valid date and time."]);
        return null;
    }

    /// <summary>The value the picker's hidden inputs carry for a bound.</summary>
    public static string FieldValue(DateTime? value)
        => value?.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>The pill class for a level: Error and Fatal stand out, a warning is marked more lightly.</summary>
    public static string LevelPillClass(string level)
        => level.Equals("Warn", StringComparison.OrdinalIgnoreCase) ? "pill pill-warning" : "pill pill-danger";

    /// <summary>The level as the reader's word for it; the stored NLog name is shown in the sort key and the row search.</summary>
    public static string LevelTextKey(string level)
        => level.ToUpperInvariant() switch
        {
            "WARN" => "Warning",
            "ERROR" => "Error",
            "FATAL" => "Fatal",
            _ => level
        };
}
