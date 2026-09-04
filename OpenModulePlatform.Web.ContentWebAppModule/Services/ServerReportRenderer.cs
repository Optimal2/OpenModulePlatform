// File: OpenModulePlatform.Web.ContentWebAppModule/Services/ServerReportRenderer.cs
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Localization;
using OpenModulePlatform.Web.ContentWebAppModule.Localization;
using OpenModulePlatform.Web.ContentWebAppModule.Models;

namespace OpenModulePlatform.Web.ContentWebAppModule.Services;

public sealed class ServerReportRenderer
{
    private static readonly HtmlEncoder DefaultHtmlEncoder = HtmlEncoder.Default;

    /// <summary>
    /// Serializer settings for report data that is written inside a &lt;script&gt; block.
    /// </summary>
    /// <remarks>
    /// The encoder is load-bearing, not a default that happened to be left in place. Report
    /// rows come from a database and can contain any text, including a literal
    /// "&lt;/script&gt;", which inside a script block would end the block and let the rest of
    /// the value be parsed as markup.
    ///
    /// JavaScriptEncoder.Default escapes '&lt;' to <, so no value can produce a closing
    /// tag. Verified by experiment rather than assumed, because the mechanism is not the one
    /// it is often described as: the encoder does NOT escape '/', so a value like "a/b" is
    /// emitted verbatim. It is the '&lt;' escaping alone that closes the hole.
    ///
    /// Replacing this with JavaScriptEncoder.UnsafeRelaxedJsonEscaping would reintroduce the
    /// breakout. ServerReportRendererEncodingTests pins the behaviour so that swap fails a
    /// test rather than shipping. Flagged for verification by GitHub code quality.
    /// </remarks>
    internal static readonly JsonSerializerOptions JavaScriptJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
    };

    private readonly ServerReportDefinitionLoader _definitionLoader;
    private readonly ServerReportQueryRunner _queryRunner;
    private readonly ILogger<ServerReportRenderer> _logger;
    private readonly IStringLocalizer<ContentWebAppModuleResource> _localizer;

    public ServerReportRenderer(
        ServerReportDefinitionLoader definitionLoader,
        ServerReportQueryRunner queryRunner,
        ILogger<ServerReportRenderer> logger,
        IStringLocalizer<ContentWebAppModuleResource> localizer)
    {
        _definitionLoader = definitionLoader;
        _queryRunner = queryRunner;
        _logger = logger;
        _localizer = localizer;
    }

    public async Task<string> RenderAsync(string? reportKey, CancellationToken ct)
    {
        try
        {
            return await RenderCoreAsync(reportKey, ct).ConfigureAwait(false);
        }
        catch (ServerReportException ex)
        {
            _logger.LogWarning(ex, "Server report definition could not be rendered for key {ReportKey}", reportKey);
            return RenderError(ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-requested cancellation is intentionally propagated instead of
            // being converted to a rendered error block.
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError(ex, "Unexpected server report rendering cancellation for key {ReportKey}", reportKey);
            return RenderError(_localizer["The server report could not be rendered."].Value);
        }
        catch (SystemException ex)
        {
            _logger.LogError(ex, "Unexpected server report rendering failure for key {ReportKey}", reportKey);
            return RenderError(_localizer["The server report could not be rendered."].Value);
        }
    }

    public async Task<string> RenderJavaScriptAsync(
        string? reportKey,
        string? variableName,
        string readerScriptUrl,
        CancellationToken ct)
    {
        try
        {
            return await RenderJavaScriptCoreAsync(reportKey, variableName, readerScriptUrl, ct).ConfigureAwait(false);
        }
        catch (ServerReportException ex)
        {
            _logger.LogWarning(ex, "Server report JavaScript definition could not be rendered for key {ReportKey}", reportKey);
            return RenderJavaScriptError(reportKey, variableName, ex.Message, readerScriptUrl);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-requested cancellation is intentionally propagated instead of
            // being converted to a JavaScript error payload.
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError(ex, "Unexpected server report JavaScript rendering cancellation for key {ReportKey}", reportKey);
            return RenderJavaScriptError(reportKey, variableName, _localizer["The server report could not be rendered."].Value, readerScriptUrl);
        }
        catch (SystemException ex)
        {
            _logger.LogError(ex, "Unexpected server report JavaScript rendering failure for key {ReportKey}", reportKey);
            return RenderJavaScriptError(reportKey, variableName, _localizer["The server report could not be rendered."].Value, readerScriptUrl);
        }
    }

    private async Task<string> RenderCoreAsync(string? reportKey, CancellationToken ct)
    {
        var definition = await _definitionLoader.LoadAsync(reportKey, ct).ConfigureAwait(false);
        var result = await _queryRunner.ExecuteAsync(definition, ct).ConfigureAwait(false);
        return RenderResult(result, _localizer);
    }

    private async Task<string> RenderJavaScriptCoreAsync(
        string? reportKey,
        string? variableName,
        string readerScriptUrl,
        CancellationToken ct)
    {
        var definition = await _definitionLoader.LoadAsync(reportKey, ct).ConfigureAwait(false);
        var result = await _queryRunner.ExecuteAsync(definition, ct).ConfigureAwait(false);
        return RenderJavaScriptResult(result, ResolveJavaScriptVariableName(reportKey, variableName), readerScriptUrl);
    }

    private static string RenderResult(
        ServerReportResult result,
        IStringLocalizer<ContentWebAppModuleResource> localizer)
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
            RenderQuery(html, query, localizer);
        }

        html.Append("</section>");
        return html.ToString();
    }

    private static void RenderQuery(
        StringBuilder html,
        ServerReportQueryResult query,
        IStringLocalizer<ContentWebAppModuleResource> localizer)
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

        // These three strings sit in the same rendered output as the error messages above,
        // which R8-P5-4 already routed through the module resource -- but they were left
        // as hardcoded English, so a Swedish reader got a localized error and an English
        // empty state on the same page. Reported by GitHub code quality.
        if (query.Columns.Count == 0)
        {
            html.Append("<div class=\"server-report__empty\">");
            AppendEncoded(html, localizer["The report query returned no columns."].Value);
            html.Append("</div></section>");
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
            html.Append("\">");
            AppendEncoded(html, localizer["No rows were returned."].Value);
            html.Append("</td></tr>");
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
            // Parameterised rather than concatenated, so a translation can put the number
            // where its own grammar needs it instead of always in the middle.
            html.Append("<p class=\"muted server-report__truncated\">");
            AppendEncoded(html, localizer["Result truncated at {0} rows.", query.MaxRows].Value);
            html.Append("</p>");
        }

        html.Append("</section>");
    }

    // R8-P5-4: this HTML is rendered to end users, not administrators, and the heading
    // was hardcoded English while the message came straight off the exception. Both now
    // go through the module resource; Display leaves an unknown text unchanged, so no
    // information is lost when a report defines its own wording.
    private string RenderError(string message)
    {
        var html = new StringBuilder();
        html.Append("<section class=\"server-report server-report--error\"><h2>");
        AppendEncoded(html, _localizer["Server report unavailable"].Value);
        html.Append("</h2><p>");
        AppendEncoded(html, ContentWebAppTextLocalizer.Display(_localizer, message));
        html.Append("</p></section>");
        return html.ToString();
    }

    // The DB_JSON_SCRIPT shortcode emits a non-executable JSON data block (CSP
    // report-only preparation, campaign csp-vagen-till-enforcement) followed by the
    // static reader script, which assigns the documented window.<name> (rows only) and
    // window.<name>Report (full metadata) globals. The reader tag is emitted after each
    // data block so a following script in the trusted content sees the globals in source
    // order, exactly as the old inline assignment did.
    private static string RenderJavaScriptResult(ServerReportResult result, string variableName, string readerScriptUrl)
    {
        var report = ToJavaScriptReport(result);
        var payloadJson = JsonSerializer.Serialize(
            new { rows = report.Rows, report },
            JavaScriptJsonOptions);

        var html = new StringBuilder();
        AppendJavaScriptPayloadBlock(html, variableName, payloadJson, readerScriptUrl);
        return html.ToString();
    }

    private string RenderJavaScriptError(string? reportKey, string? variableName, string message, string readerScriptUrl)
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

        var payloadJson = JsonSerializer.Serialize(
            new { rows = Array.Empty<object>(), report },
            JavaScriptJsonOptions);
        var html = new StringBuilder();
        AppendJavaScriptPayloadBlock(html, resolvedVariableName, payloadJson, readerScriptUrl);
        return html.ToString();
    }

    private static void AppendJavaScriptPayloadBlock(
        StringBuilder html,
        string variableName,
        string payloadJson,
        string readerScriptUrl)
    {
        // variableName comes from ResolveJavaScriptVariableName (ASCII letters, digits,
        // underscore), so it is safe as a bare attribute value. The payload encoder keeps
        // '<' escaped, so no database value can emit a closing tag inside the block.
        html.Append("<script type=\"application/json\" data-omp-server-report-json data-variable-name=\"");
        html.Append(variableName);
        html.AppendLine("\">");
        html.AppendLine(payloadJson);
        html.AppendLine("</script>");
        html.Append("<script src=\"");
        html.Append(readerScriptUrl);
        html.AppendLine("\"></script>");
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

            flatRows.AddRange(rows.Select(row => new Dictionary<string, string?>(row, StringComparer.OrdinalIgnoreCase)
            {
                ["__queryName"] = query.Name,
                ["__queryTitle"] = query.Title
            }));
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
