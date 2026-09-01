// File: OpenModulePlatform.Web.Shared/Options/SecurityHeadersOptions.cs
namespace OpenModulePlatform.Web.Shared.Options;

/// <summary>
/// Configuration for the platform Content-Security-Policy, bound from
/// <c>{optionsSectionName}:SecurityHeaders:ContentSecurityPolicy</c>.
/// See docs/CONTENT_SECURITY_POLICY.md for the rollout model and the
/// documented per-app exceptions.
/// </summary>
public sealed class ContentSecurityPolicyOptions
{
    /// <summary>
    /// Gets or sets whether the CSP header is emitted at all. Default true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the policy is emitted as
    /// <c>Content-Security-Policy-Report-Only</c> (true, the rollout default)
    /// or as the enforcing <c>Content-Security-Policy</c> (false). An app
    /// switches to enforcement by setting this to false once its report log
    /// is clean.
    /// </summary>
    public bool ReportOnly { get; set; } = true;

    /// <summary>
    /// Gets or sets a full replacement policy. When null or empty the shared
    /// baseline (<see cref="Security.OmpContentSecurityPolicy.Baseline"/>) is
    /// used. An app that needs extra sources (for example the Portal's webamp
    /// widget or the Content module's CDN-hosted editor) sets the complete
    /// policy here; a comment in the app's appsettings.json must state which
    /// code forces each added source.
    /// </summary>
    public string? Policy { get; set; }

    /// <summary>
    /// Gets or sets the app-relative path browsers POST violation reports to.
    /// Default <see cref="Security.OmpContentSecurityPolicy.ReportPath"/>;
    /// set to an empty string to omit the report-uri directive. The path is
    /// combined with the request's PathBase at emission time.
    /// </summary>
    public string ReportPath { get; set; } = Security.OmpContentSecurityPolicy.ReportPath;
}
