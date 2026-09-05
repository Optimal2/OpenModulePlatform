// File: OpenModulePlatform.Web.iFrameWebAppModule/Security/IFrameFrameSourcePolicy.cs
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.Web.iFrameWebAppModule.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Web;

namespace OpenModulePlatform.Web.iFrameWebAppModule.Security;

/// <summary>
/// Replaces the iFrame module's static <c>frame-src 'self' https: http:</c> wildcard
/// with an allowlist of the exact origins of the enabled, administrator-configured
/// URLs in omp_iframe.urls (security review follow-up, campaign
/// csp-vagen-till-enforcement). Frame targets are runtime data, so the directive is
/// computed per request from the database rows — cached briefly — instead of naming
/// whole schemes.
/// </summary>
public static partial class IFrameFrameSourcePolicy
{
    /// <summary>
    /// The module's own tightened policy — the shared
    /// <see cref="OmpContentSecurityPolicy.Baseline"/> minus
    /// <c>script-src 'unsafe-inline'</c>. Used when the configured
    /// <c>Portal:SecurityHeaders:ContentSecurityPolicy:Policy</c> key is missing, so
    /// a lost key cannot silently re-open inline script execution (the shared baseline
    /// still carries the exception for other apps). Must stay byte-identical to the
    /// shipped appsettings.json Policy value; IFrameModuleInlineScriptGuardTests pins
    /// both sides.
    /// </summary>
    public const string ModulePolicyBaseline =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self' ws: wss:; " +
        "media-src 'self'; " +
        "worker-src 'none'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-src 'self'; " +
        "frame-ancestors 'self'";

