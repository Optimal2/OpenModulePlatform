using OpenModulePlatform.Web.Shared.Web;

namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// The one return-URL rule (B69): the login redirect, the culture switch, the logout
/// redirect, the OIDC post-sign-in redirect and notification destinations all resolve
/// through <see cref="OmpUrlSafety.IsSafeLocalReturnUrl"/>, so the bypass techniques the
/// earlier private copies were hardened against are pinned here once.
/// </summary>
public sealed class OmpUrlSafetyLocalReturnUrlTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/portal")]
    [InlineData("/portal/apps?tab=all&x=1")]
    [InlineData("/a%20b/c")]
    public void IsSafeLocalReturnUrl_AcceptsRootedSameOriginPaths(string returnUrl)
        => Assert.True(OmpUrlSafety.IsSafeLocalReturnUrl(returnUrl));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("portal")]
    [InlineData("https://evil.example/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    [InlineData("/portal\\..\\x")]
    [InlineData("%2F%2Fevil.example/")]
    [InlineData("/%2F%2Fevil.example/")]
    [InlineData("/%5Cevil.example/")]
    [InlineData("/%zz")]
    public void IsSafeLocalReturnUrl_RejectsOpenRedirectShapes(string? returnUrl)
        => Assert.False(OmpUrlSafety.IsSafeLocalReturnUrl(returnUrl));

    [Theory]
    [InlineData("/")]
    [InlineData("/portal/notifications")]
    public void IsSafeLocalDestination_AcceptsRootedPaths(string destination)
        => Assert.True(OmpUrlSafety.IsSafeLocalDestination(destination));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    [InlineData("/a\\b")]
    public void IsSafeLocalDestination_RejectsProtocolRelativeAndBackslashes(string? destination)
        => Assert.False(OmpUrlSafety.IsSafeLocalDestination(destination));

    [Fact]
    public void IsSafeLocalReturnUrl_IsStricterThanIsSafeLocalDestination()
    {
        // "/%2F%2Fevil" passes the raw destination check (it is a rooted path) but decodes to
        // "///evil"; only the return-URL rule re-checks after decoding.
        const string encoded = "/%2F%2Fevil.example/";
        Assert.True(OmpUrlSafety.IsSafeLocalDestination(encoded));
        Assert.False(OmpUrlSafety.IsSafeLocalReturnUrl(encoded));
    }
}
