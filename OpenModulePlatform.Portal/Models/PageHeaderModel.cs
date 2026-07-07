// File: OpenModulePlatform.Portal/Models/PageHeaderModel.cs
namespace OpenModulePlatform.Portal.Models;

/// <summary>
/// Model for the shared page header partial with an optional title info badge.
/// </summary>
public sealed class PageHeaderModel
{
    public string Eyebrow { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Optional help text shown as a small "i" badge next to the title.
    /// </summary>
    public string Info { get; set; } = string.Empty;

    /// <summary>
    /// Optional extra CSS class appended to the page-header section.
    /// </summary>
    public string CssClass { get; set; } = string.Empty;
}
