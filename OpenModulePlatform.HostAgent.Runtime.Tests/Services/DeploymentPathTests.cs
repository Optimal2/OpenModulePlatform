using OpenModulePlatform.HostAgent.Runtime.Services;

namespace OpenModulePlatform.HostAgent.Runtime.Tests.Services;

public sealed class DeploymentPathTests
{
    [Fact]
    public void CombineUnderRoot_ValidRelativePath_CombinesCorrectly()
    {
        var root = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"omp-deploy-root-{Guid.NewGuid():N}"));
        var relative = "sub/dir";

        var actual = DeploymentPath.CombineUnderRoot(root, relative, "test");

        Assert.Equal(Path.GetFullPath(Path.Join(root, "sub", "dir")), actual);
    }

    [Theory]
    [InlineData("C:\\abs")]
    [InlineData("/abs")]
    public void CombineUnderRoot_RootedRelativePath_Throws(string relative)
    {
        var root = Path.GetTempPath();

        var ex = Assert.Throws<InvalidOperationException>(() => DeploymentPath.CombineUnderRoot(root, relative, "test"));

        Assert.Contains("relative path", ex.Message);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("sub/../../escape")]
    public void CombineUnderRoot_ParentSegment_Throws(string relative)
    {
        var root = Path.GetTempPath();

        Assert.Throws<InvalidOperationException>(() => DeploymentPath.CombineUnderRoot(root, relative, "test"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    public void CombineUnderRoot_EmptyOrCurrentRelativePath_ReturnsRoot(string relative)
    {
        var root = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"omp-deploy-root-{Guid.NewGuid():N}"));

        var actual = DeploymentPath.CombineUnderRoot(root, relative, "test");

        Assert.Equal(root, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CombineUnderRoot_NullOrWhitespaceRoot_Throws(string? root)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DeploymentPath.CombineUnderRoot(root!, "sub", "test"));
        Assert.Contains("root path is not configured", ex.Message);
    }

    [Fact]
    public void IsUnderRoot_SamePathAndChildren_ReturnTrue()
    {
        var root = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"omp-root-{Guid.NewGuid():N}"));

        Assert.True(DeploymentPath.IsUnderRoot(root, root));
        Assert.True(DeploymentPath.IsUnderRoot(root, Path.Join(root, "child")));
        Assert.True(DeploymentPath.IsUnderRoot(root, Path.Join(root, "child", "nested")));
    }

    [Fact]
    public void IsUnderRoot_SiblingOrParent_ReturnFalse()
    {
        var root = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"omp-root-{Guid.NewGuid():N}"));
        var sibling = Path.GetFullPath(Path.Join(Path.GetTempPath(), $"omp-sibling-{Guid.NewGuid():N}"));
        var parent = Path.GetFullPath(Path.GetTempPath());

        Assert.False(DeploymentPath.IsUnderRoot(root, sibling));
        Assert.False(DeploymentPath.IsUnderRoot(root, parent));
    }
}
