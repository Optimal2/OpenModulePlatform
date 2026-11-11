using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using OpenModulePlatform.Portal.Tests.Integration;
using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Navigation;
using OpenModulePlatform.Web.Shared.Runtime;

namespace OpenModulePlatform.Portal.Tests.Navigation;

/// <summary>Renders runtime-advisory models through the real shared MVC top-bar view.</summary>
[Collection(PushEventPipelineTestCollection.Name)]
public sealed class PortalTopBarRuntimeAdvisoryRenderingTests
{
    private readonly PushEventPipelineTestFixture _fixture;

    public PortalTopBarRuntimeAdvisoryRenderingTests(PushEventPipelineTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MvcTopBar_AdminAndNonAdminModelsRenderWithoutException()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var localizer = scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResource>>();
        var advisory = OmpRuntimeAdvisory.Compare(new Version(10, 0, 14), "10.0.15");
        var adminModel = PortalTopBarRuntimeAdvisoryTests.CreateModel(
            isPortalAdmin: true,
            advisory,
            localizer);
        var nonAdminModel = PortalTopBarRuntimeAdvisoryTests.CreateModel(
            isPortalAdmin: false,
            advisory,
            localizer);

        var adminHtml = await RenderTopBarAsync(scope.ServiceProvider, adminModel);
        var nonAdminHtml = await RenderTopBarAsync(scope.ServiceProvider, nonAdminModel);

        Assert.Contains("portal-topbar__notification-banner--warning", adminHtml, StringComparison.Ordinal);
        Assert.Contains("Older .NET runtime on the server", adminHtml, StringComparison.Ordinal);
        Assert.Contains("10.0.14", adminHtml, StringComparison.Ordinal);
        Assert.Contains("10.0.15", adminHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Older .NET runtime on the server", nonAdminHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("portal-topbar__notification-banner--warning", nonAdminHtml, StringComparison.Ordinal);
    }

    private static async Task<string> RenderTopBarAsync(
        IServiceProvider services,
        PortalTopBarModel model)
    {
        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("localhost");
        httpContext.Request.Path = "/";

        var viewEngine = services.GetRequiredService<ICompositeViewEngine>();
        var viewResult = viewEngine.GetView(
            executingFilePath: null,
            "/Views/Shared/Components/PortalTopBar/Default.cshtml",
            isMainPage: false);
        Assert.True(viewResult.Success, "The shared PortalTopBar MVC view was not found.");

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = model
        };

        await using var writer = new StringWriter();
        var viewContext = new ViewContext(
            actionContext,
            viewResult.View,
            viewData,
            new TempDataDictionary(httpContext, services.GetRequiredService<ITempDataProvider>()),
            writer,
            new HtmlHelperOptions());

        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            await viewResult.View.RenderAsync(viewContext);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }

        return writer.ToString();
    }
}
