// File: OpenModulePlatform.Web.ContentWebAppModule/Services/ContentRenderer.cs
using Markdig;
using OpenModulePlatform.Web.ContentWebAppModule.Models;

namespace OpenModulePlatform.Web.ContentWebAppModule.Services;

public sealed class ContentRenderer
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
        return format switch
        {
            ContentTypes.Html => content,
            ContentTypes.ServerReport => await _serverReportRenderer.RenderAsync(serverReportKey, ct),
            _ => Markdown.ToHtml(content ?? string.Empty, MarkdownPipeline)
        };
    }
}
