// File: OpenModulePlatform.Web.Shared/Extensions/OmpWebHostingExtensions.cs
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.IISIntegration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
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
    public static WebApplicationBuilder AddOmpWebDefaults(
        this WebApplicationBuilder builder,
        string optionsSectionName = WebAppOptions.DefaultSectionName)
    {
        builder.Services.AddOptions<WebAppOptions>()
            .Bind(builder.Configuration.GetSection(optionsSectionName));

        builder.Services.AddRazorPages();

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

        builder.Services.AddOptions<ForwardedHeadersOptions>()
            .Configure<ILoggerFactory>((options, loggerFactory) =>
            {
                var logger = loggerFactory.CreateLogger(
                    "OpenModulePlatform.Web.Shared.ForwardedHeaders");

                ConfigureForwardedHeaders(options, webAppOptions, logger);
            });

        builder.Services.AddSingleton<SqlConnectionFactory>();
        builder.Services.AddScoped<RbacService>();

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

        app.UseStaticFiles();

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
