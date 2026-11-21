using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Web.Shared.Options;

namespace OpenModulePlatform.Portal.Tests.Pages;

/// <summary>
/// Calls a Portal page handler directly, without routing or a rendered page. The options
/// allow anonymous access, so the permission guard passes without RBAC or a database; the
/// request services carry the Portal's localization the way the app registers it.
/// </summary>
internal static class PageHandlerHarness
{
    public static IOptions<WebAppOptions> AnonymousOptions()
        => Microsoft.Extensions.Options.Options.Create(new WebAppOptions { AllowAnonymous = true });

    public static IStringLocalizer<PortalResource> PortalLocalizer()
        => Services().GetRequiredService<IStringLocalizer<PortalResource>>();

    public static TModel Prepare<TModel>(TModel model)
        where TModel : PageModel
    {
        var httpContext = new DefaultHttpContext { RequestServices = Services() };
        var modelState = new ModelStateDictionary();
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new CompiledPageActionDescriptor(),
            modelState);
        model.PageContext = new PageContext(actionContext)
        {
            ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), modelState)
        };
        return model;
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        return services.BuildServiceProvider();
    }

    /// <summary>Runs <paramref name="action"/> with English UI texts, whatever the machine's culture.</summary>
    public static async Task InEnglishAsync(Func<Task> action)
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            await action();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }
}
