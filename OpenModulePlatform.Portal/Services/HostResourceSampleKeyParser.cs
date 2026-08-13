namespace OpenModulePlatform.Portal.Services;

internal enum HostResourceMetricKind
{
    Unknown,
    Cpu,
    Memory,
    State
}

internal readonly record struct HostResourceSampleKeyParts(
    string RuntimeKind,
    string RuntimeName,
    HostResourceMetricKind MetricKind);

internal static class HostResourceSampleKeyParser
{
    private const string IisAppPoolRuntimeKind = "IIS app pool";
    private const string WindowsServiceRuntimeKind = "Windows service";
    private const string WindowsServiceStateRuntimeKind = "Windows service state";
    private const string IisAppPoolCpuPrefix = "iis.apppool.";
    private const string IisAppPoolMemoryPrefix = "iis.apppool.memory.";
    private const string ServiceCpuPrefix = "service.";
    private const string ServiceMemoryPrefix = "service.memory.";
    private const string ServiceStatePrefix = "service.state.";
    private const string IisAppPoolStatePrefix = "iis.apppool.state.";
    private const string WorkerCpuPrefix = "worker.";
    private const string WorkerMemoryPrefix = "worker.memory.";
    private const string WorkerRuntimeKind = "Worker process";
    private const string IisAppPoolStateRuntimeKind = "IIS app pool state";

    public static HostResourceSampleKeyParts Parse(string? sampleKey)
    {
        if (string.IsNullOrWhiteSpace(sampleKey))
        {
            return new HostResourceSampleKeyParts(string.Empty, string.Empty, HostResourceMetricKind.Unknown);
        }

        if (sampleKey.StartsWith(IisAppPoolMemoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new HostResourceSampleKeyParts(
                IisAppPoolRuntimeKind,
                sampleKey[IisAppPoolMemoryPrefix.Length..],
                HostResourceMetricKind.Memory);
        }

        if (sampleKey.StartsWith(IisAppPoolStatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new HostResourceSampleKeyParts(
                IisAppPoolStateRuntimeKind,
                sampleKey[IisAppPoolStatePrefix.Length..],
                HostResourceMetricKind.State);
        }

        if (sampleKey.StartsWith(IisAppPoolCpuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new HostResourceSampleKeyParts(
                IisAppPoolRuntimeKind,
                sampleKey[IisAppPoolCpuPrefix.Length..],
                HostResourceMetricKind.Cpu);
        }

        if (sampleKey.StartsWith(WorkerMemoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new HostResourceSampleKeyParts(
                WorkerRuntimeKind,
                sampleKey[WorkerMemoryPrefix.Length..],
                HostResourceMetricKind.Memory);
        }

        if (sampleKey.StartsWith(WorkerCpuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new HostResourceSampleKeyParts(
                WorkerRuntimeKind,
                sampleKey[WorkerCpuPrefix.Length..],
                HostResourceMetricKind.Cpu);
        }

        if (sampleKey.StartsWith(ServiceMemoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new HostResourceSampleKeyParts(
                WindowsServiceRuntimeKind,
                sampleKey[ServiceMemoryPrefix.Length..],
                HostResourceMetricKind.Memory);
        }

        if (sampleKey.StartsWith(ServiceStatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new HostResourceSampleKeyParts(
                WindowsServiceStateRuntimeKind,
                sampleKey[ServiceStatePrefix.Length..],
                HostResourceMetricKind.State);
        }

        if (sampleKey.StartsWith(ServiceCpuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new HostResourceSampleKeyParts(
                WindowsServiceRuntimeKind,
                sampleKey[ServiceCpuPrefix.Length..],
                HostResourceMetricKind.Cpu);
        }

        return new HostResourceSampleKeyParts(string.Empty, string.Empty, HostResourceMetricKind.Unknown);
    }

    /// <summary>
    /// Strips a trailing dotted numeric version (for example ".0.3.169") from
    /// a runtime name, so resource telemetry for version-suffixed runtimes
    /// such as the HostAgent service merges into one series across upgrades.
    /// At least two numeric segments are required, so names that merely end in
    /// one number keep their identity.
    /// </summary>
    /// <summary>
    /// Maps a state runtime kind back to the kind whose CPU and memory it belongs with.
    /// </summary>
    /// <remarks>
    /// R8-P5-23. Every state sample used to be assumed to come from a Windows service, so the page
    /// hardcoded that kind. That held only while services were the sole publisher of state; once
    /// IIS app pools started publishing one too, each pool grew a second, empty row under the
    /// wrong kind. Deriving the owner from the parsed kind keeps the next source that publishes
    /// state from repeating it.
    /// </remarks>
    public static string ToOwningRuntimeKind(string runtimeKind)
    {
        if (string.IsNullOrWhiteSpace(runtimeKind))
        {
            return runtimeKind ?? string.Empty;
        }

        const string stateSuffix = " state";
        return runtimeKind.EndsWith(stateSuffix, StringComparison.OrdinalIgnoreCase)
            ? runtimeKind[..^stateSuffix.Length]
            : runtimeKind;
    }

    public static string NormalizeRuntimeName(string runtimeName)
    {
        if (string.IsNullOrEmpty(runtimeName))
        {
            return runtimeName ?? string.Empty;
        }

        var index = runtimeName.Length;
        var numericSegments = 0;
        while (index > 0)
        {
            var dot = runtimeName.LastIndexOf('.', index - 1);
            if (dot <= 0 || dot == index - 1)
            {
                break;
            }

            var segmentIsNumeric = true;
            for (var position = dot + 1; position < index; position++)
            {
                if (!char.IsDigit(runtimeName[position]))
                {
                    segmentIsNumeric = false;
                    break;
                }
            }

            if (!segmentIsNumeric)
            {
                break;
            }

            numericSegments++;
            index = dot;
        }

        return numericSegments >= 2 ? runtimeName[..index] : runtimeName;
    }

    /// <summary>
    /// Returns the sample key with any version suffix removed from its runtime
    /// name portion, or the key unchanged when it has no known prefix.
    /// </summary>
    public static string NormalizeSampleKey(string sampleKey)
    {
        if (string.IsNullOrWhiteSpace(sampleKey))
        {
            return sampleKey ?? string.Empty;
        }

        foreach (var prefix in new[] { IisAppPoolMemoryPrefix, IisAppPoolStatePrefix, WorkerMemoryPrefix, ServiceMemoryPrefix, ServiceStatePrefix, IisAppPoolCpuPrefix, WorkerCpuPrefix, ServiceCpuPrefix })
        {
            if (sampleKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return prefix + NormalizeRuntimeName(sampleKey[prefix.Length..]);
            }
        }

        return sampleKey;
    }

    public static string? DeriveCounterpartSampleKey(string sampleKey)
    {
        if (sampleKey.StartsWith(IisAppPoolCpuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return $"{IisAppPoolMemoryPrefix}{sampleKey[IisAppPoolCpuPrefix.Length..]}";
        }

        if (sampleKey.StartsWith(IisAppPoolMemoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return $"{IisAppPoolCpuPrefix}{sampleKey[IisAppPoolMemoryPrefix.Length..]}";
        }

        if (sampleKey.StartsWith(ServiceMemoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return $"{ServiceCpuPrefix}{sampleKey[ServiceMemoryPrefix.Length..]}";
        }

        if (sampleKey.StartsWith(ServiceStatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (sampleKey.StartsWith(ServiceCpuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return $"{ServiceMemoryPrefix}{sampleKey[ServiceCpuPrefix.Length..]}";
        }

        return null;
    }
}
