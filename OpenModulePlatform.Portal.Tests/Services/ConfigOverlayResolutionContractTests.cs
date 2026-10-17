using System.Text.Json.Nodes;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using static OpenModulePlatform.HostAgent.Runtime.Services.OmpHostArtifactRepository;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>Exercises the production resolver in memory, without a SQL connection.</summary>
public sealed class ConfigOverlayResolutionContractTests
{
    [Fact]
    public void HostSpecificScope_BeatsNewerAllHostsVersion()
    {
        var allHosts = Row(1, "9.0.0", "{}");
        var host = Row(2, "1.0.0", "{}");
        Assert.True(ConfigurationFileCandidateWins(host, allHosts));
        Assert.False(ConfigurationFileCandidateWins(allHosts, host));
    }

    [Fact]
    public void MergesLayerBaseAllHostsAndHost_UsingOnlyTheWinnerPerScope()
    {
        var baseRow = Row(0, null, """{"base":true,"value":0}""");
        var obsolete = Row(1, "1.0.0", """{"obsolete":true}""");
        var allHosts = Row(1, "2.0.0", """{"shared":true,"value":1}""");
        var host = Row(2, "1.0.0", """{"value":2}""");
        var json = JsonNode.Parse(ResolveLayeredConfigurationContent([host, obsolete, baseRow, allHosts], host))!;
        Assert.True(json["base"]!.GetValue<bool>());
        Assert.True(json["shared"]!.GetValue<bool>());
        Assert.Equal(2, json["value"]!.GetValue<int>());
        Assert.Null(json["obsolete"]);
    }

    [Fact]
    public void HostReplace_DiscardsEarlierLayers()
    {
        var baseRow = Row(0, null, """{"base":true}""");
        var allHosts = Row(1, "9.0.0", """{"shared":true}""");
        var host = Row(2, "1.0.0", """{"host":true}""", mergeMode: "replace");
        Assert.Equal(host.Descriptor.FileContent, ResolveLayeredConfigurationContent([host, baseRow, allHosts], host));
    }

    [Fact]
    public void SameScope_TiesUseVersionThenTimestampThenRowId()
    {
        var original = Row(1, "1.0.0", "{}");
        var newerVersion = original with { OverlayVersion = "2.0.0", SourceUpdatedUtc = original.SourceUpdatedUtc.AddDays(-1) };
        var newerTimestamp = original with { SourceUpdatedUtc = original.SourceUpdatedUtc.AddSeconds(1) };
        var higherId = Row(1, "1.0.0", "{}", id: 2);
        Assert.True(ConfigurationFileCandidateWins(newerVersion, original));
        Assert.True(ConfigurationFileCandidateWins(newerTimestamp, original));
        Assert.True(ConfigurationFileCandidateWins(higherId, original));
        Assert.False(ConfigurationFileCandidateWins(original, newerVersion));
        Assert.False(ConfigurationFileCandidateWins(original, newerTimestamp));
        Assert.False(ConfigurationFileCandidateWins(original, higherId));
    }

    private static ResolvedConfigurationFile Row(int priority, string? version, string content, string? mergeMode = null, int id = 1)
        => new(new ArtifactConfigurationFileDescriptor
        {
            ArtifactConfigurationFileId = id,
            ArtifactId = 1,
            RelativePath = "appsettings.json",
            FileContent = content
        }, priority, new DateTime(2026, 1, 1), version, mergeMode, null);
}
