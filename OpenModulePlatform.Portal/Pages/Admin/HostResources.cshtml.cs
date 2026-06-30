// File: OpenModulePlatform.Portal/Pages/Admin/HostResources.cshtml.cs
using System.Globalization;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Portal.Pages.Admin;

public sealed class HostResourcesModel : OmpPortalPageModel
{
    private static readonly string[] ValidSortColumns =
    [
        SortColumn.RuntimeKind,
        SortColumn.RuntimeName,
        SortColumn.Cpu,
        SortColumn.Memory,
        SortColumn.Samples,
        SortColumn.LastSampledUtc,
        SortColumn.State
    ];

    private readonly OmpAdminRepository _repo;

    public HostResourcesModel(IOptions<WebAppOptions> options, RbacService rbac, OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public IReadOnlyList<HostResourceLatestGroupRow> Groups { get; private set; } = [];

    public IReadOnlyList<HostResourceHostSummary> HostSummaries { get; private set; } = [];

    public TimeSpan StaleThreshold { get; private set; } = TimeSpan.FromMinutes(5);

    public int TotalHosts { get; private set; }

    public int HostsWithData { get; private set; }

    public int StaleCount { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string Sort { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string Dir { get; set; } = string.Empty;

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("Host resources");
        var rows = await _repo.GetHostResourceLatestForAllHostsAsync(ct);
        Groups = SortGroups(BuildGroups(rows), Sort, Dir);
        HostSummaries = BuildHostSummaries(Groups);
        TotalHosts = HostSummaries.Count;
        HostsWithData = HostSummaries.Count(static s => s.HasData);
        StaleCount = HostSummaries.Count(s => s.HasData && IsStale(s.LastSampledUtc));
        return Page();
    }

    private IReadOnlyList<HostResourceLatestGroupRow> BuildGroups(IReadOnlyList<HostResourceLatestRow> rows)
    {
        var hostsById = rows
            .GroupBy(row => row.HostId)
            .ToDictionary(
                group => group.Key,
                group => group.First());

        var groups = new Dictionary<(Guid HostId, string RuntimeKind, string RuntimeName), HostResourceLatestGroupRow>();

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.SampleKey))
            {
                // Host exists but has no telemetry rows yet.
                if (!groups.ContainsKey((row.HostId, string.Empty, string.Empty)))
                {
                    groups[(row.HostId, string.Empty, string.Empty)] = new HostResourceLatestGroupRow
                    {
                        HostId = row.HostId,
                        HostKey = row.HostKey,
                        HostDisplayName = row.HostDisplayName,
                        HostLastSeenUtc = row.HostLastSeenUtc
                    };
                }

                continue;
            }

            var sampleKey = HostResourceSampleKeyParser.Parse(row.SampleKey);
            if (string.IsNullOrWhiteSpace(sampleKey.RuntimeName))
            {
                continue;
            }

            var runtimeKind = sampleKey.MetricKind == HostResourceMetricKind.State
                ? "Windows service"
                : sampleKey.RuntimeKind;
            var key = (row.HostId, runtimeKind, sampleKey.RuntimeName);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new HostResourceLatestGroupRow
                {
                    HostId = row.HostId,
                    HostKey = row.HostKey,
                    HostDisplayName = row.HostDisplayName,
                    HostLastSeenUtc = row.HostLastSeenUtc,
                    RuntimeKind = runtimeKind,
                    RuntimeName = sampleKey.RuntimeName
                };
                groups[key] = group;
            }

            if (sampleKey.MetricKind == HostResourceMetricKind.Memory)
            {
                group.MemorySampleKey = row.SampleKey;
                group.MemoryValue = row.SampleValue;
            }
            else if (sampleKey.MetricKind == HostResourceMetricKind.Cpu)
            {
                group.CpuSampleKey = row.SampleKey;
                group.CpuValue = row.SampleValue;
            }
            else if (sampleKey.MetricKind == HostResourceMetricKind.State)
            {
                group.StateSampleKey = row.SampleKey;
                group.StateValue = row.SampleValue;
            }

