using OpenModulePlatform.Artifacts;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class ArtifactVersionComparerTests
{
    [Theory]
    [InlineData("1.0.10", "1.0.2", 1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("2.0", "10.0-alpha", -1)]
    public void Compare_UsesNumericVersionOrdering_WhenBothSidesParse(string left, string right, int expectedSign)
    {
        var result = ArtifactVersionComparer.Compare(left, right);

        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Theory]
    [InlineData("1.2.3-beta", "1.2.3", 0)]
    [InlineData("1.2.4+build.7", "1.2.3", 1)]
    [InlineData("1.2.3-alpha", "1.2.4+build.7", -1)]
    public void Compare_StripsSuffixesBeforeNumericComparison(string left, string right, int expectedSign)
    {
        var result = ArtifactVersionComparer.Compare(left, right);

        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Theory]
    [InlineData(null, null, 0)]
    [InlineData("", " ", 0)]
    [InlineData(null, "1.0.0", -1)]
    [InlineData("1.0.0", null, 1)]
    public void Compare_TrimsAndOrdersEmptyOrNullValues(string? left, string? right, int expectedSign)
    {
        var result = ArtifactVersionComparer.Compare(left, right);

        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Theory]
    [InlineData("beta", "alpha", 1)]
    [InlineData("Alpha", "alpha", 0)]
    [InlineData("release-10", "release-2", -1)]
    public void Compare_FallsBackToOrdinalIgnoreCaseTextOrdering(string left, string right, int expectedSign)
    {
        var result = ArtifactVersionComparer.Compare(left, right);

        Assert.Equal(expectedSign, Math.Sign(result));
    }
}
