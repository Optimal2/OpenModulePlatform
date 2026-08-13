// File: OpenModulePlatform.HostAgent.Runtime.Tests/Services/HostResourceKeyVersionTests.cs
using OpenModulePlatform.HostAgent.Runtime.Services;
using Xunit;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// The service telemetry key must lose its version suffix and nothing else (R8-P5-24).
/// </summary>
/// <remarks>
/// The HostAgent's own Windows service name carries its version, so every upgrade used to open a
/// new key and omp.HostResourceLatest -- which keeps one row per key -- only ever showed the
/// current version's count. Strip too much and unrelated runtimes collapse into one series; strip
/// too little and the split returns. Both directions are asserted here.
/// </remarks>
public sealed class HostResourceKeyVersionTests
{
    [Theory]
    [InlineData("OMP.HostAgent.0.3.189", "OMP.HostAgent")]
    [InlineData("OMP.HostAgent.0.3.169", "OMP.HostAgent")]
    [InlineData("OMP.HostAgent.1.2.3.4", "OMP.HostAgent")]
    [InlineData("OMP.WorkerManager", "OMP.WorkerManager")]
    [InlineData("EArkivChecker", "EArkivChecker")]
    [InlineData("OMP.Service.ExampleServiceAppModule", "OMP.Service.ExampleServiceAppModule")]
    [InlineData("OMP.iKrock2.Backend", "OMP.iKrock2.Backend")]
    public void StripTrailingVersion_removes_only_a_trailing_dotted_version(string input, string expected)
    {
        Assert.Equal(expected, HostResourceCollector.StripTrailingVersion(input));
    }

    /// <summary>
    /// A name that is nothing but digits must survive: emptying the key would merge every such
    /// runtime into one nameless series.
    /// </summary>
    [Theory]
    [InlineData("0.3.189")]
    [InlineData("Service2")]
    public void StripTrailingVersion_never_empties_the_name(string input)
    {
        Assert.False(string.IsNullOrEmpty(HostResourceCollector.StripTrailingVersion(input)));
    }
}
