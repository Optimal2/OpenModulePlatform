// File: OpenModulePlatform.Web.Shared/Extensions/OmpWebHostingExtensions.cs
using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Models;
using OpenModulePlatform.Web.Shared.Navigation;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Server.IISIntegration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using SystemNetIPNetwork = System.Net.IPNetwork;

namespace OpenModulePlatform.Web.Shared.Extensions;

/// <summary>
/// Registers the common hosting defaults used by the Portal and module web applications.
/// </summary>
/// <remarks>
/// <para>
/// The shared defaults deliberately stay small: Razor Pages, Windows-integrated
/// authentication, optional forwarded-header support, and the shared services that
/// every OMP web application depends on.
/// </para>
/// <para>
/// Centralising these defaults reduces copy/paste between the Portal and individual
/// module UIs while still allowing each application to add its own services.
/// </para>
/// </remarks>
public static class OmpWebHostingExtensions
{
    public static WebApplicationBuilder AddOmpWebDefaults<TAppResource>(
        this WebApplicationBuilder builder,
        string optionsSectionName = WebAppOptions.DefaultSectionName)
        where TAppResource : class
    {
        builder.AddOmpWebLogging();

        builder.Services.AddSingleton<IValidateOptions<WebAppOptions>, WebAppOptionsValidator>();

        builder.Services.AddOptions<WebAppOptions>()
            .Bind(builder.Configuration.GetSection(optionsSectionName))
            .ValidateOnStart();

        builder.Services.AddLocalization(options =>
        {
            options.ResourcesPath = "Resources";
        });

        builder.Services.AddRazorPages()
            .AddDataAnnotationsLocalization(options =>
            {
                options.DataAnnotationLocalizerProvider = static (_, factory) =>
                    factory.Create(typeof(TAppResource));
            });

        var runningUnderIis = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("ASPNETCORE_IIS_PHYSICAL_PATH"));

