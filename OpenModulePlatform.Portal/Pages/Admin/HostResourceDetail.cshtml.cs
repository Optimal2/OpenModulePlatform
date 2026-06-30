// File: OpenModulePlatform.Portal/Pages/Admin/HostResourceDetail.cshtml.cs
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Pages.Admin;

public sealed class HostResourceDetailModel : OmpPortalPageModel
{
    private const string IisAppPoolCpuPrefix = "iis.apppool.";
    private const string IisAppPoolMemoryPrefix = "iis.apppool.memory.";
    private const string ServiceCpuPrefix = "service.";
    private const string ServiceMemoryPrefix = "service.memory.";
    private const string ServiceStatePrefix = "service.state.";

    private const int MinHours = 1;
    private const int MaxHours = 168 * 4; // ~4 weeks
    private const int DefaultHours = 24;

    private readonly OmpAdminRepository _repo;

    public HostResourceDetailModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        OmpAdminRepository repo)
        : base(options, rbac)
    {
        _repo = repo;
    }

    [BindProperty(SupportsGet = true)]
    public Guid HostId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string SampleKey { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public int Hours { get; set; } = DefaultHours;

    public string HostKey { get; private set; } = string.Empty;

    public string? HostDisplayName { get; private set; }

    public string RuntimeKind { get; private set; } = string.Empty;

    public string RuntimeName { get; private set; } = string.Empty;

    public bool IsMemory { get; private set; }

    public string? CounterpartSampleKey { get; private set; }

    public IReadOnlyList<HostResourceHistoryRow> PrimaryRows { get; private set; } = [];

    public IReadOnlyList<HostResourceHistoryRow> CounterpartRows { get; private set; } = [];

    public ChartViewModel PrimaryChart { get; private set; } = new();

    public ChartViewModel? CounterpartChart { get; private set; }

    public IReadOnlyList<int> AvailableHours { get; } = [1, 6, 24, 168];

    public int EffectiveHours { get; private set; }

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequirePortalAdminAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        if (HostId == Guid.Empty || string.IsNullOrWhiteSpace(SampleKey))
        {
            return BadRequest("hostId and sampleKey are required.");
        }

        EffectiveHours = Math.Clamp(Hours, MinHours, MaxHours);

        var (runtimeKind, runtimeName, isMemory) = ParseSampleKey(SampleKey);
        RuntimeKind = runtimeKind;
        RuntimeName = runtimeName;
        IsMemory = isMemory;
        CounterpartSampleKey = DeriveCounterpartSampleKey(SampleKey, isMemory);

        var host = await _repo.GetHostAsync(HostId, ct);
        if (host is null)
        {
            return NotFound();
        }

        HostKey = host.HostKey;
        HostDisplayName = host.DisplayName;

        var sinceUtc = DateTime.UtcNow.AddHours(-EffectiveHours);
        PrimaryRows = await _repo.GetHostResourceHistoryAsync(HostId, SampleKey, sinceUtc, ct);

        if (!string.IsNullOrWhiteSpace(CounterpartSampleKey))
        {
            CounterpartRows = await _repo.GetHostResourceHistoryAsync(HostId, CounterpartSampleKey, sinceUtc, ct);
        }

        PrimaryChart = BuildChart(PrimaryRows, isMemory, T("CPU"), T("Memory"));
        if (CounterpartRows.Count > 0)
        {
            CounterpartChart = BuildChart(CounterpartRows, !isMemory, T("CPU"), T("Memory"));
        }

        SetTitles("Resource detail");
        return Page();
    }

    private static ChartViewModel BuildChart(
        IReadOnlyList<HostResourceHistoryRow> rows,
        bool isMemory,
        string cpuLabel,
        string memoryLabel)
    {
        var chronological = rows.OrderBy(static r => r.SampleBucketUtc).ToList();
        var stats = ComputeStats(chronological);
        var svg = BuildSvgChart(chronological, isMemory);

        return new ChartViewModel
        {
            Title = isMemory ? memoryLabel : cpuLabel,
            Unit = isMemory ? "MB" : "%",
            Stats = stats,
            Svg = svg,
            HasData = chronological.Count > 0
        };
    }

    private static ChartStats ComputeStats(IReadOnlyList<HostResourceHistoryRow> rows)
    {
        if (rows.Count == 0)
        {
            return new ChartStats();
        }

        var values = rows.Select(static r => r.SampleValue).ToList();
        var min = values.Min();
        var max = values.Max();
        var avg = values.Average();
        var latest = rows[^1].SampleValue;
        var earliest = rows[0].SampleBucketUtc;
        var newest = rows[^1].SampleBucketUtc;

        return new ChartStats
        {
            Average = avg,
            Minimum = min,
            Maximum = max,
            Latest = latest,
            SampleCount = rows.Count,
            EarliestUtc = earliest,
            NewestUtc = newest
        };
    }

    private static string BuildSvgChart(IReadOnlyList<HostResourceHistoryRow> rows, bool isMemory)
    {
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        const int width = 760;
        const int height = 240;
        const int paddingLeft = 52;
        const int paddingRight = 16;
        const int paddingTop = 12;
        const int paddingBottom = 36;
        const int plotWidth = width - paddingLeft - paddingRight;
        const int plotHeight = height - paddingTop - paddingBottom;

        var values = rows.Select(static r => r.SampleValue).ToList();
        var minValue = values.Min();
        var maxValue = values.Max();

        var (yMin, yMax) = ComputeYRange(minValue, maxValue, isMemory);
        var yRange = yMax - yMin;
        if (yRange <= 0)
        {
            yRange = 1;
        }

        var startTime = rows[0].SampleBucketUtc;
        var endTime = rows[^1].SampleBucketUtc;
        var timeRange = endTime - startTime;
        if (timeRange.TotalSeconds <= 0)
        {
            timeRange = TimeSpan.FromSeconds(1);
        }

        double MapX(DateTime t)
        {
            var fraction = (t - startTime).TotalSeconds / timeRange.TotalSeconds;
            return paddingLeft + fraction * plotWidth;
        }

        double MapY(double v)
        {
            var fraction = (v - yMin) / yRange;
            return paddingTop + plotHeight - fraction * plotHeight;
        }

        var pathBuilder = new StringBuilder();
        var areaBuilder = new StringBuilder();
        var pointBuilder = new StringBuilder();
        var gridBuilder = new StringBuilder();

        // Horizontal grid lines and Y-axis labels.
        const int gridLines = 4;
        for (var i = 0; i <= gridLines; i++)
        {
            var fraction = i / (double)gridLines;
            var yValue = yMin + fraction * yRange;
            var y = MapY(yValue);
            var label = isMemory
                ? yValue.ToString("F0", CultureInfo.InvariantCulture)
                : yValue.ToString("F1", CultureInfo.InvariantCulture);

            gridBuilder.Append(CultureInfo.InvariantCulture, $"<line x1=\"{paddingLeft}\" y1=\"{y:F1}\" x2=\"{width - paddingRight}\" y2=\"{y:F1}\" class=\"host-resource-chart__grid\" />");
            gridBuilder.Append(CultureInfo.InvariantCulture, $"<text x=\"{paddingLeft - 6}\" y=\"{y + 4:F1}\" class=\"host-resource-chart__axis-label host-resource-chart__axis-label--y\">{label}</text>");
        }

        // X-axis time labels.
        gridBuilder.Append(CultureInfo.InvariantCulture, $"<text x=\"{paddingLeft}\" y=\"{height - 10}\" class=\"host-resource-chart__axis-label host-resource-chart__axis-label--x\">{startTime:HH:mm}</text>");
        gridBuilder.Append(CultureInfo.InvariantCulture, $"<text x=\"{width - paddingRight}\" y=\"{height - 10}\" class=\"host-resource-chart__axis-label host-resource-chart__axis-label--x host-resource-chart__axis-label--end\">{endTime:HH:mm}</text>");

        if (rows.Count == 1)
        {
            var x = MapX(rows[0].SampleBucketUtc);
            var y = MapY(rows[0].SampleValue);
            pointBuilder.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{x:F1}\" cy=\"{y:F1}\" r=\"4\" class=\"host-resource-chart__point\" />");
        }
        else
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var x = MapX(row.SampleBucketUtc);
                var y = MapY(row.SampleValue);

                if (i == 0)
                {
                    pathBuilder.Append(CultureInfo.InvariantCulture, $"M {x:F1} {y:F1}");
                    areaBuilder.Append(CultureInfo.InvariantCulture, $"M {x:F1} {paddingTop + plotHeight} L {x:F1} {y:F1}");
                }
                else
                {
                    pathBuilder.Append(CultureInfo.InvariantCulture, $" L {x:F1} {y:F1}");
                    areaBuilder.Append(CultureInfo.InvariantCulture, $" L {x:F1} {y:F1}");
                }

                pointBuilder.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{x:F1}\" cy=\"{y:F1}\" r=\"2.5\" class=\"host-resource-chart__point\" />");
            }

            if (rows.Count > 1)
            {
                var lastX = MapX(rows[^1].SampleBucketUtc);
                areaBuilder.Append(CultureInfo.InvariantCulture, $" L {lastX:F1} {paddingTop + plotHeight} Z");
            }
        }

        var svg = new StringBuilder();
        svg.Append("<svg class=\"host-resource-chart\" viewBox=\"0 0 ");
        svg.Append(width);
        svg.Append(' ');
        svg.Append(height);
        svg.Append("\" role=\"img\" aria-label=\"");
        svg.Append(isMemory ? "Memory history" : "CPU history");
        svg.Append("\">");
        svg.Append(gridBuilder);

        if (areaBuilder.Length > 0)
        {
            svg.Append("<path d=\"");
            svg.Append(areaBuilder);
            svg.Append("\" class=\"host-resource-chart__area\" />");
        }

        if (pathBuilder.Length > 0)
        {
            svg.Append("<path d=\"");
            svg.Append(pathBuilder);
            svg.Append("\" class=\"host-resource-chart__line\" />");
        }

        svg.Append(pointBuilder);
        svg.Append("</svg>");

        return svg.ToString();
    }

    private static (double Min, double Max) ComputeYRange(double minValue, double maxValue, bool isMemory)
    {
        if (isMemory)
        {
            var floor = Math.Floor(minValue / 10.0) * 10.0;
            var ceil = Math.Ceiling(maxValue / 10.0) * 10.0;
            if (ceil <= floor)
            {
                ceil = floor + 10.0;
            }

            return (floor, ceil);
        }

        // CPU: keep a minimum ceiling of 5% and round up to the nearest 5%.
        var cpuMin = Math.Min(minValue, 0.0);
        var cpuMax = Math.Max(maxValue, 5.0);
        var cpuCeil = Math.Ceiling(cpuMax / 5.0) * 5.0;
        return (cpuMin, cpuCeil);
    }

    public string FormatValue(double value)
        => IsMemory
            ? string.Create(CultureInfo.InvariantCulture, $"{value:F0} MB")
            : string.Create(CultureInfo.InvariantCulture, $"{value:F1}%");

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

        if (sampleKey.StartsWith(ServiceStatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ("Windows service state", sampleKey[ServiceStatePrefix.Length..], false);
        }

        if (sampleKey.StartsWith(ServiceCpuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ("Windows service", sampleKey[ServiceCpuPrefix.Length..], false);
        }

        return (string.Empty, string.Empty, false);
    }

    private static string? DeriveCounterpartSampleKey(string sampleKey, bool isMemory)
    {
        if (sampleKey.StartsWith(IisAppPoolCpuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = sampleKey[IisAppPoolCpuPrefix.Length..];
            return isMemory ? sampleKey : $"{IisAppPoolMemoryPrefix}{name}";
        }

        if (sampleKey.StartsWith(IisAppPoolMemoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = sampleKey[IisAppPoolMemoryPrefix.Length..];
            return isMemory ? $"{IisAppPoolCpuPrefix}{name}" : sampleKey;
        }

        if (sampleKey.StartsWith(ServiceMemoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = sampleKey[ServiceMemoryPrefix.Length..];
            return isMemory ? $"{ServiceCpuPrefix}{name}" : sampleKey;
        }

        if (sampleKey.StartsWith(ServiceStatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (sampleKey.StartsWith(ServiceCpuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = sampleKey[ServiceCpuPrefix.Length..];
            return isMemory ? sampleKey : $"{ServiceMemoryPrefix}{name}";
        }

        return null;
    }
}