    /// <summary>
    /// Reduces configured URLs to the frame-src directive: <c>frame-src 'self'</c>
    /// plus each distinct, scheme-validated origin. Relative rows are same-origin and
    /// already covered by 'self'; rows failing <see cref="OmpUrlSafety"/> contribute
    /// nothing (the read paths refuse to emit them too).
    /// </summary>
    public static string BuildFrameSourceDirective(IEnumerable<string> configuredUrls)
    {
        var origins = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var value in configuredUrls)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
                && OmpUrlSafety.IsAllowedAbsoluteScheme(uri))
            {
                origins.Add(uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant());
            }
        }

        var directive = new StringBuilder("frame-src 'self'");
        foreach (var origin in origins)
        {
            directive.Append(' ');
            directive.Append(origin);
        }

        return directive.ToString();
    }

    /// <summary>
    /// Swaps the policy's frame-src directive for <paramref name="frameSourceDirective"/>.
    /// The configured policy is expected to carry a frame-src directive; when it does
    /// not, the directive is appended rather than silently dropped.
    /// </summary>
    public static string ReplaceFrameSource(string policy, string frameSourceDirective)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            return frameSourceDirective;
        }

        return FrameSourceDirectiveRegex().IsMatch(policy)
            ? FrameSourceDirectiveRegex().Replace(policy, match =>
                match.Groups[1].Value.Length == 0
                    ? frameSourceDirective
                    : match.Groups[1].Value + " " + frameSourceDirective)
            : policy.Trim().TrimEnd(';') + "; " + frameSourceDirective;
    }

    /// <summary>
    /// The policy the middleware rewrites: the configured one, or the module's tightened
    /// baseline when the key is missing OR blank. Null and blank must be treated alike —
    /// with a null-only fallback an empty Policy key reached ReplaceFrameSource, whose
    /// blank-policy shortcut returned the frame-src directive alone (no script-src, no
    /// default-src: inline scripts allowed again) while the warning above claimed the
    /// tightened policy was in use (second-opinion finding, 2026-09-05).
    /// </summary>
    public static string ResolveConfiguredPolicy(string? configuredPolicy)
        => string.IsNullOrWhiteSpace(configuredPolicy) ? ModulePolicyBaseline : configuredPolicy;

    /// <summary>
    /// Sets the module's CSP header (report-only or enforcing, following the configured
    /// options) with the DB-derived frame-src allowlist, before the shared security
    /// headers middleware runs — its set-if-missing pattern then keeps this value.
    /// Register before UseOmpWebDefaults.
    /// </summary>
    public static IApplicationBuilder UseIFrameFrameSourceCsp(
        this IApplicationBuilder app,
        string optionsSectionName = "Portal")
    {
        var cspOptions = app.ApplicationServices
            .GetService<IConfiguration>()?
            .GetSection($"{optionsSectionName}:SecurityHeaders:ContentSecurityPolicy")
            .Get<ContentSecurityPolicyOptions>() ?? new ContentSecurityPolicyOptions();
        var logger = app.ApplicationServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("OpenModulePlatform.Web.iFrameWebAppModule.Security.IFrameFrameSourcePolicy");

        // A missing Policy key must not silently restore the shared baseline's
        // script-src 'unsafe-inline' (review follow-up, campaign
        // iframe-csp-grinden-ar-inte-deterministisk): warn loudly and fall back to
        // the module's own tightened policy, which keeps the hardening.
        if (string.IsNullOrWhiteSpace(cspOptions.Policy))
        {
            logger.LogWarning(
                "The {Section}:SecurityHeaders:ContentSecurityPolicy:Policy key is missing; falling back to the module's built-in tightened policy (no script-src 'unsafe-inline').",
                optionsSectionName);
        }

        // Short-lived process-local cache: the allowlist changes when an administrator
        // edits URLs in the Portal, and a minute of staleness there is acceptable, while
        // a database round trip per response is not.
        var cacheLock = new object();
        string? cachedDirective = null;
        DateTimeOffset cacheExpiresAt = DateTimeOffset.MinValue;
        var cacheLifetime = TimeSpan.FromSeconds(60);

        return app.Use(async (context, next) =>
        {
            if (!cspOptions.Enabled)
            {
                await next();
                return;
            }

            var directive = cachedDirective;
            if (directive is null || DateTimeOffset.UtcNow >= cacheExpiresAt)
            {
                try
                {
                    var repo = context.RequestServices.GetRequiredService<IFrameWebAppModuleRepository>();
                    var urls = await repo.GetEnabledUrlsAsync(context.RequestAborted);
                    directive = BuildFrameSourceDirective(urls);
                    lock (cacheLock)
                    {
                        cachedDirective = directive;
                        cacheExpiresAt = DateTimeOffset.UtcNow.Add(cacheLifetime);
                    }
                }
                catch (Exception ex)
                {
                    // A database hiccup must not take down every page: fall back to the
                    // tightest directive ('self' only) for this request and retry next time.
                    logger.LogWarning(ex, "Could not load the iFrame frame-src allowlist; falling back to frame-src 'self'.");
                    directive ??= "frame-src 'self'";
                }
            }

            var rewritten = new ContentSecurityPolicyOptions
            {
                Enabled = cspOptions.Enabled,
                ReportOnly = cspOptions.ReportOnly,
                Policy = ReplaceFrameSource(ResolveConfiguredPolicy(cspOptions.Policy), directive),
                ReportPath = cspOptions.ReportPath
            };

            context.Response.Headers[cspOptions.ReportOnly
                ? "Content-Security-Policy-Report-Only"
                : "Content-Security-Policy"] = OmpContentSecurityPolicy.Build(rewritten, context.Request.PathBase);

            await next();
        });
    }

    // The match must start at a directive boundary — the start of the policy or just
    // after a ';' — captured in group 1 so the replacement can re-emit it. Anchoring
    // only the character before the name (the old (?<![-\w]) lookbehind) still let the
    // match fire inside another directive's VALUE (e.g. an administrator-configured
    // "script-src https://cdn.example/frame-src/libs/"), and [^;]* would then overwrite
    // the rest of that directive. The (?=[\s;]|$) lookahead keeps "frame-src" a complete
    // directive name and lets an empty value ("frame-src;") match so it is replaced
    // instead of duplicated by the append fallback.
    [GeneratedRegex(@"(^|;)\s*frame-src(?=[\s;]|$)[^;]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FrameSourceDirectiveRegex();
}
