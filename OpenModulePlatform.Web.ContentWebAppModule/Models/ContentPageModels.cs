// File: OpenModulePlatform.Web.ContentWebAppModule/Models/ContentPageModels.cs
namespace OpenModulePlatform.Web.ContentWebAppModule.Models;

public sealed class ContentPageListRow
{
    public Guid ContentId { get; set; }

    public string Slug { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string ContentType { get; set; } = ContentTypes.Markdown;

    public bool IsEnabled { get; set; }

    public int? SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }

    public string? AccessSummary { get; set; }
}

public sealed class ContentPageEditRow
{
    public Guid ContentId { get; set; }

    public Guid AppInstanceId { get; set; }

    public string Slug { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string ContentType { get; set; } = ContentTypes.Markdown;

    public string Body { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public int? SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }
}

public sealed class ContentPageRenderRow
{
    public Guid ContentId { get; set; }

    public string Slug { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string ContentType { get; set; } = ContentTypes.Markdown;

    public string Body { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class ContentRoleAccessRow
{
    public int RoleId { get; set; }

    public string RoleName { get; set; } = string.Empty;

    public bool CanRead { get; set; }

    public bool CanWrite { get; set; }
}

public sealed class ContentRoleAccessSaveRow
{
    public int RoleId { get; set; }

    public bool CanRead { get; set; }

    public bool CanWrite { get; set; }
}

public sealed class ContentPageSaveRequest
{
    public Guid ContentId { get; set; }

    public string Slug { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string ContentType { get; set; } = ContentTypes.Markdown;

    public string Body { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public int? SortOrder { get; set; }

    public IReadOnlyList<ContentRoleAccessSaveRow> RoleAccesses { get; set; } = [];

    public bool SaveRoleAccess { get; set; }
}

public static class ContentTypes
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
