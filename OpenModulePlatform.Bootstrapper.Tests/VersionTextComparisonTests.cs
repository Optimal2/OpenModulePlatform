namespace OpenModulePlatform.Bootstrapper.Tests;

/// <summary>
/// The version comparator shared by the module-definition sync, the status
/// views and the package-library definition gate. A wrong answer here makes the
/// gate call a released definition stale against its own prerelease, or lets
/// the sync keep a prerelease copy over the release it precedes.
/// </summary>
public sealed class VersionTextComparisonTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3-rc1")]
    [InlineData("1.2.3", "1.2.3-alpha")]
    [InlineData("1.2.3", "1.2.3-rc.1")]
    [InlineData("1.2.4-rc1", "1.2.3")]
    [InlineData("1.2.3-rc2", "1.2.3-rc1")]
    [InlineData("1.2.3-rc.2", "1.2.3-rc.1")]
    [InlineData("1.2.3-rc.10", "1.2.3-rc.9")]
    [InlineData("1.2.3-beta", "1.2.3-alpha")]
    [InlineData("1.2.3-alpha.1", "1.2.3-alpha")]
    [InlineData("1.2.3-alpha.beta", "1.2.3-alpha.1")]
    [InlineData("1.2.10", "1.2.9")]
    [InlineData("0.3.855", "0.3.119")]
    [InlineData("1.0.20260928121732", "1.0.20260928121731")]
    public void TheLeftVersionIsNewer(string newer, string older)
    {
        Assert.True(Program.CompareVersionText(newer, older) > 0, $"{newer} should be newer than {older}.");
        Assert.True(Program.CompareVersionText(older, newer) < 0, $"{older} should be older than {newer}.");
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3-rc1", "1.2.3-RC1")]
    [InlineData("1.2.3+build.5", "1.2.3+build.7")]
    [InlineData("1.2.3-rc1+build.5", "1.2.3-rc1")]
    [InlineData("1.2.3-rc1", "1.2.3.0-rc1")]
    public void TheVersionsHaveTheSamePrecedence(string left, string right)
    {
        Assert.Equal(0, Program.CompareVersionText(left, right));
        Assert.Equal(0, Program.CompareVersionText(right, left));
    }

    [Fact]
    public void BuildMetadataDoesNotMakeAReleaseOutrankItsSuccessor()
    {
        Assert.True(Program.CompareVersionText("1.2.3+build.9", "1.2.4") < 0);
    }
}
