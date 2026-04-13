// File: OpenModulePlatform.Web.Shared/Extensions/OmpWebHostingExtensions.cs
using OpenModulePlatform.Web.Shared.Localization;
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
using AspNetIPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

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

        app.MapGet("/status/{statusCode:int}", (
            HttpContext context,
            int statusCode,
            IStringLocalizer<SharedResource> localizer) =>
        {
            var feature = context.Features.Get<IStatusCodeReExecuteFeature>();
            var originalPath = BuildOriginalPath(feature);
            var portalHref = PortalTopBarModelFactory.CombinePortalHref(options.PortalTopBar.PortalBaseUrl, "/");
            var html = BuildStatusPageHtml(statusCode, originalPath, portalHref, localizer);

            return Results.Content(
                html,
                contentType: "text/html; charset=utf-8",
                contentEncoding: System.Text.Encoding.UTF8,
                statusCode: statusCode);
        }).AllowAnonymous();

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

        app.MapGet("/rbac/set-active-role", async (HttpContext context, int? roleId, string? returnUrl, RbacService rbac, CancellationToken ct) =>
        {
            var roleContext = await rbac.GetUserRoleContextAsync(context.User, ct);
            var validRoleIds = roleContext.AvailableRoles.Select(x => x.RoleId).ToHashSet();
            var roleChanged = roleId is int requestedRoleId && requestedRoleId != roleContext.ActiveRoleId;

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

            var safePortalHref = PortalTopBarModelFactory.CombinePortalHref(options.PortalTopBar.PortalBaseUrl, "/");

            if (roleChanged)
            {
                if (Uri.IsWellFormedUriString(safePortalHref, UriKind.Absolute))
                {
                    return Results.Redirect(safePortalHref);
                }

                return Results.LocalRedirect(safePortalHref);
            }

            if (!string.IsNullOrWhiteSpace(returnUrl) && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative))
            {
                return Results.LocalRedirect(returnUrl);
            }

            if (Uri.IsWellFormedUriString(safePortalHref, UriKind.Absolute))
            {
                return Results.Redirect(safePortalHref);
            }

            return Results.LocalRedirect(safePortalHref);
        });

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
            options.KnownNetworks.Clear();
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
            options.KnownNetworks.Clear();

            foreach (var cidr in webAppOptions.ForwardedHeadersKnownNetworks
                         .Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                if (TryParseCidrNetwork(cidr, out var network))
                {
                    options.KnownNetworks.Add(network);
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

    private static string BuildOriginalPath(IStatusCodeReExecuteFeature? feature)
    {
        if (feature is null)
        {
            return string.Empty;
        }

        return string.Concat(
            feature.OriginalPathBase,
            feature.OriginalPath,
            feature.OriginalQueryString);
    }

    private static string BuildStatusPageHtml(
        int statusCode,
        string originalPath,
        string portalHref,
        IStringLocalizer<SharedResource> localizer)
    {
        var title = statusCode switch
        {
            StatusCodes.Status403Forbidden => localizer["StatusPageTitle403"],
            StatusCodes.Status404NotFound => localizer["StatusPageTitle404"],
            _ => localizer["StatusPageTitleDefault"]
        };

        var heading = statusCode switch
        {
            StatusCodes.Status403Forbidden => localizer["StatusPageHeading403"],
            StatusCodes.Status404NotFound => localizer["StatusPageHeading404"],
            _ => localizer["StatusPageHeadingDefault"]
        };

        var message = statusCode switch
        {
            StatusCodes.Status403Forbidden => localizer["StatusPageMessage403"],
            StatusCodes.Status404NotFound => localizer["StatusPageMessage404"],
            _ => localizer["StatusPageMessageDefault"]
        };

        var safeTitle = WebUtility.HtmlEncode(title.Value);
        var safeHeading = WebUtility.HtmlEncode(heading.Value);
        var safeMessage = WebUtility.HtmlEncode(message.Value);
        var safePortalHref = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(portalHref) ? "/" : portalHref);
        var safeBackText = WebUtility.HtmlEncode(localizer["StatusPageBackToPortal"].Value);
        var safePathLabel = WebUtility.HtmlEncode(localizer["StatusPageOriginalPath"].Value);
        var safeOriginalPath = WebUtility.HtmlEncode(originalPath);
        var safeCulture = WebUtility.HtmlEncode(CultureInfo.CurrentUICulture.Name);

        var originalPathMarkup = string.IsNullOrWhiteSpace(originalPath)
            ? string.Empty
            : $"<p class='omp-status-detail'><strong>{safePathLabel}:</strong> <code>{safeOriginalPath}</code></p>";

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
    .omp-status-shell { min-height: 100vh; display: grid; place-items: center; padding: 2rem; }
    .omp-status-card { width: min(42rem, 100%); background: #fff; border: 1px solid #d7dde5; border-radius: 16px; box-shadow: 0 12px 36px rgba(15, 23, 42, 0.08); padding: 2rem; }
    .omp-status-code { display: inline-block; font-size: 0.875rem; font-weight: 700; letter-spacing: 0.08em; text-transform: uppercase; color: #3559e0; margin-bottom: 1rem; }
    h1 { margin: 0 0 0.75rem; font-size: 1.75rem; line-height: 1.2; }
    p { margin: 0 0 1rem; line-height: 1.6; }
    .omp-status-detail { color: #4b5563; word-break: break-word; }
    code { display: inline-block; padding: 0.125rem 0.375rem; border-radius: 6px; background: #eef2ff; color: #1f2937; }
    .omp-status-actions { margin-top: 1.5rem; }
    .omp-status-button { display: inline-flex; align-items: center; justify-content: center; padding: 0.75rem 1rem; border-radius: 999px; background: #3559e0; color: #fff; text-decoration: none; font-weight: 600; }
    .omp-status-button:hover { background: #2948bc; }
    @media (prefers-color-scheme: dark) {
      body { background: #0f172a; color: #e5e7eb; }
      .omp-status-card { background: #111827; border-color: #374151; box-shadow: none; }
      .omp-status-code { color: #93c5fd; }
      .omp-status-detail { color: #cbd5e1; }
      code { background: #1f2937; color: #e5e7eb; }
      .omp-status-button { background: #60a5fa; color: #0f172a; }
      .omp-status-button:hover { background: #93c5fd; }
    }
  </style>
</head>
<body>
  <main class="omp-status-shell">
    <section class="omp-status-card">
      <div class="omp-status-code">{{statusCode}}</div>
      <h1>{{safeHeading}}</h1>
      <p>{{safeMessage}}</p>
      {{originalPathMarkup}}
      <div class="omp-status-actions">
        <a class="omp-status-button" href="{{safePortalHref}}">{{safeBackText}}</a>
      </div>
    </section>
  </main>
</body>
</html>
""";
    }

    private static bool TryParseCidrNetwork(
        string? cidr,
        [NotNullWhen(true)] out AspNetIPNetwork? network)
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

        network = new AspNetIPNetwork(prefix, prefixLength);
        return true;
    }
}
