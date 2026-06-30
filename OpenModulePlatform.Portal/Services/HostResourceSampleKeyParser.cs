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

        if (sampleKey.StartsWith(IisAppPoolCpuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new HostResourceSampleKeyParts(
                IisAppPoolRuntimeKind,
                sampleKey[IisAppPoolCpuPrefix.Length..],
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
