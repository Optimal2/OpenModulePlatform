// File: OpenModulePlatform.Portal/Pages/Admin/ActivityLog.cshtml.cs
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.ActivityLog;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// The activity viewer: the newest human-attributable events across every module
/// that keeps an ActivityLog table, filtered by OMP user, module, period and text.
/// Entries are read through the shared envelope reader; an entry the reader does
/// not understand (a future version, malformed JSON) still gets a row with its
/// summary when one can be found, and its raw text in the detail.
/// </summary>
public sealed class ActivityLogModel : OmpPortalPageModel
{
    private const int DefaultTake = 200;
    private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly OmpAdminRepository _repo;

    public ActivityLogModel(IOptions<WebAppOptions> options, RbacService rbac, OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty(SupportsGet = true)]
    public int? UserId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string[] Modules { get; set; } = [];

    [BindProperty(SupportsGet = true)]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    /// <summary>The period preset key the range control chose (its dates travel as From/To); display only.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Range { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Take { get; set; } = DefaultTake;

    public IReadOnlyList<ActivityLogModule> AvailableModules { get; private set; } = [];

    public IReadOnlyList<ActivityLogUser> Users { get; private set; } = [];

    public IReadOnlyList<ActivityLogEntryView> Entries { get; private set; } = [];

    /// <summary>
    /// The range control sends a preset key; the page resolves the key to From/To
    /// (UTC days) so the query and the control agree. A known key always wins over
    /// the dates that travel with it: those are yesterday's resolution once the
    /// page is reloaded the next day, and "last 7 days" must keep rolling. The
    /// control writes an empty key for custom dates, and an unknown key is ignored.
    /// </summary>
    private void ApplyRangePreset()
    {
        if (string.IsNullOrWhiteSpace(Range))
        {
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        switch (Range)
        {
            case "all":
                (From, To) = (null, null);
                break;
            case "today":
                (From, To) = (today, today);
                break;
            case "7d":
                (From, To) = (today.AddDays(-6), today);
                break;
            case "30d":
                (From, To) = (today.AddDays(-29), today);
                break;
            case "90d":
                (From, To) = (today.AddDays(-89), today);
                break;
        }
    }

    /// <summary>True when the page was opened with any filter, so an empty result reads as "no match" rather than "nothing logged".</summary>
    public bool HasFilter => UserId.HasValue || Modules.Length > 0 || From.HasValue || To.HasValue || !string.IsNullOrWhiteSpace(Q);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("User log");
        // The repository caps the read; the page shows the same number so the
        // note and the Rows select never claim more than is fetched.
        Take = Take <= 0 ? DefaultTake : Math.Min(Take, OmpAdminRepository.MaxActivityLogTake);
        ApplyRangePreset();

        AvailableModules = await _repo.GetActivityLogModulesAsync(ct);
        Users = await _repo.GetActivityLogUsersAsync(ct);

        if (AvailableModules.Count == 0)
        {
            return Page();
        }

        var filter = new ActivityLogFilter
        {
            UserId = UserId,
            ModuleKeys = Modules.Where(key => !string.IsNullOrWhiteSpace(key)).ToList(),
            FromUtc = From?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            ToUtc = To?.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Text = Q,
            Take = Take
        };

        var rows = await _repo.SearchActivityLogAsync(AvailableModules, filter, ct);
        Entries = rows.Select(ActivityLogEntryView.From).ToList();
        return Page();
    }

    public static string FormatUtc(DateTime value)
        => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Pretty(JsonElement element)
        => JsonSerializer.Serialize(element, PrettyJson);
}

/// <summary>One list row: the stored row plus what the reader made of its envelope.</summary>
public sealed class ActivityLogEntryView
{
    public required ActivityLogRow Row { get; init; }

    /// <summary>The parsed version-1 envelope, or null when the reader could not read it.</summary>
    public ActivityEnvelope? Envelope { get; init; }

    public required string Event { get; init; }

    public required string Summary { get; init; }

    public string Outcome { get; init; } = ActivityOutcomes.Ok;

    public bool IsUnreadable => Envelope is null;

    public static ActivityLogEntryView From(ActivityLogRow row)
    {
        var envelope = ActivityLogJson.TryParse(row.Entry);
        if (envelope is not null)
        {
            return new ActivityLogEntryView
            {
                Row = row,
                Envelope = envelope,
                Event = envelope.Event,
                Summary = envelope.Summary,
                Outcome = string.IsNullOrWhiteSpace(envelope.Outcome) ? ActivityOutcomes.Ok : envelope.Outcome
            };
        }

        return new ActivityLogEntryView
        {
            Row = row,
            Envelope = null,
            Event = string.Empty,
            Summary = ActivityLogJson.TryReadSummary(row.Entry) ?? string.Empty
        };
    }
}
