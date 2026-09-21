using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Security;

public sealed class AuthLoginPageTests
{
    [Fact]
    public void LoginPage_MakesOidcPrimaryOnlyWhenEnabled()
    {
        var page = OmpRepositoryFiles.ReadRepositoryTextFile("OpenModulePlatform.Auth", "Pages", "Login.cshtml");
        var primaryActions = ExtractBetween(
            page,
            """<div class="primary-actions">""",
            """</div>""");
        var otherOptions = ExtractBetween(
            page,
            """<div class="auth-options-list" id="other-signin-options-list">""",
            """<button""");

        Assert.Contains("@if (Model.OidcEnabled)", primaryActions);
        Assert.Contains("href=\"@Model.OidcLoginUrl\"", primaryActions);
        Assert.Contains("@string.Format(L[\"Sign in with {0}\"].Value, Model.OidcDisplayName)", primaryActions);
        Assert.Contains("else", primaryActions);
        Assert.Contains("href=\"@Model.WindowsLoginUrl\"", primaryActions);
        Assert.DoesNotContain("href=\"@Model.OidcLoginUrl\"", otherOptions);
        Assert.Contains("@if (Model.OidcEnabled)", otherOptions);
        Assert.Contains("href=\"@Model.WindowsLoginUrl\"", otherOptions);
    }

    private static string ExtractBetween(string value, string start, string end)
    {
        var startIndex = value.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Could not find start marker '{start}'.");

        startIndex += start.Length;
        var endIndex = value.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex >= 0, $"Could not find end marker '{end}'.");

        return value[startIndex..endIndex];
    }
}
