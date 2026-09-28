using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Options;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Integration;

/// <summary>
/// Renders dashboard widget partials through the Portal's real Razor pipeline.
/// </summary>
[Collection(PushEventPipelineTestCollection.Name)]
public sealed class DashboardWidgetRenderingIntegrationTests
{
    private readonly PushEventPipelineTestFixture _fixture;

    public DashboardWidgetRenderingIntegrationTests(PushEventPipelineTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WidgetReadiness_ShowsAffectedVersionAndSameVersionRepairAction()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var html = await RenderPartialAsync(scope.ServiceProvider, CreateHttpContext(scope.ServiceProvider),
            "/Pages/Shared/_DashboardWidgetReadiness.cshtml",
            new DashboardWidgetReadiness(
                new List<DashboardWidgetReadinessIssue> { new(190003, "example:legacy", "1.0.0", false) },
                ModuleFragmentEndpointIssue.None));

        Assert.Contains("example:legacy", html, StringComparison.Ordinal);
        Assert.Contains("1.0.0", html, StringComparison.Ordinal);
        Assert.Contains("190003", html, StringComparison.Ordinal);
        Assert.Contains("Quick import", html, StringComparison.Ordinal);
        Assert.Contains("Replace existing dashboard widgets for rollback or repair", html, StringComparison.Ordinal);
        Assert.Contains("/admin/modulepackageimport", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WidgetReadiness_ShowsWidgetsSkippedByHostAgentImportWithReason()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var html = await RenderPartialAsync(scope.ServiceProvider, CreateHttpContext(scope.ServiceProvider),
            "/Pages/Shared/_DashboardWidgetReadiness.cshtml",
            new DashboardWidgetReadiness(
                new List<DashboardWidgetReadinessIssue>
                {
                    new(0, "example:future", string.Empty, false)
                    {
                        SkipReason = "unsupported widgetType future-widget",
                        SkippedUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)
                    }
                },
                ModuleFragmentEndpointIssue.None));

        Assert.Contains("No invalid stored module-fragment widget definitions were found.", html, StringComparison.Ordinal);
        Assert.Contains("Widgets skipped by a HostAgent import", html, StringComparison.Ordinal);
        Assert.Contains("example:future", html, StringComparison.Ordinal);
        Assert.Contains("unsupported widgetType future-widget", html, StringComparison.Ordinal);
        Assert.DoesNotContain("(#0)", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WidgetReadiness_ShowsBlockedEndpointReasonAndAction()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var html = await RenderPartialAsync(scope.ServiceProvider, CreateHttpContext(scope.ServiceProvider),
            "/Pages/Shared/_DashboardWidgetReadiness.cshtml",
            new DashboardWidgetReadiness([], ModuleFragmentEndpointIssue.InsecureInternalBaseUrlForHttpsRequest));

        Assert.Contains("ModuleFragmentWidgets:InternalBaseUrl uses HTTP", html, StringComparison.Ordinal);
        Assert.Contains("ModuleFragmentWidgets:AllowInsecureInternalBaseUrl=true", html, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModuleFragmentWidget_EndpointBlocked_ShowsReasonOnlyToAdministrators(bool isAdmin)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var html = await RenderPartialAsync(scope.ServiceProvider, CreateHttpContext(scope.ServiceProvider),
            "/Pages/Shared/_DashboardModuleFragmentWidget.cshtml",
            new DashboardModuleFragmentWidget(7, 300,
                ModuleFragmentResult.EndpointBlocked(ModuleFragmentEndpointIssue.InsecureInternalBaseUrlForHttpsRequest),
                ShowAdminDiagnostics: isAdmin));

        Assert.Contains("dashboard-module-fragment__placeholder", html, StringComparison.Ordinal);
        Assert.Equal(isAdmin, html.Contains("ModuleFragmentWidgets:AllowInsecureInternalBaseUrl=true", StringComparison.Ordinal));
        Assert.Equal(isAdmin, html.Contains("/admin/maintenance#maintenance-widgets", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ModuleFragmentWidget_Unavailable_RendersNeutralPlaceholder()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var httpContext = CreateHttpContext(scope.ServiceProvider);

        var html = await RenderPartialAsync(
            scope.ServiceProvider,
            httpContext,
            "/Pages/Shared/_DashboardModuleFragmentWidget.cshtml",
            new DashboardModuleFragmentWidget(7, 300, ModuleFragmentResult.Unavailable));

        Assert.Contains("dashboard-module-fragment__placeholder", html, StringComparison.Ordinal);
        Assert.Contains("is-unavailable", html, StringComparison.Ordinal);
        Assert.Contains("is-narrow", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModuleFragmentWidget_Loaded_RendersSanitizedContentWithModeClass()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var httpContext = CreateHttpContext(scope.ServiceProvider);
        var fragment = ModuleFragmentHtmlSanitizer.Sanitize(
            "<div data-widget-mode=\"medium\"><strong>42</strong><script>x()</script></div>",
            "/sample");

        var html = await RenderPartialAsync(
            scope.ServiceProvider,
            httpContext,
            "/Pages/Shared/_DashboardModuleFragmentWidget.cshtml",
            new DashboardModuleFragmentWidget(7, 900, fragment));

        Assert.Contains("<strong>42</strong>", html, StringComparison.Ordinal);
        Assert.Contains("is-medium", html, StringComparison.Ordinal);
        Assert.DoesNotContain("is-wide", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
    }

    private static DefaultHttpContext CreateHttpContext(IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        return context;
    }

    private static async Task<string> RenderPartialAsync(
        IServiceProvider services,
        HttpContext httpContext,
        string viewPath,
        object model)
    {
        var viewEngine = services.GetRequiredService<ICompositeViewEngine>();
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath, isMainPage: false);
        Assert.True(viewResult.Success, $"View '{viewPath}' was not found.");

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
        await viewResult.View.RenderAsync(viewContext);
        return writer.ToString();
    }
}
