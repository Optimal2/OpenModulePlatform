using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// The post-SQL pointer re-apply picks "the newest artifact per app". That choice used to
/// live in a PARSENAME ORDER BY, which ranks "1.2.3-beta" below "1.2.1" (the suffixed part
/// converts to NULL, then 0) while the import path's ArtifactVersionComparer strips the
/// suffix and ranks it above - so SQL and the import could disagree on the winner whenever
/// suffixed and plain versions coexist for one app. Red before the choice moved into C#.
/// </summary>
public sealed class ApplyLatestModuleArtifactsSelectionTests
{
    [Fact]
    public void SuffixedVersionOutranksLowerPlainVersion_LikeTheImportPath()
    {
        var winners = OmpHostArtifactRepository.SelectNewestArtifactPerApp(new[]
        {
            (AppId: 1, ArtifactId: 10, Version: "1.2.1"),
            (AppId: 1, ArtifactId: 11, Version: "1.2.3-beta"),
        });

        Assert.Equal(new[] { 11 }, winners);
    }

    [Fact]
    public void EqualVersionsFallBackToTheHigherArtifactId()
    {
        var winners = OmpHostArtifactRepository.SelectNewestArtifactPerApp(new[]
        {
            (AppId: 1, ArtifactId: 12, Version: "2.0.0"),
            (AppId: 1, ArtifactId: 11, Version: "2.0.0"),
        });

        Assert.Equal(new[] { 12 }, winners);
    }

    [Fact]
    public void OneWinnerPerApp_AndNothingForAnAppWithoutCandidates()
    {
        var winners = OmpHostArtifactRepository.SelectNewestArtifactPerApp(new[]
        {
            (AppId: 1, ArtifactId: 1, Version: "1.0.0"),
            (AppId: 1, ArtifactId: 2, Version: "1.0.1"),
            (AppId: 2, ArtifactId: 3, Version: "0.9.0"),
        });

        Assert.Equal(new[] { 2, 3 }, winners.OrderBy(static id => id));
    }

    [Fact]
    public void NoCandidates_NoWinners()
    {
        Assert.Empty(OmpHostArtifactRepository.SelectNewestArtifactPerApp(
            Array.Empty<(int AppId, int ArtifactId, string Version)>()));
    }
}
