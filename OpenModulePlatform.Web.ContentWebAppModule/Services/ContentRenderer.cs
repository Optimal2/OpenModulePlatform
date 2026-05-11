// File: OpenModulePlatform.Web.ContentWebAppModule/Services/ContentRenderer.cs
using Markdig;
using OpenModulePlatform.Web.ContentWebAppModule.Models;

namespace OpenModulePlatform.Web.ContentWebAppModule.Services;

public sealed class ContentRenderer
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public string RenderToHtml(string content, string? contentType)
    {
        var format = ContentTypes.Normalize(contentType);
        return format == ContentTypes.Html
            ? content
            : Markdown.ToHtml(content ?? string.Empty, MarkdownPipeline);
    }
}
