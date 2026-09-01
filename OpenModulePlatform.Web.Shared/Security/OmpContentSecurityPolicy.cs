// File: OpenModulePlatform.Web.Shared/Security/OmpContentSecurityPolicy.cs
using Microsoft.AspNetCore.Http;
using OpenModulePlatform.Web.Shared.Options;

namespace OpenModulePlatform.Web.Shared.Security;

/// <summary>
/// The platform Content-Security-Policy baseline and per-request assembly.
/// </summary>
/// <remarks>
/// <para>
/// The baseline is the strictest policy that works unmodified for every OMP
/// web surface today. It is deliberately report-only by default
/// (<see cref="ContentSecurityPolicyOptions.ReportOnly"/>); an app switches to
/// enforcement by configuration once its violation log is clean.
/// </para>
/// <para>
/// The two <c>unsafe-inline</c> entries are not blanket permissions; each has
/// a documented, file-referenced reason in docs/CONTENT_SECURITY_POLICY.md:
/// inline scripts are legacy Razor blocks being migrated away (Portal carries
/// most of them), and inline styles are webamp's runtime-injected skin
/// stylesheet plus dynamic Razor style attributes. <c>unsafe-eval</c> is never
/// required anywhere in the platform.
/// </para>
/// </remarks>
public static class OmpContentSecurityPolicy
{
    /// <summary>
    /// The app-relative path that receives CSP violation reports. Mapped by
    /// UseOmpWebDefaults and by the Auth app's hand-rolled pipeline; reports
    /// are logged as warnings under the
    /// <c>OpenModulePlatform.Web.Shared.Security.CspReport</c> category.
    /// </summary>
    public const string ReportPath = "/omp/csp-report";

    /// <summary>
    /// The shared baseline policy. ws:/wss: in connect-src covers same-origin
    /// SignalR WebSockets in browsers that do not treat 'self' as matching
    /// WebSocket schemes (Safari); script/style execution stays gated by the
    /// other directives.
    /// </summary>
    public const string Baseline =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
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
    /// Builds the header value for a request: the configured policy (or the
    /// baseline) with a path-base-aware report-uri appended when a report path
    /// is configured.
    /// </summary>
    public static string Build(ContentSecurityPolicyOptions options, PathString pathBase)
    {
        var policy = string.IsNullOrWhiteSpace(options.Policy)
            ? Baseline
            : options.Policy.Trim().TrimEnd(';');

        if (string.IsNullOrWhiteSpace(options.ReportPath))
        {
            return policy;
        }

        var reportUri = pathBase.HasValue
            ? string.Concat(pathBase.Value, options.ReportPath)
            : options.ReportPath;

        return string.Concat(policy, "; report-uri ", reportUri);
    }
}
