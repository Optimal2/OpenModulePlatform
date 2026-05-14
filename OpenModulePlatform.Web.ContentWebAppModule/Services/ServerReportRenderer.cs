// File: OpenModulePlatform.Web.ContentWebAppModule/Services/ServerReportRenderer.cs
using System.Text;
using System.Text.Encodings.Web;
using OpenModulePlatform.Web.ContentWebAppModule.Models;

namespace OpenModulePlatform.Web.ContentWebAppModule.Services;

public sealed class ServerReportRenderer
{
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

    private async Task<string> RenderCoreAsync(string? reportKey, CancellationToken ct)
    {
        var definition = await _definitionLoader.LoadAsync(reportKey, ct).ConfigureAwait(false);
        var result = await _queryRunner.ExecuteAsync(definition, ct).ConfigureAwait(false);
        return RenderResult(result);
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

    private static void AppendEncoded(StringBuilder html, string value)
        => html.Append(HtmlEncoder.Default.Encode(value));
}
