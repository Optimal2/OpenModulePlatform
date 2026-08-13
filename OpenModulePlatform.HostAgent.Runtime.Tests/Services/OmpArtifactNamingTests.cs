// File: OpenModulePlatform.HostAgent.Runtime.Tests/Services/OmpArtifactNamingTests.cs
using OpenModulePlatform.Artifacts;
using Xunit;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

/// <summary>
/// R8-P2-16..23: artifact identity fields come from database rows and package manifests,
/// and they end up as path segments and zip entry names.
/// </summary>
public sealed class OmpArtifactNamingTests
{
    [Theory]
    [InlineData("../../evil")]
    [InlineData("..\\..\\evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void SanitizePathSegment_never_returns_something_that_can_traverse(string value)
    {
        var sanitized = OmpArtifactNaming.SanitizePathSegment(value);

        Assert.DoesNotContain("..", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("/", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", sanitized, StringComparison.Ordinal);
        Assert.Equal(sanitized, Path.GetFileName(sanitized));
    }

    [Fact]
    public void SanitizePathSegment_keeps_an_ordinary_value_recognizable()
    {
        Assert.Equal("ibs-packager-web", OmpArtifactNaming.SanitizePathSegment("ibs-packager-web"));
        Assert.Equal("0.3.232", OmpArtifactNaming.SanitizePathSegment("0.3.232"));
    }

    [Fact]
    public void SanitizePathSegment_never_returns_an_empty_segment()
    {
        // An empty segment would collapse the path rather than name a level in it.
        Assert.NotEqual(string.Empty, OmpArtifactNaming.SanitizePathSegment(""));
        Assert.NotEqual(string.Empty, OmpArtifactNaming.SanitizePathSegment("   "));
        Assert.NotEqual(string.Empty, OmpArtifactNaming.SanitizePathSegment(null));
    }

    [Fact]
    public void CreateArtifactPackageFileName_sanitizes_every_part()
    {
        // The version is the field an artifact author controls most freely, and the
        // result is used both as a file name and as a zip entry name.
        var fileName = OmpArtifactNaming.CreateArtifactPackageFileName(
            "ibs_packager",
            "ibs_packager_web",
            "web-app",
            "ibs-packager-web",
            "../../../Windows/System32/evil");

        Assert.DoesNotContain("..", fileName, StringComparison.Ordinal);
        Assert.DoesNotContain("/", fileName, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", fileName, StringComparison.Ordinal);
        Assert.Equal(fileName, Path.GetFileName(fileName));
        Assert.EndsWith(".zip", fileName, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateArtifactPackageFileName_is_unchanged_for_ordinary_values()
    {
        Assert.Equal(
            "ibs_packager__ibs_packager_web__web-app__ibs-packager-web__0.3.232.zip",
            OmpArtifactNaming.CreateArtifactPackageFileName(
                "ibs_packager",
                "ibs_packager_web",
                "web-app",
                "ibs-packager-web",
                "0.3.232"));
    }
}
