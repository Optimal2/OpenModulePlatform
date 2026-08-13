using OpenModulePlatform.Web.Shared.OpenDocViewer;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Text.Json;
using OpenModulePlatform.Web.Shared.Web;

namespace OpenModulePlatform.Web.ExampleWebAppModule.Pages;

public sealed class OpenDocViewerDemoModel : ExampleWebAppModulePageModel
{
    private readonly OpenDocViewerExampleOptions _openDocViewerOptions;

    public OpenDocViewerDemoModel(
        IOptions<WebAppOptions> options,
        RbacService rbac,
        IOptions<OpenDocViewerExampleOptions> openDocViewerOptions)
        : base(options, rbac)
    {
        _openDocViewerOptions = openDocViewerOptions.Value;
    }

    public string BundleJson { get; private set; } = "{}";

    public string OpenDocViewerBaseUrl { get; private set; } = "/opendocviewer/";

    public string OpenDocViewerFrameUrl { get; private set; } = "/opendocviewer/";

    public string OpenDocViewerNewTabUrl { get; private set; } = "/opendocviewer/";

    public string UserId { get; private set; } = "anonymous";

    public async Task<IActionResult> OnGet(CancellationToken ct)
    {
        var guard = await RequireViewAsync(ct);
        if (guard is not null)
        {
            return guard;
        }

        SetTitles("OpenDocViewer integration");
        UserId = string.IsNullOrWhiteSpace(User.Identity?.Name) ? "anonymous" : User.Identity.Name!;
        OpenDocViewerBaseUrl = OmpUrlSafety.NormalizeViewerBaseUrl(_openDocViewerOptions.BaseUrl, "/opendocviewer/");

        var bundle = OpenDocViewerExampleBundleFactory.BuildSampleBundle(
            Request,
            _openDocViewerOptions,
            "OpenModulePlatform.Web.ExampleWebAppModule",
            UserId);

        BundleJson = JsonSerializer.Serialize(bundle, OpenDocViewerExampleBundleFactory.JsonOptions);

        var bundleUrl = OpenDocViewerExampleBundleFactory.ToAbsoluteUrl(
            Request,
            OpenDocViewerExampleBundleFactory.DefaultBundleEndpointPath);

        OpenDocViewerFrameUrl = OpenDocViewerExampleBundleFactory.BuildFrameUrl(
            _openDocViewerOptions,
            bundleUrl);
        OpenDocViewerNewTabUrl = OpenDocViewerFrameUrl;

        return Page();
    }

}
