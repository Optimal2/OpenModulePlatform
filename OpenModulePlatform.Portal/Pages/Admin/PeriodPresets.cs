// File: OpenModulePlatform.Portal/Pages/Admin/PeriodPresets.cs
namespace OpenModulePlatform.Portal.Pages.Admin;

/// <summary>
/// The period presets the shared range picker offers on the log pages,
/// resolved on the server so the query and the control agree. A known key
/// always wins over the dates that travel with it: those are yesterday's
/// resolution once the page is reloaded the next day, and "last 7 days" must
/// keep rolling. The control writes "custom" for typed or picked dates, and
/// an unknown key leaves the dates as they are.
/// </summary>
internal static class PeriodPresets
{
    /// <summary>True for a key the pickers offer and this resolver knows.</summary>
    public static bool IsPreset(string? key)
        => key is "all" or "today" or "7d" or "30d" or "90d";

    public static (DateOnly? From, DateOnly? To) Apply(string? key, DateOnly? from, DateOnly? to)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return (from, to);
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        return key switch
        {
            "all" => (null, null),
            "today" => (today, today),
            "7d" => (today.AddDays(-6), today),
            "30d" => (today.AddDays(-29), today),
            "90d" => (today.AddDays(-89), today),
            _ => (from, to)
        };
    }
}
