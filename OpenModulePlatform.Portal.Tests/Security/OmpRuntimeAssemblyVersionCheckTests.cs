using OpenModulePlatform.Auth.Services;

namespace OpenModulePlatform.Portal.Tests.Security;

public sealed class OmpRuntimeAssemblyVersionCheckTests
{
    private static readonly Version NegotiateBand = new(10, 0, 10);

    [Theory]
    [InlineData(10, 0, 10, 0)]
    [InlineData(10, 0, 10, 1)]
    [InlineData(10, 0, 10, 12345)]
    public void IsOnBand_AcceptsAnyRevisionOnTheThreePartBand(int major, int minor, int build, int revision)
    {
        var version = new Version(major, minor, build, revision);

        Assert.True(OmpRuntimeAssemblyVersionCheck.IsOnBand(version, NegotiateBand));
    }

    [Theory]
    [InlineData(10, 0, 9, 0)]
    [InlineData(10, 0, 11, 0)]
    [InlineData(10, 1, 10, 0)]
    [InlineData(9, 0, 10, 99999)]
    [InlineData(11, 0, 10, 0)]
    public void IsOnBand_RejectsVersionsOutsideTheBand(int major, int minor, int build, int revision)
    {
        var version = new Version(major, minor, build, revision);

        Assert.False(OmpRuntimeAssemblyVersionCheck.IsOnBand(version, NegotiateBand));
    }

    [Fact]
    public void IsOnBand_RejectsNullVersion()
    {
        Assert.False(OmpRuntimeAssemblyVersionCheck.IsOnBand(null, NegotiateBand));
    }

    [Fact]
    public void IsOnBand_UsesTheThreePartBandDefinedByTheConstant()
    {
        Assert.True(OmpRuntimeAssemblyVersionCheck.IsOnBand(
            new Version(10, 0, 10, 42),
            OmpRuntimeAssemblyVersionCheck.ExpectedNegotiateBand));
        Assert.False(OmpRuntimeAssemblyVersionCheck.IsOnBand(
            new Version(10, 0, 9, 42),
            OmpRuntimeAssemblyVersionCheck.ExpectedNegotiateBand));
    }

    [Fact]
    public void CreateReport_ReportsBothTrackedAssembliesWithFourPartVersions()
    {
        var report = OmpRuntimeAssemblyVersionCheck.CreateReport();

        Assert.Contains(report.Assemblies, entry =>
            entry.AssemblyName == OmpRuntimeAssemblyVersionCheck.NegotiateAssemblyName);
        var authEntry = Assert.Single(report.Assemblies, entry =>
            entry.AssemblyName == OmpRuntimeAssemblyVersionCheck.AuthAssemblyName);

        // The Auth assembly is loaded by this test process and must report a
        // full four-part version.
        Assert.NotNull(authEntry.LoadedVersion);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", authEntry.LoadedVersion);
        Assert.Null(authEntry.Warning);
    }
}
