// File: OpenModulePlatform.Web.ContentWebAppModule/Models/ServerReportModels.cs
namespace OpenModulePlatform.Web.ContentWebAppModule.Models;

public sealed class ServerReportDefinition
{
    public string Title { get; set; } = string.Empty;

    public List<ServerReportQueryDefinition> Queries { get; set; } = [];
}

public sealed class ServerReportQueryDefinition
{
    public string Name { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Sql { get; set; } = string.Empty;

    public string Renderer { get; set; } = "table";

    public int? MaxRows { get; set; }
}

public sealed class ServerReportResult
{
    public string Title { get; set; } = string.Empty;

    public IReadOnlyList<ServerReportQueryResult> Queries { get; set; } = [];
}

public sealed class ServerReportQueryResult
{
    public string Name { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public IReadOnlyList<string> Columns { get; set; } = [];

    public IReadOnlyList<IReadOnlyList<string?>> Rows { get; set; } = [];

    public bool IsTruncated { get; set; }

    public int MaxRows { get; set; }

    public string? ErrorMessage { get; set; }
}
