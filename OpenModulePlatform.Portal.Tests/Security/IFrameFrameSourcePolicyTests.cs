// File: OpenModulePlatform.Portal.Tests/Security/IFrameFrameSourcePolicyTests.cs
using OpenModulePlatform.Web.iFrameWebAppModule.Security;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// Pins the iFrame module's frame-src allowlist (campaign
/// csp-vagen-till-enforcement): the old 'https: http:' scheme wildcards are
/// replaced by the exact origins of the enabled configured URLs.
/// </summary>
public sealed class IFrameFrameSourcePolicyTests
{
    [Fact]
    public void BuildFrameSourceDirective_ReducesUrlsToDistinctOrigins()
    {
        var directive = IFrameFrameSourcePolicy.BuildFrameSourceDirective(
        [
            "https://Reports.example.internal/app/view?id=1",
            "https://reports.example.internal/app/other",
            "https://grafana.example.internal:3000/d/abc",
            "http://legacy.intranet/page",
            "/relative/same-origin",
            "javascript:alert(1)",
            "not a url at all",
            ""
        ]);

        Assert.Equal(
            "frame-src 'self' http://legacy.intranet https://grafana.example.internal:3000 https://reports.example.internal",
            directive);
    }

    [Fact]
    public void BuildFrameSourceDirective_EmptyInput_KeepsSelfOnly()
    {
        Assert.Equal("frame-src 'self'", IFrameFrameSourcePolicy.BuildFrameSourceDirective([]));
    }

    [Fact]
    public void ReplaceFrameSource_ReplacesWildcardDirective()
    {
        const string policy =
            "default-src 'self'; frame-src 'self' https: http:; frame-ancestors 'self'";

        var rewritten = IFrameFrameSourcePolicy.ReplaceFrameSource(
            policy,
            "frame-src 'self' https://reports.example.internal");

        Assert.Equal(
            "default-src 'self'; frame-src 'self' https://reports.example.internal; frame-ancestors 'self'",
            rewritten);
    }

    [Fact]
    public void ReplaceFrameSource_AppendsWhenDirectiveMissing()
    {
        var rewritten = IFrameFrameSourcePolicy.ReplaceFrameSource(
            "default-src 'self'",
            "frame-src 'self' https://reports.example.internal");

        Assert.Equal("default-src 'self'; frame-src 'self' https://reports.example.internal", rewritten);
    }

    [Fact]
    public void ReplaceFrameSource_BaselineGetsSelfOnlyAllowlist()
    {
        var rewritten = IFrameFrameSourcePolicy.ReplaceFrameSource(
            OmpContentSecurityPolicy.Baseline,
            IFrameFrameSourcePolicy.BuildFrameSourceDirective(["https://a.example"]));

        Assert.Contains("frame-src 'self' https://a.example;", rewritten);
        Assert.DoesNotContain("https: http:", rewritten);
    }

    [Fact]
    public void ReplaceFrameSource_DoesNotCorruptChildFrameSourceDirective()
    {
        // Regression guard (campaign csp-sista-undantagen): the frame-src match
        // must not fire inside a "child-frame-src" directive. Without the
        // lookbehind, the "frame-src ..." tail of "child-frame-src ..." is
        // rewritten and the child directive is corrupted.
        const string policy =
            "default-src 'self'; child-frame-src https://child.example; frame-ancestors 'self'";

        var rewritten = IFrameFrameSourcePolicy.ReplaceFrameSource(
            policy,
            "frame-src 'self' https://reports.example.internal");

        Assert.Equal(
            "default-src 'self'; child-frame-src https://child.example; frame-ancestors 'self'; frame-src 'self' https://reports.example.internal",
            rewritten);
    }

    [Fact]
    public void ReplaceFrameSource_ReplacesRealDirectiveWhenChildFrameSourcePresent()
    {
        // Same guard, with both directives present: child-frame-src stays
        // byte-identical, the real frame-src is the one replaced.
        const string policy =
            "default-src 'self'; child-frame-src https://child.example; frame-src 'self' https: http:; frame-ancestors 'self'";

        var rewritten = IFrameFrameSourcePolicy.ReplaceFrameSource(
            policy,
            "frame-src 'self' https://reports.example.internal");

        Assert.Equal(
            "default-src 'self'; child-frame-src https://child.example; frame-src 'self' https://reports.example.internal; frame-ancestors 'self'",
            rewritten);
    }
}
