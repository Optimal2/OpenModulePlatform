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
            ? FrameSourceDirectiveRegex().Replace(policy, frameSourceDirective)
            : policy.Trim().TrimEnd(';') + "; " + frameSourceDirective;
    }

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
                Policy = ReplaceFrameSource(cspOptions.Policy ?? OmpContentSecurityPolicy.Baseline, directive),
                ReportPath = cspOptions.ReportPath
            };

            context.Response.Headers[cspOptions.ReportOnly
                ? "Content-Security-Policy-Report-Only"
                : "Content-Security-Policy"] = OmpContentSecurityPolicy.Build(rewritten, context.Request.PathBase);

            await next();
        });
    }

    [GeneratedRegex(@"frame-src\s+[^;]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FrameSourceDirectiveRegex();
}
