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

    public SystemLogModel(IOptions<WebAppOptions> options, RbacService rbac, OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty(SupportsGet = true)]
    public string[] Processes { get; set; } = [];

    [BindProperty(SupportsGet = true)]
    public string[] Levels { get; set; } = [];

    /// <summary>Start of the period, UTC, to the minute (yyyy-MM-ddTHH:mm); the picker sets whole days unless a time is typed.</summary>
    [BindProperty(SupportsGet = true)]
    public DateTime? From { get; set; }

    /// <summary>End of the period, UTC, inclusive to the minute.</summary>
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
        // The bounds are UTC wall-clock minutes. A value with an offset (a
        // hand-written "Z" in the address) is converted rather than relabelled,
        // and a bound written as a bare day (a bookmark from the days-only
        // page) spans that whole day.
        From = AsUtc(From);
        To = AsUtc(To);
        if (To is { } toDay && !Request.Query["To"].ToString().Contains('T'))
        {
            To = toDay.Date.AddHours(23).AddMinutes(59);
        }

        // A preset key resolves to whole days; typed times only travel with
        // a custom period, and an unknown key leaves them alone.
        if (PeriodPresets.IsPreset(Range))
        {
            var (presetFrom, presetTo) = PeriodPresets.Apply(Range, null, null);
            From = presetFrom?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            To = presetTo?.ToDateTime(new TimeOnly(23, 59), DateTimeKind.Utc);
        }

        AvailableProcesses = await _repo.GetSystemLogProcessesAsync(ct);

        var filter = new SystemLogFilter
        {
            Processes = Processes.Where(p => !string.IsNullOrWhiteSpace(p)).ToList(),
            Levels = Levels.Where(l => !string.IsNullOrWhiteSpace(l)).ToList(),
            FromUtc = From is { } fromValue ? DateTime.SpecifyKind(fromValue, DateTimeKind.Utc) : null,
            // Inclusive to the minute: the bound is the start of the next one.
            ToUtc = To is { } toValue ? DateTime.SpecifyKind(toValue, DateTimeKind.Utc).AddMinutes(1) : null,
            Text = Q,
            Take = Take
        };

        Entries = await _repo.SearchSystemLogAsync(filter, ct);
        return Page();
    }

    public static string FormatUtc(DateTime value)
        => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static DateTime? AsUtc(DateTime? value)
        => value is { } v ? (v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;

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
