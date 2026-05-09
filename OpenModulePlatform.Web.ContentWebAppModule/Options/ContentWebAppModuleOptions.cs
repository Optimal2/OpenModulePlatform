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
}
