// File: OpenModulePlatform.Web.ContentWebAppModule/Services/ContentRenderer.cs
using Markdig;
using OpenModulePlatform.Web.ContentWebAppModule.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenModulePlatform.Web.ContentWebAppModule.Services;

public sealed partial class ContentRenderer
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private readonly ServerReportRenderer _serverReportRenderer;

    public ContentRenderer(ServerReportRenderer serverReportRenderer)
    {
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

        var expandedContent = await ExpandServerReportShortcodesAsync(content ?? string.Empty, ct);
        return format switch
        {
            ContentTypes.Html => expandedContent,
            _ => Markdown.ToHtml(expandedContent, MarkdownPipeline)
        };
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
            result.Append(await _serverReportRenderer.RenderAsync(match.Groups["key"].Value, ct));
            result.AppendLine();
            lastIndex = match.Index + match.Length;
        }

        result.Append(content, lastIndex, content.Length - lastIndex);
        return result.ToString();
    }

    [GeneratedRegex("""\[\s*DB\\?_JSON\s*=\s*["'](?<key>[a-zA-Z0-9_-]+)["']\s*\]""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServerReportShortcodeRegex();
}
