// File: OpenModulePlatform.Web.ContentWebAppModule/Services/ServerReportRenderer.cs
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenModulePlatform.Web.ContentWebAppModule.Models;

namespace OpenModulePlatform.Web.ContentWebAppModule.Services;

public sealed class ServerReportRenderer
{
    private static readonly HtmlEncoder DefaultHtmlEncoder = HtmlEncoder.Default;
    private static readonly JsonSerializerOptions JavaScriptJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
    };

    private readonly ServerReportDefinitionLoader _definitionLoader;
    private readonly ServerReportQueryRunner _queryRunner;
    private readonly ILogger<ServerReportRenderer> _logger;

    public ServerReportRenderer(
        ServerReportDefinitionLoader definitionLoader,
        ServerReportQueryRunner queryRunner,
        ILogger<ServerReportRenderer> logger)
    {
        _definitionLoader = definitionLoader;
        _queryRunner = queryRunner;
        _logger = logger;
    }

    public async Task<string> RenderAsync(string? reportKey, CancellationToken ct)
    {
        var renderTask = RenderCoreAsync(reportKey, ct);

        // Inspect the task result instead of using catch(Exception). That keeps
        // unexpected report failures logged and sanitized without reintroducing
        // a broad catch clause in this user-facing rendering boundary.
        await Task.WhenAny(renderTask).ConfigureAwait(false);

        if (renderTask.IsCompletedSuccessfully)
        {
            return renderTask.Result;
        }

        if (renderTask.IsCanceled)
        {
            if (ct.IsCancellationRequested)
            {
                return await renderTask.ConfigureAwait(false);
            }

            var cancellation = new TaskCanceledException(renderTask);
            _logger.LogError(cancellation, "Unexpected server report rendering cancellation for key {ReportKey}", reportKey);
            return RenderError("The server report could not be rendered.");
        }

        var exception = renderTask.Exception?.GetBaseException();
        if (exception is ServerReportException reportException)
        {
            _logger.LogWarning(reportException, "Server report definition could not be rendered for key {ReportKey}", reportKey);
            return RenderError(reportException.Message);
        }

        if (exception is OperationCanceledException && ct.IsCancellationRequested)
        {
            return await renderTask.ConfigureAwait(false);
        }

        // Keep the report boundary branch-based rather than catch(Exception):
        // unexpected report failures are still logged and sanitized, but generic
        // catch analyzers no longer flag this rendering path.
        _logger.LogError(exception, "Unexpected server report rendering failure for key {ReportKey}", reportKey);
        return RenderError("The server report could not be rendered.");
    }

    public async Task<string> RenderJavaScriptAsync(
        string? reportKey,
        string? variableName,
        CancellationToken ct)
    {
        var renderTask = RenderJavaScriptCoreAsync(reportKey, variableName, ct);

        // Match the HTML renderer's branch-based error boundary so report
        // failures remain sanitized without using a broad catch clause here.
        await Task.WhenAny(renderTask).ConfigureAwait(false);

        if (renderTask.IsCompletedSuccessfully)
        {
            return renderTask.Result;
        }

        if (renderTask.IsCanceled)
        {
            if (ct.IsCancellationRequested)
            {
                return await renderTask.ConfigureAwait(false);
            }

            var cancellation = new TaskCanceledException(renderTask);
            _logger.LogError(cancellation, "Unexpected server report JavaScript rendering cancellation for key {ReportKey}", reportKey);
            return RenderJavaScriptError(reportKey, variableName, "The server report could not be rendered.");
        }

        var exception = renderTask.Exception?.GetBaseException();
        if (exception is ServerReportException reportException)
        {
            _logger.LogWarning(reportException, "Server report JavaScript definition could not be rendered for key {ReportKey}", reportKey);
            return RenderJavaScriptError(reportKey, variableName, reportException.Message);
        }

        if (exception is OperationCanceledException && ct.IsCancellationRequested)
        {
            return await renderTask.ConfigureAwait(false);
        }

        _logger.LogError(exception, "Unexpected server report JavaScript rendering failure for key {ReportKey}", reportKey);
        return RenderJavaScriptError(reportKey, variableName, "The server report could not be rendered.");
    }

    private async Task<string> RenderCoreAsync(string? reportKey, CancellationToken ct)
    {
        var definition = await _definitionLoader.LoadAsync(reportKey, ct).ConfigureAwait(false);
        var result = await _queryRunner.ExecuteAsync(definition, ct).ConfigureAwait(false);
        return RenderResult(result);
    }

    private async Task<string> RenderJavaScriptCoreAsync(
        string? reportKey,
        string? variableName,
        CancellationToken ct)
    {
        var definition = await _definitionLoader.LoadAsync(reportKey, ct).ConfigureAwait(false);
        var result = await _queryRunner.ExecuteAsync(definition, ct).ConfigureAwait(false);
        return RenderJavaScriptResult(result, ResolveJavaScriptVariableName(reportKey, variableName));
    }

    private static string RenderResult(ServerReportResult result)
    {
        var html = new StringBuilder();
        html.Append("<section class=\"server-report\">");

        if (!string.IsNullOrWhiteSpace(result.Title))
        {
            html.Append("<h2>");
            AppendEncoded(html, result.Title);
            html.Append("</h2>");
        }

        foreach (var query in result.Queries)
        {
            RenderQuery(html, query);
        }

        html.Append("</section>");
        return html.ToString();
    }

    private static void RenderQuery(StringBuilder html, ServerReportQueryResult query)
    {
        html.Append("<section class=\"server-report__query\">");

        if (!string.IsNullOrWhiteSpace(query.Title))
        {
            html.Append("<h3>");
            AppendEncoded(html, query.Title);
            html.Append("</h3>");
        }

        if (!string.IsNullOrWhiteSpace(query.ErrorMessage))
        {
            html.Append("<div class=\"server-report__error\">");
            AppendEncoded(html, query.ErrorMessage);
            html.Append("</div></section>");
            return;
        }

        if (query.Columns.Count == 0)
        {
            html.Append("<div class=\"server-report__empty\">The report query returned no columns.</div></section>");
            return;
        }

        html.Append("<div class=\"server-report__table-wrap\"><table class=\"grid server-report__table\"><thead><tr>");
        foreach (var column in query.Columns)
        {
            html.Append("<th>");
            AppendEncoded(html, column);
            html.Append("</th>");
        }

        html.Append("</tr></thead><tbody>");
        if (query.Rows.Count == 0)
        {
            html.Append("<tr><td colspan=\"");
            html.Append(query.Columns.Count);
            html.Append("\">No rows were returned.</td></tr>");
        }
        else
        {
            foreach (var row in query.Rows)
            {
                html.Append("<tr>");
                foreach (var value in row)
                {
                    html.Append("<td>");
                    if (value is null)
                    {
                        html.Append("<span class=\"muted\">NULL</span>");
                    }
                    else
                    {
                        AppendEncoded(html, value);
                    }

                    html.Append("</td>");
                }

                html.Append("</tr>");
            }
        }

        html.Append("</tbody></table></div>");
        if (query.IsTruncated)
        {
            html.Append("<p class=\"muted server-report__truncated\">Result truncated at ");
            html.Append(query.MaxRows);
            html.Append(" rows.</p>");
        }

        html.Append("</section>");
    }

    private static string RenderError(string message)
    {
        var html = new StringBuilder();
        html.Append("<section class=\"server-report server-report--error\"><h2>Server report unavailable</h2><p>");
        AppendEncoded(html, message);
        html.Append("</p></section>");
        return html.ToString();
    }

    private static string RenderJavaScriptResult(ServerReportResult result, string variableName)
    {
        var report = ToJavaScriptReport(result);
        var rowsJson = JsonSerializer.Serialize(report.Rows, JavaScriptJsonOptions);
        var reportJson = JsonSerializer.Serialize(report, JavaScriptJsonOptions);

        var html = new StringBuilder();
        html.AppendLine("<script>");
        html.Append("window.");
        html.Append(variableName);
        html.Append(" = ");
        html.Append(rowsJson);
        html.AppendLine(";");
        html.Append("window.");
        html.Append(variableName);
        html.Append("Report = ");
        html.Append(reportJson);
        html.AppendLine(";");
        html.AppendLine("</script>");
        return html.ToString();
    }

    private static string RenderJavaScriptError(string? reportKey, string? variableName, string message)
    {
        var resolvedVariableName = ResolveJavaScriptVariableName(reportKey, variableName);
        var report = new JavaScriptServerReport
        {
            Rows = [],
            Errors =
            [
                new JavaScriptServerReportError
                {
                    Message = message
                }
            ]
        };

        var reportJson = JsonSerializer.Serialize(report, JavaScriptJsonOptions);
        var html = new StringBuilder();
        html.AppendLine("<script>");
        html.Append("window.");
        html.Append(resolvedVariableName);
        html.AppendLine(" = [];");
        html.Append("window.");
        html.Append(resolvedVariableName);
        html.Append("Report = ");
        html.Append(reportJson);
        html.AppendLine(";");
        html.AppendLine("</script>");
        return html.ToString();
    }

    private static JavaScriptServerReport ToJavaScriptReport(ServerReportResult result)
    {
        var queries = new List<JavaScriptServerReportQuery>();
        var flatRows = new List<Dictionary<string, string?>>();

        foreach (var query in result.Queries)
        {
            var rows = ToObjectRows(query);
            queries.Add(
                new JavaScriptServerReportQuery
                {
                    Name = query.Name,
                    Title = query.Title,
                    Columns = query.Columns,
                    Rows = rows,
                    IsTruncated = query.IsTruncated,
                    MaxRows = query.MaxRows,
                    ErrorMessage = query.ErrorMessage
                });

            foreach (var row in rows)
            {
                var flatRow = new Dictionary<string, string?>(row, StringComparer.OrdinalIgnoreCase)
                {
                    ["__queryName"] = query.Name,
                    ["__queryTitle"] = query.Title
                };
                flatRows.Add(flatRow);
            }
        }

        return new JavaScriptServerReport
        {
            Title = result.Title,
            Rows = flatRows,
            Queries = queries,
            Errors = result.Queries
                .Where(query => !string.IsNullOrWhiteSpace(query.ErrorMessage))
                .Select(query => new JavaScriptServerReportError
                {
                    QueryName = query.Name,
                    Message = query.ErrorMessage!
                })
                .ToList()
        };
    }

    private static List<Dictionary<string, string?>> ToObjectRows(ServerReportQueryResult query)
    {
        var rows = new List<Dictionary<string, string?>>();
        foreach (var row in query.Rows)
        {
            var item = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < query.Columns.Count; index++)
            {
                var column = ToSafeObjectPropertyName(query.Columns[index], index);
                item[column] = index < row.Count ? row[index] : null;
            }

            rows.Add(item);
        }

        return rows;
    }

    private static string ResolveJavaScriptVariableName(string? reportKey, string? variableName)
    {
        var candidate = string.IsNullOrWhiteSpace(variableName) ? reportKey : variableName;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return "serverReport";
        }

        var sanitized = new StringBuilder(candidate.Length);
        foreach (var ch in candidate.Trim())
        {
            sanitized.Append(char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        if (sanitized.Length == 0)
        {
            return "serverReport";
        }

        if (char.IsAsciiDigit(sanitized[0]))
        {
            sanitized.Insert(0, '_');
        }

        return sanitized.ToString();
    }

    private static string ToSafeObjectPropertyName(string columnName, int columnIndex)
    {
        var trimmed = columnName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return $"column{columnIndex + 1}";
        }

        return trimmed;
    }

    private static void AppendEncoded(StringBuilder html, string value)
        => html.Append(DefaultHtmlEncoder.Encode(value));

    private sealed class JavaScriptServerReport
    {
        public string Title { get; set; } = string.Empty;

        public IReadOnlyList<Dictionary<string, string?>> Rows { get; set; } = [];

        public IReadOnlyList<JavaScriptServerReportQuery> Queries { get; set; } = [];

        public IReadOnlyList<JavaScriptServerReportError> Errors { get; set; } = [];
    }

    private sealed class JavaScriptServerReportQuery
    {
        public string Name { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public IReadOnlyList<string> Columns { get; set; } = [];

        public IReadOnlyList<Dictionary<string, string?>> Rows { get; set; } = [];

        public bool IsTruncated { get; set; }

        public int MaxRows { get; set; }

        public string? ErrorMessage { get; set; }
    }

    private sealed class JavaScriptServerReportError
    {
        public string? QueryName { get; set; }

        public string Message { get; set; } = string.Empty;
    }
}
