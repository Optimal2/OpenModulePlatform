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
    private const string IisAppPoolCpuPrefix = "iis.apppool.";
    private const string IisAppPoolMemoryPrefix = "iis.apppool.memory.";
    private const string ServiceCpuPrefix = "service.";
    private const string ServiceMemoryPrefix = "service.memory.";

    private readonly OmpAdminRepository _repo;

    public HostResourcesModel(IOptions<WebAppOptions> options, RbacService rbac, OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    public IReadOnlyList<HostResourceLatestGroupRow> Groups { get; private set; } = [];

    public TimeSpan StaleThreshold { get; private set; } = TimeSpan.FromMinutes(5);

    public int TotalHosts { get; private set; }

    public int HostsWithData { get; private set; }

    public int StaleCount { get; private set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
            return guard;

        SetTitles("Host resources");
        var rows = await _repo.GetHostResourceLatestForAllHostsAsync(ct);
        Groups = BuildGroups(rows);
        TotalHosts = Groups.Select(static g => g.HostId).Distinct().Count();
        HostsWithData = Groups.Count(static g => g.HasData);
        StaleCount = Groups.Count(g => g.HasData && IsStale(g.LastSampledUtc));
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

            var (runtimeKind, runtimeName, isMemory) = ParseSampleKey(row.SampleKey);
            if (string.IsNullOrWhiteSpace(runtimeName))
            {
                continue;
            }

            var key = (row.HostId, runtimeKind, runtimeName);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new HostResourceLatestGroupRow
                {
                    HostId = row.HostId,
                    HostKey = row.HostKey,
                    HostDisplayName = row.HostDisplayName,
                    HostLastSeenUtc = row.HostLastSeenUtc,
                    RuntimeKind = runtimeKind,
                    RuntimeName = runtimeName
                };
                groups[key] = group;
            }

            if (isMemory)
            {
                group.MemorySampleKey = row.SampleKey;
                group.MemoryValue = row.SampleValue;
            }
            else
            {
                group.CpuSampleKey = row.SampleKey;
                group.CpuValue = row.SampleValue;
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

    private static (string RuntimeKind, string RuntimeName, bool IsMemory) ParseSampleKey(string sampleKey)
    {
        if (sampleKey.StartsWith(IisAppPoolMemoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ("IIS app pool", sampleKey[IisAppPoolMemoryPrefix.Length..], true);
        }

        if (sampleKey.StartsWith(IisAppPoolCpuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ("IIS app pool", sampleKey[IisAppPoolCpuPrefix.Length..], false);
        }

        if (sampleKey.StartsWith(ServiceMemoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ("Windows service", sampleKey[ServiceMemoryPrefix.Length..], true);
        }

        if (sampleKey.StartsWith(ServiceCpuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ("Windows service", sampleKey[ServiceCpuPrefix.Length..], false);
        }

        return (string.Empty, string.Empty, false);
    }

    public bool IsStale(DateTime? lastSampledUtc)
        => lastSampledUtc.HasValue
           && DateTime.UtcNow - DateTime.SpecifyKind(lastSampledUtc.Value, DateTimeKind.Utc) > StaleThreshold;

    public string FormatCpu(double? value)
        => value.HasValue ? string.Create(CultureInfo.InvariantCulture, $"{value.Value:F1}%") : string.Empty;

    public string FormatMemory(double? value)
        => value.HasValue ? string.Create(CultureInfo.InvariantCulture, $"{value.Value:F1} MB") : string.Empty;

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
}
