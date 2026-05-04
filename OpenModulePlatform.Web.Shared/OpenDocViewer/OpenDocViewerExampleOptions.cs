namespace OpenModulePlatform.Web.Shared.OpenDocViewer;

/// <summary>
/// Options for the OpenDocViewer integration sample pages in OMP example web applications.
/// </summary>
public sealed class OpenDocViewerExampleOptions
{
    public const string DefaultSectionName = "OpenDocViewer";

    public string BaseUrl { get; set; } = "/opendocviewer/";

    public string SampleFileUrl { get; set; } = "/opendocviewer/sample.pdf";
}
