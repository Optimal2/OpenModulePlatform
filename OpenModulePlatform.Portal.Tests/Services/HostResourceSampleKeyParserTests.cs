// File: OpenModulePlatform.Portal.Tests/Services/HostResourceSampleKeyParserTests.cs
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class HostResourceSampleKeyParserTests
{
    [Theory]
    [InlineData("OMP.HostAgent.0.3.169", "OMP.HostAgent")]
    [InlineData("OMP.HostAgent.10.11", "OMP.HostAgent")]
    [InlineData("OMP.iKrock2.Backend", "OMP.iKrock2.Backend")]
    [InlineData("OMP.Service.ExampleServiceAppModule", "OMP.Service.ExampleServiceAppModule")]
    [InlineData("EArkivChecker", "EArkivChecker")]
    [InlineData("OMP_earkiv_checker_web", "OMP_earkiv_checker_web")]
    [InlineData("OMP.WorkerManager", "OMP.WorkerManager")]
    [InlineData("Service.7", "Service.7")]
    [InlineData("", "")]
    public void NormalizeRuntimeName_strips_only_dotted_version_suffixes(string input, string expected)
    {
        Assert.Equal(expected, HostResourceSampleKeyParser.NormalizeRuntimeName(input));
    }

    [Theory]
    [InlineData("service.OMP.HostAgent.0.3.169", "service.OMP.HostAgent")]
    [InlineData("service.memory.OMP.HostAgent.0.3.169", "service.memory.OMP.HostAgent")]
    [InlineData("service.state.OMP.HostAgent.0.3.169", "service.state.OMP.HostAgent")]
    [InlineData("service.OMP.WorkerManager", "service.OMP.WorkerManager")]
    [InlineData("iis.apppool.memory.OMP_content_webapp_webapp", "iis.apppool.memory.OMP_content_webapp_webapp")]
    [InlineData("unknown.key", "unknown.key")]
    public void NormalizeSampleKey_normalizes_the_runtime_name_portion(string input, string expected)
    {
        Assert.Equal(expected, HostResourceSampleKeyParser.NormalizeSampleKey(input));
    }

    [Fact]
    public void Parse_keeps_versioned_runtime_names_intact()
    {
        var parts = HostResourceSampleKeyParser.Parse("service.memory.OMP.HostAgent.0.3.169");

        Assert.Equal("OMP.HostAgent.0.3.169", parts.RuntimeName);
        Assert.Equal(HostResourceMetricKind.Memory, parts.MetricKind);
    }

    /// <summary>
    /// The worker fleet and the app pool state key must parse, or their samples are stored and
    /// then dropped on the way out.
    /// </summary>
    /// <remarks>
    /// R8-P5-20 and R8-P5-21. Both prefixes overlap a shorter one that was already handled --
    /// "worker.memory." sits under "worker." and "iis.apppool.state." under "iis.apppool." -- so
    /// the longer prefix has to be tested before the shorter one. Getting that order wrong does
    /// not fail the build; it silently files every memory sample as a CPU sample.
    /// </remarks>
    [Theory]
    [InlineData("worker.memory.ibs-packager-worker-1", "Worker process", "ibs-packager-worker-1", HostResourceMetricKind.Memory)]
    [InlineData("worker.ibs-packager-worker-1", "Worker process", "ibs-packager-worker-1", HostResourceMetricKind.Cpu)]
    [InlineData("iis.apppool.state.OMP_portal", "IIS app pool state", "OMP_portal", HostResourceMetricKind.State)]
    [InlineData("iis.apppool.memory.OMP_portal", "IIS app pool", "OMP_portal", HostResourceMetricKind.Memory)]
    [InlineData("iis.apppool.OMP_portal", "IIS app pool", "OMP_portal", HostResourceMetricKind.Cpu)]
    internal void Parse_handles_worker_and_app_pool_state_keys(
        string sampleKey,
        string expectedRuntimeKind,
        string expectedRuntimeName,
        HostResourceMetricKind expectedMetricKind)
    {
        var parts = HostResourceSampleKeyParser.Parse(sampleKey);

        Assert.Equal(expectedRuntimeKind, parts.RuntimeKind);
        Assert.Equal(expectedRuntimeName, parts.RuntimeName);
        Assert.Equal(expectedMetricKind, parts.MetricKind);
    }
}
