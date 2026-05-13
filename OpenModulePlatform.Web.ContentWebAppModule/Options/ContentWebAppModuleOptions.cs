// File: OpenModulePlatform.Web.ContentWebAppModule/Options/ContentWebAppModuleOptions.cs
namespace OpenModulePlatform.Web.ContentWebAppModule.Options;

public sealed class ContentWebAppModuleOptions
{
    public const string SectionName = "ContentWebAppModule";

    /// <summary>
    /// OMP AppInstanceId that owns the content tree rendered by this IIS application.
    /// </summary>
    public Guid AppInstanceId { get; set; }

    /// <summary>
    /// Optional fallback slug used when the root page does not exist.
    /// </summary>
    public string HomeSlug { get; set; } = "home";

    /// <summary>
    /// Whitelisted server-side directory for JSON server report definitions.
    /// Relative paths are resolved from the web app content root.
    /// </summary>
    public string ServerReportsPath { get; set; } = "App_Data/ContentReports";

    public int ServerReportDefaultMaxRows { get; set; } = 100;

    public int ServerReportMaxRowsLimit { get; set; } = 1000;

    public int ServerReportQueryTimeoutSeconds { get; set; } = 30;
}
