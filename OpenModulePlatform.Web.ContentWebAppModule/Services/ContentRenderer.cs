// File: OpenModulePlatform.Web.ContentWebAppModule/Services/ContentRenderer.cs
using Markdig;
using OpenModulePlatform.Web.ContentWebAppModule.Models;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace OpenModulePlatform.Web.ContentWebAppModule.Services;

public sealed partial class ContentRenderer
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly HtmlEncoder DefaultHtmlEncoder = HtmlEncoder.Default;

    private readonly HtmlContentFileLoader _htmlFileLoader;
    private readonly ServerReportRenderer _serverReportRenderer;

    public ContentRenderer(
        HtmlContentFileLoader htmlFileLoader,
        ServerReportRenderer serverReportRenderer)
    {
        _htmlFileLoader = htmlFileLoader;
        _serverReportRenderer = serverReportRenderer;
    }

    public async Task<string> RenderToHtmlAsync(
        string content,
        string? contentType,
        string? serverReportKey,
        CancellationToken ct)
    {
        var format = ContentTypes.Normalize(contentType);
        if (format == ContentTypes.ServerReport)
        {
            return await _serverReportRenderer.RenderAsync(serverReportKey, ct);
        }

        if (format == ContentTypes.HtmlFile)
        {
            return await RenderHtmlFileAsync(serverReportKey, ct);
        }

        var expandedContent = await ExpandServerReportShortcodesAsync(content ?? string.Empty, ct);
        return format switch
        {
            ContentTypes.Html => expandedContent,
            _ => Markdown.ToHtml(expandedContent, MarkdownPipeline)
        };
    }

    private async Task<string> RenderHtmlFileAsync(string? htmlFileKey, CancellationToken ct)
    {
        try
        {
            var content = await _htmlFileLoader.LoadAsync(htmlFileKey, ct).ConfigureAwait(false);
            return await ExpandServerReportShortcodesAsync(content, ct).ConfigureAwait(false);
        }
        catch (HtmlContentFileException ex)
        {
            return "<section class=\"server-report server-report--error\"><h2>HTML content file unavailable</h2><p>"
                + DefaultHtmlEncoder.Encode(ex.Message)
                + "</p></section>";
        }
    }

    private async Task<string> ExpandServerReportShortcodesAsync(string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content)
            || content.IndexOf("[DB", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return content;
        }

        var matches = ServerReportShortcodeRegex().Matches(content);
        if (matches.Count == 0)
        {
            return content;
        }

        var result = new StringBuilder(content.Length);
        var lastIndex = 0;
        foreach (Match match in matches)
        {
            result.Append(content, lastIndex, match.Index - lastIndex);
            result.AppendLine();
            var key = match.Groups["key"].Value;
            if (match.Groups["script"].Success)
            {
                result.Append(await _serverReportRenderer.RenderJavaScriptAsync(
                    key,
                    match.Groups["variable"].Success ? match.Groups["variable"].Value : null,
                    ct));
            }
            else
            {
                result.Append(await _serverReportRenderer.RenderAsync(key, ct));
            }

            result.AppendLine();
            lastIndex = match.Index + match.Length;
        }

        result.Append(content, lastIndex, content.Length - lastIndex);
        return result.ToString();
    }

    [GeneratedRegex("""\[\s*DB\\?_JSON(?<script>\\?_SCRIPT)?\s*=\s*["'](?<key>[a-zA-Z0-9_-]+)["'](?:\s+(?:variable|var|name)\s*=\s*["'](?<variable>[A-Za-z_][A-Za-z0-9_]{0,127})["'])?\s*\]""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServerReportShortcodeRegex();
}