        if (runningUnderIis)
        {
            builder.Services.AddAuthentication(IISDefaults.AuthenticationScheme);
        }
        else
        {
            builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
                .AddNegotiate();
        }

        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = options.DefaultPolicy;
        });

        var webAppOptions = builder.Configuration
            .GetSection(optionsSectionName)
            .Get<WebAppOptions>() ?? new WebAppOptions();

        builder.Services.Configure<RequestLocalizationOptions>(options =>
        {
            var supportedCultureNames = webAppOptions.SupportedCultures;

            if (supportedCultureNames is null || supportedCultureNames.Length == 0)
            {
                supportedCultureNames = [webAppOptions.DefaultCulture];
            }

            var supportedCultures = supportedCultureNames
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(x => new CultureInfo(x))
                .ToArray();

            if (supportedCultures.Length == 0)
            {
                supportedCultures = [new CultureInfo("sv-SE")];
            }

            var defaultCultureName = string.IsNullOrWhiteSpace(webAppOptions.DefaultCulture)
                ? supportedCultures[0].Name
                : webAppOptions.DefaultCulture;

            options.DefaultRequestCulture = new RequestCulture(defaultCultureName);
            options.SupportedCultures = supportedCultures;
            options.SupportedUICultures = supportedCultures;
            options.RequestCultureProviders =
            [
                new PreferredCultureRequestCultureProvider(webAppOptions, new CultureSelectionService()),
                new CookieRequestCultureProvider(),
                new AcceptLanguageHeaderRequestCultureProvider()
            ];
        });

        builder.Services.AddOptions<ForwardedHeadersOptions>()
            .Configure<ILoggerFactory>((options, loggerFactory) =>
            {
                var logger = loggerFactory.CreateLogger(
                    "OpenModulePlatform.Web.Shared.ForwardedHeaders");

                ConfigureForwardedHeaders(options, webAppOptions, logger);
            });

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddTransient<IClaimsTransformation, ActiveRoleClaimsTransformation>();
        builder.Services.AddSingleton<CultureSelectionService>();
        builder.Services.AddSingleton<SqlConnectionFactory>();
        builder.Services.AddScoped<RbacService>();
        builder.Services.AddScoped<OpenModulePlatform.Web.Shared.Navigation.PortalTopBarService>();

        return builder;
    }

    public static WebApplication UseOmpWebDefaults(
        this WebApplication app,
        string optionsSectionName = WebAppOptions.DefaultSectionName,
        bool mapRazorPages = true)
    {
        var options = app.Configuration
            .GetSection(optionsSectionName)
            .Get<WebAppOptions>() ?? new WebAppOptions();

        if (options.UseForwardedHeaders)
        {
            app.UseForwardedHeaders();
        }

        var localizationOptions = app.Services
            .GetRequiredService<IOptions<RequestLocalizationOptions>>()
            .Value;

        app.UseRequestLocalization(localizationOptions);
        app.UseStaticFiles();
        app.UseStatusCodePagesWithReExecute("/status/{0}");

        if (!mapRazorPages)
        {
            app.MapGet("/status/{statusCode:int}", (
                HttpContext context,
                int statusCode,
                IStringLocalizer<SharedResource> localizer) =>
            {
                var feature = context.Features.Get<IStatusCodeReExecuteFeature>();
                var requestedUrl = feature is null
                    ? context.Request.Path.ToString()
                    : string.Concat(feature.OriginalPathBase, feature.OriginalPath, feature.OriginalQueryString);
                var portalHref = OmpUrlPathHelper.CombinePortalHref(options.PortalTopBar.PortalBaseUrl, "/");
                var appHomeHref = OmpUrlPathHelper.BuildAppHomeHref(context.Request.PathBase);
                var model = OmpErrorDisplayModelFactory.CreateForStatusCode(
                    statusCode,
                    requestedUrl,
                    portalHref,
                    appHomeHref,
                    localizer,
                    showBackButton: true);

                return Results.Content(
                    BuildFallbackStatusPageHtml(model),
                    contentType: "text/html; charset=utf-8",
                    contentEncoding: System.Text.Encoding.UTF8,
                    statusCode: statusCode);
            }).AllowAnonymous();
        }

        app.MapGet("/localization/set-language", (
            HttpContext context,
            string culture,
            string? returnUrl,
            CultureSelectionService cultureSelectionService) =>
        {
            var preferredCulture = cultureSelectionService.NormalizePreferredCulture(culture, options);
            var effectiveCulture = cultureSelectionService.ResolveEffectiveCulture(preferredCulture, options);
            cultureSelectionService.ApplyCookies(context.Response, preferredCulture, effectiveCulture);

            if (!string.IsNullOrWhiteSpace(returnUrl) && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative))
            {
                return Results.LocalRedirect(returnUrl);
            }

            return Results.LocalRedirect("/");
        });

        async Task<IResult> HandleSetActiveRoleAsync(HttpContext context, int? roleId, string? returnUrl, RbacService rbac, PortalTopBarService portalTopBarService, CancellationToken ct)
        {
            var roleContext = await rbac.GetUserRoleContextAsync(context.User, ct);
            var validRoleIds = roleContext.AvailableRoles.Select(x => x.RoleId).ToHashSet();
            var roleChanged = roleId is int requestedRoleId && requestedRoleId != roleContext.ActiveRoleId;
            var targetPermissions = roleChanged
                ? await rbac.GetPermissionsForRoleAsync(context.User, roleId, ct)
                : roleContext.EffectivePermissions;

            if (roleId is int selectedRoleId && validRoleIds.Contains(selectedRoleId))
            {
                context.Response.Cookies.Append(
                    ActiveRoleCookie.CookieName,
                    selectedRoleId.ToString(CultureInfo.InvariantCulture),
                    new CookieOptions
                    {
                        Expires = DateTimeOffset.UtcNow.AddYears(1),
                        IsEssential = true,
                        HttpOnly = false,
                        SameSite = SameSiteMode.Lax,
                        Secure = true,
                        Path = "/"
                    });
            }
            else
            {
                context.Response.Cookies.Delete(ActiveRoleCookie.CookieName, new CookieOptions { Path = "/", Secure = true });
            }

            var safePortalHref = OmpUrlPathHelper.CombinePortalHref(options.PortalTopBar.PortalBaseUrl, "/");
            var canReturnToRequestedUrl = !string.IsNullOrWhiteSpace(returnUrl)
                && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
                && await portalTopBarService.CanAccessReturnUrlAsync(options, returnUrl, targetPermissions, ct);

            if (canReturnToRequestedUrl)
            {
                return Results.LocalRedirect(returnUrl!);
            }

            if (Uri.IsWellFormedUriString(safePortalHref, UriKind.Absolute))
            {
                return Results.Redirect(safePortalHref);
            }

            return Results.LocalRedirect(safePortalHref);
        }

        app.MapGet("/security/set-active-role", HandleSetActiveRoleAsync);
        app.MapGet("/rbac/set-active-role", HandleSetActiveRoleAsync);

        if (!options.AllowAnonymous)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        if (mapRazorPages)
        {
            app.MapRazorPages();
        }

        return app;
    }

    /// <summary>
    /// Applies the forwarded-header trust model defined in configuration.
    /// </summary>
    /// <remarks>
    /// Trusting all proxies is convenient during development but unsafe for internet-facing
    /// deployments unless a trusted reverse proxy is guaranteed in front of the application.
    /// </remarks>
    private static void ConfigureForwardedHeaders(
        ForwardedHeadersOptions options,
        WebAppOptions webAppOptions,
        ILogger logger)
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedProto |
            ForwardedHeaders.XForwardedHost;

        if (webAppOptions.ForwardedHeadersTrustAllProxies)
        {
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();

            logger.LogWarning(
                "Forwarded headers are configured to trust all proxies. " +
                "Only use this setting when a trusted reverse proxy is guaranteed.");

            return;
        }

        if (webAppOptions.ForwardedHeadersKnownProxies.Length > 0)
        {
            options.KnownProxies.Clear();

            foreach (var ipText in webAppOptions.ForwardedHeadersKnownProxies
                         .Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                if (IPAddress.TryParse(ipText.Trim(), out var ip))
                {
                    options.KnownProxies.Add(ip);
                }
                else
                {
                    logger.LogWarning(
                        "Skipped invalid forwarded-header proxy IP '{ProxyIp}'.",
                        ipText);
                }
            }
        }

        if (webAppOptions.ForwardedHeadersKnownNetworks.Length > 0)
        {
            options.KnownIPNetworks.Clear();

            foreach (var cidr in webAppOptions.ForwardedHeadersKnownNetworks
                         .Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                if (TryParseCidrNetwork(cidr, out var network))
                {
                    options.KnownIPNetworks.Add(network);
                }
                else
                {
                    logger.LogWarning(
                        "Skipped invalid forwarded-header network '{NetworkCidr}'.",
                        cidr);
                }
            }
        }
    }

    private static string BuildFallbackStatusPageHtml(OmpErrorDisplayModel model)
    {
        var safeTitle = WebUtility.HtmlEncode(model.Title);
        var safeMessage = WebUtility.HtmlEncode(model.Message);
        var safeRequestedUrlLabel = WebUtility.HtmlEncode(model.RequestedUrlLabel ?? string.Empty);
        var safeRequestedUrl = WebUtility.HtmlEncode(model.RequestedUrl ?? string.Empty);
        var safePortalHref = WebUtility.HtmlEncode(model.PortalHref ?? "/");
        var safePortalText = WebUtility.HtmlEncode(model.PortalText ?? string.Empty);
        var safeAppHomeHref = WebUtility.HtmlEncode(model.AppHomeHref ?? string.Empty);
        var safeAppHomeText = WebUtility.HtmlEncode(model.AppHomeText ?? string.Empty);
        var safeBackText = WebUtility.HtmlEncode(model.BackText ?? string.Empty);
        var requestedUrlMarkup = string.IsNullOrWhiteSpace(model.RequestedUrl)
            ? string.Empty
            : $"<p class='omp-error-view__detail'><strong>{safeRequestedUrlLabel}:</strong> <code>{safeRequestedUrl}</code></p>";
        var backButtonMarkup = model.ShowBackButton && !string.IsNullOrWhiteSpace(model.BackText)
            ? $"<button type='button' class='omp-error-view__button omp-error-view__button--secondary' onclick='history.back()'>{safeBackText}</button>"
            : string.Empty;
        var appHomeMarkup = string.IsNullOrWhiteSpace(model.AppHomeHref) || string.IsNullOrWhiteSpace(model.AppHomeText)
            ? string.Empty
            : $"<a class='omp-error-view__button omp-error-view__button--secondary' href='{safeAppHomeHref}'>{safeAppHomeText}</a>";
        var portalMarkup = string.IsNullOrWhiteSpace(model.PortalHref) || string.IsNullOrWhiteSpace(model.PortalText)
            ? string.Empty
            : $"<a class='omp-error-view__button omp-error-view__button--primary' href='{safePortalHref}'>{safePortalText}</a>";
        var safeCulture = WebUtility.HtmlEncode(CultureInfo.CurrentUICulture.Name);

        return $$"""
<!doctype html>
<html lang="{{safeCulture}}">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{{safeTitle}} - OMP</title>
  <style>
    :root { color-scheme: light dark; }
    body { margin: 0; font-family: Arial, Helvetica, sans-serif; background: #f5f7fa; color: #16202a; }
    .omp-error-fallback { min-height: 100vh; display: grid; place-items: center; padding: 2rem; }
    .omp-error-view { width: min(46rem, 100%); margin: 0 auto; padding: 2rem; border: 1px solid #d7dde5; border-radius: 18px; background: #fff; box-shadow: 0 12px 36px rgba(15, 23, 42, 0.08); }
    .omp-error-view__code { display: inline-block; margin-bottom: 1rem; font-size: 0.875rem; font-weight: 700; letter-spacing: 0.08em; text-transform: uppercase; color: #3559e0; }
    .omp-error-view__title { margin: 0 0 0.75rem; font-size: 1.9rem; line-height: 1.2; }
    .omp-error-view__message { margin: 0 0 1rem; line-height: 1.6; color: #223044; }
    .omp-error-view__detail { margin: 0; color: #4b5563; word-break: break-word; }
    .omp-error-view__detail code { display: inline-block; margin-top: 0.35rem; padding: 0.2rem 0.45rem; border-radius: 6px; background: #eef2ff; color: #1f2937; }
    .omp-error-view__actions { display: flex; flex-wrap: wrap; gap: 0.75rem; margin-top: 1.5rem; }
    .omp-error-view__button { display: inline-flex; align-items: center; justify-content: center; min-height: 2.75rem; padding: 0.75rem 1rem; border: 1px solid transparent; border-radius: 999px; font: inherit; font-weight: 600; text-decoration: none; cursor: pointer; }
    .omp-error-view__button--primary { background: #3559e0; border-color: #3559e0; color: #fff; }
    .omp-error-view__button--primary:hover, .omp-error-view__button--primary:focus, .omp-error-view__button--primary:focus-visible { background: #2948bc; border-color: #2948bc; color: #fff; text-decoration: none; }
    .omp-error-view__button--secondary { background: #fff; border-color: #c8d1dc; color: #223044; }
  </style>
</head>
<body>
  <main class="omp-error-fallback">
    <section class="omp-error-view">
      <div class="omp-error-view__code">{{model.StatusCode}}</div>
      <h1 class="omp-error-view__title">{{safeTitle}}</h1>
      <p class="omp-error-view__message">{{safeMessage}}</p>
      {{requestedUrlMarkup}}
      <div class="omp-error-view__actions">
        {{backButtonMarkup}}
        {{appHomeMarkup}}
        {{portalMarkup}}
      </div>
    </section>
  </main>
</body>
</html>
""";
    }

    private static bool TryParseCidrNetwork(
        string? cidr,
        [NotNullWhen(true)] out SystemNetIPNetwork? network)
    {
        network = null;

        if (string.IsNullOrWhiteSpace(cidr))
        {
            return false;
        }

        var parts = cidr.Trim().Split(
            '/',
            2,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 2)
        {
            return false;
        }

        if (!IPAddress.TryParse(parts[0], out var prefix))
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        var maxBits = prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? 32
            : 128;

        if (prefixLength < 0 || prefixLength > maxBits)
        {
            return false;
        }

        network = new SystemNetIPNetwork(prefix, prefixLength);
        return true;
    }
}
