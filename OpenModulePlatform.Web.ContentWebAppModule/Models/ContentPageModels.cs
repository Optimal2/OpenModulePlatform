// File: OpenModulePlatform.Web.ContentWebAppModule/Models/ContentPageModels.cs
namespace OpenModulePlatform.Web.ContentWebAppModule.Models;

public sealed class ContentPageListRow
{
    public Guid PageId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool IsPublished { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public int SortOrder { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class ContentPageEditRow
{
    public Guid PageId { get; set; }
    public Guid AppInstanceId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public string ContentFormat { get; set; } = ContentFormats.Markdown;
    public string Content { get; set; } = string.Empty;
    public bool IsPublished { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class ContentPageRenderRow
{
    public Guid PageId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public string ContentFormat { get; set; } = ContentFormats.Markdown;
    public string Content { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class ContentPageRevisionRow
{
    public Guid RevisionId { get; set; }
    public Guid PageId { get; set; }
    public int RevisionNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string ContentFormat { get; set; } = ContentFormats.Markdown;
    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public string? ChangeNote { get; set; }
}

public sealed class ContentPageSaveRequest
{
    public Guid PageId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }
    public string ContentFormat { get; set; } = ContentFormats.Markdown;
    public string Content { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string? ChangeNote { get; set; }
}

public static class ContentFormats
{
    public const string Markdown = "markdown";
    public const string Html = "html";

    public static string Normalize(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? Markdown
            : value.Trim().ToLowerInvariant();

        return normalized == Html ? Html : Markdown;
    }
}
