using System.Reflection;
using System.Xml.Linq;
using OpenModulePlatform.Web.Shared.Runtime;

namespace OpenModulePlatform.Portal.Tests.Runtime;

public sealed class OmpRuntimeAdvisoryTests
{
    [Theory]
    [InlineData(10, 0, 14, "10.0.15", true)]
    [InlineData(10, 0, 15, "10.0.15", false)]
    [InlineData(10, 0, 16, "10.0.15", false)]
    [InlineData(9, 0, 14, "10.0.15", false)]
    [InlineData(11, 0, 14, "10.0.15", false)]
    [InlineData(10, 1, 0, "10.0.15", false)]
    public void Compare_OnlyFlagsAnOlderPatchOnTheSameMajorMinor(
        int major,
        int minor,
        int build,
        string recommended,
        bool expected)
    {
        var running = new Version(major, minor, build);

        var result = OmpRuntimeAdvisory.Compare(running, recommended);

        Assert.Equal(expected, result.IsOutdated);
        Assert.Equal(running, result.Running);
        Assert.Equal(Version.Parse(recommended), result.Recommended);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void Compare_UnparseableOrMissingRecommendationIsNotOutdated(string? recommended)
    {
        var running = new Version(10, 0, 0);

        var result = OmpRuntimeAdvisory.Compare(running, recommended);

        Assert.False(result.IsOutdated);
        Assert.Equal(running, result.Running);
        Assert.Null(result.Recommended);
    }

    [Fact]
    public void Compare_MissingRunningVersionIsNotOutdated()
    {
        var result = OmpRuntimeAdvisory.Compare(null, "10.0.15");

        Assert.False(result.IsOutdated);
        Assert.Null(result.Running);
        Assert.Null(result.Recommended);
    }

    [Fact]
    public void Evaluate_MissingMetadataIsNotOutdated()
    {
        var running = new Version(10, 0, 0);

        var result = OmpRuntimeAdvisory.Evaluate(typeof(object).Assembly, running);

        Assert.False(result.IsOutdated);
        Assert.Equal(running, result.Running);
        Assert.Null(result.Recommended);
    }

    [Fact]
    public void EvaluateCurrentProcess_ReadsTheWebSharedRecommendationWithoutThrowing()
    {
        var result = OmpRuntimeAdvisory.EvaluateCurrentProcess();

        Assert.Equal(Environment.Version, result.Running);
        Assert.NotNull(result.Recommended);
    }

    [Fact]
    public void StampedRecommendation_FollowsTheCentralHostingPackageVersion()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var props = XDocument.Load(Path.Combine(directory!.FullName, "Directory.Packages.props"));
        var packageVersion = props.Descendants("PackageVersion")
            .Single(element => (string?)element.Attribute("Include") == "Microsoft.Extensions.Hosting")
            .Attribute("Version")!.Value;

        var attribute = typeof(OmpRuntimeAdvisory).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(candidate =>
                candidate.Key == OmpRuntimeAdvisory.RecommendedRuntimeMetadataKey);

        Assert.NotNull(attribute);
        Assert.Equal(packageVersion, attribute!.Value);
    }
}
