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
}
