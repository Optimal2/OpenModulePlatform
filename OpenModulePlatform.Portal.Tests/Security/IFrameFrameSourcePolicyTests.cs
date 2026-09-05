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

    [Fact]
    public void ReplaceFrameSource_DoesNotRewriteFrameSourceTextInsideAnotherDirectiveValue()
    {
        // Anchoring guard (campaign iframe-csp-grinden-ar-inte-deterministisk): the
        // match must start at a directive boundary. The old lookbehind only protected
        // the character before the name, so the "frame-src" text inside another
        // directive's value matched and [^;]* overwrote the rest of that directive.
        const string policy =
            "default-src 'self'; script-src https://cdn.example/frame-src/libs/; frame-ancestors 'self'";

        var rewritten = IFrameFrameSourcePolicy.ReplaceFrameSource(
            policy,
            "frame-src 'self' https://reports.example.internal");

        Assert.Equal(
            "default-src 'self'; script-src https://cdn.example/frame-src/libs/; frame-ancestors 'self'; frame-src 'self' https://reports.example.internal",
            rewritten);
    }

    [Fact]
    public void ReplaceFrameSource_ReplacesEmptyValueDirectiveInsteadOfAppending()
    {
        // "frame-src;" with an empty value matched nothing under the old
        // "frame-src\s+[^;]*" (\s+ requires whitespace), so a second frame-src was
        // appended instead of replacing the empty one.
        const string policy =
            "default-src 'self'; frame-src; frame-ancestors 'self'";

        var rewritten = IFrameFrameSourcePolicy.ReplaceFrameSource(
            policy,
            "frame-src 'self' https://reports.example.internal");

        Assert.Equal(
            "default-src 'self'; frame-src 'self' https://reports.example.internal; frame-ancestors 'self'",
            rewritten);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveConfiguredPolicy_MissingOrBlank_FallsBackToTightenedBaseline(string? configured)
    {
        // Second-opinion finding (2026-09-05): the warning fired on IsNullOrWhiteSpace while the
        // fallback only covered null, so an EMPTY Policy key produced a header consisting of the
        // frame-src directive alone — no script-src, no default-src — i.e. inline scripts allowed
        // again, under a log line claiming the tightened policy was in use.
        var resolved = IFrameFrameSourcePolicy.ResolveConfiguredPolicy(configured);

        Assert.Equal(IFrameFrameSourcePolicy.ModulePolicyBaseline, resolved);
        Assert.Contains("script-src 'self';", resolved);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", resolved);
    }

    [Fact]
    public void ResolveConfiguredPolicy_Configured_IsKeptVerbatim()
    {
        const string policy = "default-src 'self'; script-src 'self'; frame-src 'self'";

        Assert.Same(policy, IFrameFrameSourcePolicy.ResolveConfiguredPolicy(policy));
    }
}
