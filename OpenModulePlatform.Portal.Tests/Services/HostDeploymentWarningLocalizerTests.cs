using System.Globalization;
using Microsoft.Extensions.Localization;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class HostDeploymentWarningLocalizerTests
{
    [Fact]
    public void Localize_ReturnsNullOrWhitespaceUnchanged()
    {
        var localizer = new StubStringLocalizer();

        Assert.Null(HostDeploymentWarningLocalizer.Localize(null, localizer));
        Assert.Equal("   ", HostDeploymentWarningLocalizer.Localize("   ", localizer));
    }

    [Fact]
    public void Localize_KeepsPlainTextWarningAsIs()
    {
        var localizer = new StubStringLocalizer();
        const string raw = "Required config root section 'ConnectionStrings' is missing.";

        Assert.Equal(raw, HostDeploymentWarningLocalizer.Localize(raw, localizer));
    }

    [Fact]
    public void Localize_MapsNotUncPathCodeToLocalizedTemplate()
    {
        var localizer = new StubStringLocalizer();
        const string stored = @"{""code"":""OmpAuth.DataProtectionKeyPath.NotUncPath"",""params"":[""C:\\OMP\\Keys""]}";

        var result = HostDeploymentWarningLocalizer.Localize(stored, localizer);

        Assert.Equal(
            "OmpAuth:DataProtectionKeyPath 'C:\\OMP\\Keys' is not a UNC path. " +
            "A local key path will break auth-cookie sharing in load-balanced scenarios.",
            result);
    }

    [Fact]
    public void Localize_MapsMismatchCodeToLocalizedTemplate()
    {
        var localizer = new StubStringLocalizer();
        const string stored = @"{""code"":""OmpAuth.DataProtectionKeyPath.Mismatch"",""params"":[""D:\\App\\Keys"",""C:\\OMP\\Keys""]}";

        var result = HostDeploymentWarningLocalizer.Localize(stored, localizer);

        Assert.Equal(
            "OmpAuth:DataProtectionKeyPath is 'D:\\App\\Keys' but the HostAgent expects 'C:\\OMP\\Keys'. " +
            "Data protection keys must be shared across OMP web apps for auth-cookie compatibility.",
            result);
    }

    [Fact]
    public void Localize_MapsCookieNameAndApplicationNameCodes()
    {
        var localizer = new StubStringLocalizer();
        const string stored = @"{""code"":""OmpAuth.CookieName.UnexpectedValue"",""params"":["".Custom"","".OpenModulePlatform.Auth""]}"
            + "\n"
            + @"{""code"":""OmpAuth.ApplicationName.UnexpectedValue"",""params"":[""CustomApp"",""OpenModulePlatform""]}";

        var result = HostDeploymentWarningLocalizer.Localize(stored, localizer);

        Assert.Contains(
            "OmpAuth:CookieName is '.Custom' but the expected OMP default is '.OpenModulePlatform.Auth'.",
            result,
            StringComparison.Ordinal);
        Assert.Contains(
            "OmpAuth:ApplicationName is 'CustomApp' but the expected OMP default is 'OpenModulePlatform'.",
            result,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Localize_KeepsUnknownCodeAsRawFallback()
    {
        var localizer = new StubStringLocalizer();
        const string stored = @"{""code"":""OmpAuth.Future.Unknown"",""params"":[""x""]}";

        Assert.Equal(stored, HostDeploymentWarningLocalizer.Localize(stored, localizer));
    }

    [Fact]
    public void Localize_KeepsMalformedJsonAsRawFallback()
    {
        var localizer = new StubStringLocalizer();
        const string stored = @"{""code"":""OmpAuth.DataProtectionKeyPath.NotUncPath"",";

        Assert.Equal(stored, HostDeploymentWarningLocalizer.Localize(stored, localizer));
    }

    [Fact]
    public void Localize_HandlesMixedStructuredAndPlainLines()
    {
        var localizer = new StubStringLocalizer();
        const string stored = @"{""code"":""OmpAuth.DataProtectionKeyPath.NotUncPath"",""params"":[""C:\\OMP\\Keys""]}"
            + "\r\n"
            + "Deploy-set inconsistency in set 'default': versions differ.";

        var result = HostDeploymentWarningLocalizer.Localize(stored, localizer);

        Assert.Contains("is not a UNC path", result, StringComparison.Ordinal);
        Assert.Contains("Deploy-set inconsistency", result, StringComparison.Ordinal);
    }

    private sealed class StubStringLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name]
            => new(name, name);

        public LocalizedString this[string name, params object[] arguments]
            => new(name, string.Format(CultureInfo.InvariantCulture, name, arguments));

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
            => [];
    }
}
