using System.Collections.Specialized;
using System.Reflection;
using System.Xml.Linq;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.HostAgent.Sentinel;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class HostAgentSentinelTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static AgentSnapshot Agent(string name = "OMP.HostAgent.1.2.3", string status = "Running", bool alive = true)
        => new() { Name = name, Status = status, ProcessAlive = alive, StartedUtc = Now.AddHours(-1) };

    [Theory]
    [InlineData("OMP.HostAgent.1.2.3", true)]
    [InlineData("omp.hostagent.10.20.300", true)]
    [InlineData("OMP.HostAgent.Sentinel", false)]
    [InlineData("OMP.HostAgent", false)]
    [InlineData("OMP.HostAgent.1.2.3.preview", false)]
    [InlineData("OMP.HostAgent.1.2.3\n", false)]
    [InlineData("Other.OMP.HostAgent.1.2.3", false)]
    [InlineData("OMP.HostAgent.1.2", false)]
    public void SelectionUsesOnlyVersionedServiceNames(string name, bool expected)
        => Assert.Equal(expected, SentinelPolicy.IsAgent(name));

    [Fact]
    public void MissingAgentIncludingSentinelOnlyProduces102()
    {
        Assert.Equal(102, SentinelPolicy.Evaluate([]).EventId);
        Assert.Equal(102, SentinelPolicy.Evaluate([Agent("OMP.HostAgent.Sentinel")]).EventId);
    }

    [Fact]
    public void SingleRunningAgentWithLiveProcessIsHealthy()
    {
        var result = SentinelPolicy.Evaluate([Agent(), Agent("OMP.HostAgent.Sentinel")]);
        Assert.Equal(0, result.EventId);
        Assert.Contains(Now.AddHours(-1).ToString("O"), result.Message);
    }

    [Theory]
    [InlineData("Stopped", false)]
    [InlineData("StartPending", true)]
    [InlineData("StopPending", true)]
    [InlineData("Running", false)]
    public void NonRunningOrDeadProcessProduces100(string status, bool alive)
        => Assert.Equal(100, SentinelPolicy.Evaluate([Agent(status: status, alive: alive)]).EventId);

    [Fact]
    public void DuplicateIncludesStoppedOldServiceAndTakesPrecedence()
        => Assert.Equal(101, SentinelPolicy.Evaluate([Agent(), Agent("OMP.HostAgent.1.2.2", "Stopped", false)]).EventId);

    [Fact]
    public void RecoveryOccursExactlyOnceAndHeartbeatIsPeriodic()
    {
        var state = new SentinelState(false);
        var ok = SentinelPolicy.Evaluate([Agent()]);
        Assert.Equal([10], state.Apply(ok, Now, 10));
        Assert.Empty(state.Apply(ok, Now.AddMinutes(9), 10));
        Assert.Equal([10], state.Apply(ok, Now.AddMinutes(10), 10));
        Assert.Equal([100], state.Apply(new Observation(100, "down"), Now.AddMinutes(11), 10));
        Assert.Equal([100], state.Apply(new Observation(100, "down"), Now.AddMinutes(12), 10));
        Assert.True(state.Faulted);
        Assert.Equal([1, 10], state.Apply(ok, Now.AddMinutes(13), 10));
        Assert.False(state.Faulted);
        Assert.Empty(state.Apply(ok, Now.AddMinutes(14), 10));
    }

    [Fact]
    public void RestartRestoresFaultAndEmitsRecoveryOnlyOnce()
    {
        var state = new SentinelState(true);
        var ok = SentinelPolicy.Evaluate([Agent()]);
        Assert.Equal([1, 10], state.Apply(ok, Now, 10));
        Assert.Empty(state.Apply(ok, Now.AddSeconds(30), 10));
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(102, true)]
    [InlineData(101, false)]
    [InlineData(103, false)]
    [InlineData(0, false)]
    public void OnlyMissingOrDownAgentStopsService(int id, bool expected)
    {
        Assert.Equal(expected, SentinelState.ShouldStop(id, true));
        Assert.False(SentinelState.ShouldStop(id, false));
    }

    [Fact]
    public void DatabaseHeartbeatBoundaryAndMissingRow()
    {
        Assert.Null(SentinelPolicy.DatabaseHeartbeat(Now.AddMinutes(-15), Now, 15));
        Assert.Equal(103, SentinelPolicy.DatabaseHeartbeat(Now.AddMinutes(-15).AddTicks(-1), Now, 15).EventId);
        Assert.Equal(103, SentinelPolicy.DatabaseHeartbeat(null, Now, 15).EventId);
    }

    [Fact]
    public void DefaultsMatchShippedConfiguration()
    {
        var values = new NameValueCollection();
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Sentinel.config"));
        foreach (var add in document.Root!.Element("appSettings")!.Elements("add"))
            values.Add((string)add.Attribute("key")!, (string)add.Attribute("value")!);
        foreach (var config in new[] { SentinelSettings.Read(new NameValueCollection()), SentinelSettings.Read(values) })
        {
            Assert.Equal(30, config.PollIntervalSeconds);
            Assert.Equal(10, config.OkHeartbeatMinutes);
            Assert.Equal(15, config.HeartbeatStaleMinutes);
            Assert.True(config.StopSelfWhenHostAgentDown);
            Assert.False(config.CheckDatabaseHeartbeat);
            Assert.False(config.DatabaseEnabled);
        }
    }

    [Theory]
    [InlineData("PollIntervalSeconds", "0")]
    [InlineData("PollIntervalSeconds", "3601")]
    [InlineData("PollIntervalSeconds", "2147483648")]
    [InlineData("OkHeartbeatMinutes", "-1")]
    [InlineData("HeartbeatStaleMinutes", "no")]
    [InlineData("StopSelfWhenHostAgentDown", "1")]
    [InlineData("CheckDatabaseHeartbeat", "")]
    public void InvalidConfigurationIsRejected(string key, string value)
        => Assert.Throws<ArgumentException>(() => SentinelSettings.Read(new NameValueCollection { { key, value } }));

    [Fact]
    public void OverridesAndEmptyDatabaseConnectionAreHonored()
    {
        var values = new NameValueCollection
        {
            { "PollIntervalSeconds", "5" }, { "OkHeartbeatMinutes", "2" },
            { "HeartbeatStaleMinutes", "3" }, { "StopSelfWhenHostAgentDown", "false" },
            { "CheckDatabaseHeartbeat", "true" }, { "DatabaseConnectionString", " " }
        };
        var config = SentinelSettings.Read(values);
        Assert.Equal(5, config.PollIntervalSeconds);
        Assert.Equal(2, config.OkHeartbeatMinutes);
        Assert.Equal(3, config.HeartbeatStaleMinutes);
        Assert.False(config.StopSelfWhenHostAgentDown);
        Assert.False(config.DatabaseEnabled);
        values["DatabaseConnectionString"] = "Server=localhost;Database=example;Integrated Security=true";
        Assert.True(SentinelSettings.Read(values).DatabaseEnabled);
    }

    [Theory]
    [InlineData("OMP.HostAgent.Sentinel", false)]
    [InlineData("omp.hostagent.sentinel", false)]
    [InlineData("OMP.HostAgent.0.3.262", true)]
    [InlineData("OMP.HostAgent", true)]
    public void UpgradeAndRetirementEnumerationNeverIncludesSentinel(string name, bool expected)
    {
        var method = typeof(HostAgentSelfUpgradeService).GetMethod("IsHostAgentServiceName", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(expected, method.Invoke(null, [name, new[] { "OMP.HostAgent" }]));
    }
}
