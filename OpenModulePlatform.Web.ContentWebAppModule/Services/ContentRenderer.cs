// File: OpenModulePlatform.Web.ContentWebAppModule/Services/ContentRenderer.cs
using Ganss.Xss;
using Markdig;
using OpenModulePlatform.Web.ContentWebAppModule.Models;

namespace OpenModulePlatform.Web.ContentWebAppModule.Services;

public sealed class ContentRenderer
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private readonly HtmlSanitizer _sanitizer = CreateSanitizer();

    public string RenderToSafeHtml(string content, string? contentFormat)
    {
        var format = ContentFormats.Normalize(contentFormat);
        var html = format == ContentFormats.Html
            ? content
            : Markdown.ToHtml(content ?? string.Empty, MarkdownPipeline);

        return _sanitizer.Sanitize(html);
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowedSchemes.Add("tel");
        return sanitizer;
    }
}