            group.SampleCount = Math.Max(group.SampleCount, row.SampleCount);
            if (!group.LastSampledUtc.HasValue || row.LastSampledUtc > group.LastSampledUtc.Value)
            {
                group.LastSampledUtc = row.LastSampledUtc;
            }
        }

        // Ensure every enabled host appears at least once, even if it has no samples.
        foreach (var host in hostsById.Values)
        {
            var emptyKey = (host.HostId, string.Empty, string.Empty);
            if (!groups.ContainsKey(emptyKey))
            {
                groups[emptyKey] = new HostResourceLatestGroupRow
                {
                    HostId = host.HostId,
                    HostKey = host.HostKey,
                    HostDisplayName = host.HostDisplayName,
                    HostLastSeenUtc = host.HostLastSeenUtc
                };
            }
        }

        return groups.Values
            .OrderBy(g => g.HostKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.RuntimeKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.RuntimeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<HostResourceHostSummary> BuildHostSummaries(IReadOnlyList<HostResourceLatestGroupRow> groups)
    {
        return groups
            .GroupBy(g => g.HostId)
            .Select(hostGroups =>
            {
                var representative = hostGroups.First();
                var components = hostGroups.Where(static g => g.HasData).ToList();
                DateTime? lastSampledUtc = null;
                if (components.Count > 0)
                {
                    lastSampledUtc = components
                        .Where(static g => g.LastSampledUtc.HasValue)
                        .Max(static g => g.LastSampledUtc!.Value);
                }

                return new HostResourceHostSummary
                {
                    HostId = hostGroups.Key,
                    HostKey = representative.HostKey,
                    HostDisplayName = representative.HostDisplayName,
                    HostLastSeenUtc = representative.HostLastSeenUtc,
                    TotalCpu = components.Sum(static g => g.CpuValue ?? 0),
                    TotalMemory = components.Sum(static g => g.MemoryValue ?? 0),
                    ComponentCount = components.Count,
                    LastSampledUtc = lastSampledUtc,
                    HasData = components.Count > 0
                };
            })
            .OrderBy(s => s.HostKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<HostResourceLatestGroupRow> SortGroups(
        IReadOnlyList<HostResourceLatestGroupRow> groups,
        string sort,
        string dir)
    {
        var sortColumn = sort.Trim();
        if (!ValidSortColumns.Contains(sortColumn, StringComparer.OrdinalIgnoreCase))
        {
            sortColumn = SortColumn.RuntimeName;
        }

        var descending = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);

        return groups
            .GroupBy(g => g.HostId)
            .SelectMany(hostGroups => OrderHostGroups(hostGroups, sortColumn, descending))
            .ToList();
    }

    private IOrderedEnumerable<HostResourceLatestGroupRow> OrderHostGroups(
        IGrouping<Guid, HostResourceLatestGroupRow> hostGroups,
        string sortColumn,
        bool descending)
    {
        IOrderedEnumerable<HostResourceLatestGroupRow> ordered = sortColumn.ToLowerInvariant() switch
        {
            SortColumn.RuntimeKind => descending
                ? hostGroups.OrderByDescending(g => g.RuntimeKind, StringComparer.OrdinalIgnoreCase)
                : hostGroups.OrderBy(g => g.RuntimeKind, StringComparer.OrdinalIgnoreCase),
            SortColumn.RuntimeName => descending
                ? hostGroups.OrderByDescending(g => g.RuntimeName, StringComparer.OrdinalIgnoreCase)
                : hostGroups.OrderBy(g => g.RuntimeName, StringComparer.OrdinalIgnoreCase),
            SortColumn.Cpu => OrderByNullableNumber(hostGroups, static g => g.CpuValue, descending),
            SortColumn.Memory => OrderByNullableNumber(hostGroups, static g => g.MemoryValue, descending),
            SortColumn.Samples => descending
                ? hostGroups.OrderByDescending(g => g.HasData ? g.SampleCount : int.MinValue)
                : hostGroups.OrderBy(g => g.HasData ? g.SampleCount : int.MaxValue),
            SortColumn.LastSampledUtc => descending
                ? hostGroups.OrderByDescending(g => g.LastSampledUtc ?? DateTime.MinValue)
                : hostGroups.OrderBy(g => g.LastSampledUtc ?? DateTime.MaxValue),
            SortColumn.State => descending
                ? hostGroups.OrderByDescending(g => StateSortOrder(g))
                : hostGroups.OrderBy(g => StateSortOrder(g)),
            _ => hostGroups.OrderBy(g => g.RuntimeKind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => g.RuntimeName, StringComparer.OrdinalIgnoreCase)
        };

        // Stable tie-breaker so rows with equal primary sort keys do not jump.
        if (!string.Equals(sortColumn, SortColumn.RuntimeName, StringComparison.OrdinalIgnoreCase))
        {
            ordered = descending
                ? ordered.ThenByDescending(g => g.RuntimeName, StringComparer.OrdinalIgnoreCase)
                : ordered.ThenBy(g => g.RuntimeName, StringComparer.OrdinalIgnoreCase);
        }

        if (!string.Equals(sortColumn, SortColumn.RuntimeKind, StringComparison.OrdinalIgnoreCase))
        {
            ordered = descending
                ? ordered.ThenByDescending(g => g.RuntimeKind, StringComparer.OrdinalIgnoreCase)
                : ordered.ThenBy(g => g.RuntimeKind, StringComparer.OrdinalIgnoreCase);
        }

        return ordered;
    }

    private int StateSortOrder(HostResourceLatestGroupRow group)
    {
        if (!group.HasData)
            return 0;

        if (IsStale(group.LastSampledUtc))
            return 1;

        return 2;
    }

    public string SortLink(string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);

        var nextDir = string.Equals(Sort, column, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Dir, "asc", StringComparison.OrdinalIgnoreCase)
            ? "desc"
            : "asc";

        return $"~/admin/hostresources?sort={Uri.EscapeDataString(column)}&dir={nextDir}";
    }

    public string SortClass(string column)
    {
        return string.Equals(Sort, column, StringComparison.OrdinalIgnoreCase)
            ? (string.Equals(Dir, "desc", StringComparison.OrdinalIgnoreCase) ? "sortable sort-desc" : "sortable sort-asc")
            : "sortable";
    }

    public string SortIndicator(string column)
    {
        if (!string.Equals(Sort, column, StringComparison.OrdinalIgnoreCase))
            return "\u2195";

        return string.Equals(Dir, "desc", StringComparison.OrdinalIgnoreCase) ? "\u2193" : "\u2191";
    }

    public static class SortColumn
    {
        public const string RuntimeKind = "runtimeKind";
        public const string RuntimeName = "runtimeName";
        public const string Cpu = "cpu";
        public const string Memory = "memory";
        public const string Samples = "samples";
        public const string LastSampledUtc = "lastSampledUtc";
        public const string State = "state";
    }

    private static IOrderedEnumerable<HostResourceLatestGroupRow> OrderByNullableNumber(
        IEnumerable<HostResourceLatestGroupRow> groups,
        Func<HostResourceLatestGroupRow, double?> valueSelector,
        bool descending)
        => descending
            ? groups
                .OrderBy(g => valueSelector(g).HasValue ? 0 : 1)
                .ThenByDescending(g => valueSelector(g) ?? 0)
            : groups
                .OrderBy(g => valueSelector(g).HasValue ? 0 : 1)
                .ThenBy(g => valueSelector(g) ?? 0);

    public bool IsStale(DateTime? lastSampledUtc)
        => lastSampledUtc.HasValue
           && DateTime.UtcNow - DateTime.SpecifyKind(lastSampledUtc.Value, DateTimeKind.Utc) > StaleThreshold;

    public string FormatCpu(double? value)
        => value.HasValue ? string.Create(CultureInfo.InvariantCulture, $"{value.Value:F1}%") : string.Empty;

    public string FormatMemory(double? value)
    {
        if (!value.HasValue)
            return string.Empty;

        var mb = value.Value;
        if (mb >= 1024)
            return string.Create(CultureInfo.InvariantCulture, $"{mb / 1024:F2} GB");

        return string.Create(CultureInfo.InvariantCulture, $"{mb:F1} MB");
    }

    public static string StateClass(HostResourceLatestGroupRow group, HostResourcesModel model)
    {
        if (!group.HasData)
        {
            return "state-none";
        }

        if (model.IsStale(group.LastSampledUtc))
        {
            return "state-stale";
        }

        return "state-current";
    }

    public static string StateClass(HostResourceHostSummary host, HostResourcesModel model)
    {
        if (!host.HasData)
        {
            return "state-none";
        }

        if (model.IsStale(host.LastSampledUtc))
        {
            return "state-stale";
        }

        return "state-current";
    }
}
