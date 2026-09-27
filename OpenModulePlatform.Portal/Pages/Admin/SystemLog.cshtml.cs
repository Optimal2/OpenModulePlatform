// File: OpenModulePlatform.Portal/Pages/Admin/SystemLog.cshtml.cs
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
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

    private readonly OmpAdminRepository _repo;
    private readonly OmpTime _time;

    public SystemLogModel(IOptions<WebAppOptions> options, RbacService rbac, OmpAdminRepository repo, OmpTime time)
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
    [BindProperty(SupportsGet = true)]
    public DateTime? From { get; set; }

    /// <summary>End in the configured calendar zone, inclusive to the minute.</summary>
    [BindProperty(SupportsGet = true)]
    public DateTime? To { get; set; }

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
        // Bare values are calendar inputs; explicit instants are converted for the picker.
        From = AsCalendarInput(From);
        To = AsCalendarInput(To);
        if (To is { } toDay && !Request.Query["To"].ToString().Contains('T'))
        {
            To = toDay.Date.AddHours(23).AddMinutes(59);
        }

        // A preset key resolves to whole days; typed times only travel with
        // a custom period, and an unknown key leaves them alone.
        if (PeriodPresets.IsPreset(Range))
        {
            var (presetFrom, presetTo) = PeriodPresets.Apply(Range, null, null, _time.Today);
            From = presetFrom?.ToDateTime(TimeOnly.MinValue);
            To = presetTo?.ToDateTime(new TimeOnly(23, 59));
        }

        AvailableProcesses = await _repo.GetSystemLogProcessesAsync(ct);

        DateTime? fromUtc;
        DateTime? toUtc;
        try
        {
            var wholeDays = PeriodPresets.IsPreset(Range);
            fromUtc = From is { } from
                ? wholeDays || !Request.Query["From"].ToString().Contains('T')
                    ? _time.StartOfDayUtc(DateOnly.FromDateTime(from))
                    : _time.ToUtc(from)
                : null;
            toUtc = To is { } to
                ? wholeDays || !Request.Query["To"].ToString().Contains('T')
                    ? _time.StartOfDayUtc(DateOnly.FromDateTime(to).AddDays(1))
                    : _time.ToUtc(to, upperBound: true).AddMinutes(1)
                : null;
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
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

    private DateTime? AsCalendarInput(DateTime? value)
        => value is { Kind: not DateTimeKind.Unspecified } instant
            ? _time.ToDisplayTime(instant.ToUniversalTime()).DateTime
            : value;

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
