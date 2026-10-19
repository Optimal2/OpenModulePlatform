using OpenModulePlatform.Auth.Services;

namespace OpenModulePlatform.Portal.Tests.Security;

public sealed class OmpRuntimeAssemblyVersionCheckTests
{
    private static readonly Version NegotiateBand = new(10, 0, 11);

    [Theory]
    [InlineData(10, 0, 11, 0)]
    [InlineData(10, 0, 11, 1)]
    [InlineData(10, 0, 11, 12345)]
    public void IsOnBand_AcceptsAnyRevisionOnTheThreePartBand(int major, int minor, int build, int revision)
    {
        var version = new Version(major, minor, build, revision);

        Assert.True(OmpRuntimeAssemblyVersionCheck.IsOnBand(version, NegotiateBand));
    }

    [Theory]
    [InlineData(10, 0, 9, 0)]
    [InlineData(10, 0, 10, 0)]
    [InlineData(10, 1, 11, 0)]
    [InlineData(9, 0, 11, 99999)]
    [InlineData(11, 0, 11, 0)]
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
            new Version(10, 0, 13, 42),
            OmpRuntimeAssemblyVersionCheck.ExpectedNegotiateBand));
        Assert.False(OmpRuntimeAssemblyVersionCheck.IsOnBand(
            new Version(10, 0, 12, 42),
            OmpRuntimeAssemblyVersionCheck.ExpectedNegotiateBand));
        Assert.False(OmpRuntimeAssemblyVersionCheck.IsOnBand(
            new Version(10, 0, 11, 42),
            OmpRuntimeAssemblyVersionCheck.ExpectedNegotiateBand));
    }

    [Fact]
    public void ExpectedNegotiateBand_FollowsTheCentralPackageVersion()
    {
        // A security bump of the Negotiate package must move the runtime
        // check with it; otherwise /runtime-versions warns on patched hosts
        // and accepts the vulnerable band.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var props = System.Xml.Linq.XDocument.Load(Path.Combine(directory!.FullName, "Directory.Packages.props"));
        var packageVersion = props.Descendants("PackageVersion")
            .Single(element => (string?)element.Attribute("Include") == OmpRuntimeAssemblyVersionCheck.NegotiateAssemblyName)
            .Attribute("Version")!.Value;
        var parsed = Version.Parse(packageVersion);

        Assert.Equal(
            new Version(parsed.Major, parsed.Minor, parsed.Build),
            OmpRuntimeAssemblyVersionCheck.ExpectedNegotiateBand);
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
