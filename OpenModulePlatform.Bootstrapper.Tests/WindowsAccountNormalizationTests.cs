using Xunit;

namespace OpenModulePlatform.Bootstrapper.Tests;

public sealed class WindowsAccountNormalizationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeWindowsAccount_PreservesEmptyAccountAsDefaultServiceIdentity(string value)
    {
        Assert.Equal(string.Empty, Program.NormalizeWindowsAccount(value));
    }
}
