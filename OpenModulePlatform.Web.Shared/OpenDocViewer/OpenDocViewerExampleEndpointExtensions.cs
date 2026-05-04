using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace OpenModulePlatform.Web.Shared.OpenDocViewer;

public static class OpenDocViewerExampleEndpointExtensions
{
    public static RouteHandlerBuilder MapOpenDocViewerExampleBundleEndpoint(
        this IEndpointRouteBuilder endpoints,
        string source,
        string pattern = "/" + OpenDocViewerExampleBundleFactory.DefaultBundleEndpointPath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.MapGet(pattern, (
            HttpContext context,
            IOptions<OpenDocViewerExampleOptions> options) =>
        {
            var bundle = OpenDocViewerExampleBundleFactory.BuildSampleBundle(
                context.Request,
                options.Value,
                source,
                context.User.Identity?.Name);

            return Results.Json(bundle, OpenDocViewerExampleBundleFactory.JsonOptions);
        });
    }
}
