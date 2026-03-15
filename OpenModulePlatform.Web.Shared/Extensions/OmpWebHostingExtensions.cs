// File: OpenModulePlatform.Web.Shared/Extensions/OmpWebHostingExtensions.cs
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Services;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using AspNetIPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace OpenModulePlatform.Web.Shared.Extensions;

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
            builder.Services.AddAuthentication();
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

        var webAppOptions = builder.Configuration.GetSection(optionsSectionName).Get<WebAppOptions>() ?? new WebAppOptions();
        builder.Services.Configure<ForwardedHeadersOptions>(options => ConfigureForwardedHeaders(options, webAppOptions));

        builder.Services.AddSingleton<SqlConnectionFactory>();
        builder.Services.AddScoped<RbacService>();

        return builder;
    }

    public static WebApplication UseOmpWebDefaults(
        this WebApplication app,
        string optionsSectionName = WebAppOptions.DefaultSectionName,
        bool mapRazorPages = true)
    {
        var options = app.Configuration.GetSection(optionsSectionName).Get<WebAppOptions>() ?? new WebAppOptions();

        if (options.UseForwardedHeaders)
            app.UseForwardedHeaders();

        app.UseStaticFiles();

        if (!options.AllowAnonymous)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        if (mapRazorPages)
            app.MapRazorPages();

        return app;
    }

    private static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, WebAppOptions webAppOptions)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

        if (webAppOptions.ForwardedHeadersTrustAllProxies)
        {
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
            return;
        }

        if (webAppOptions.ForwardedHeadersKnownProxies.Length > 0)
        {
            options.KnownProxies.Clear();
            foreach (var ipText in webAppOptions.ForwardedHeadersKnownProxies)
            {
                if (IPAddress.TryParse(ipText?.Trim(), out var ip))
                    options.KnownProxies.Add(ip);
            }
        }

        if (webAppOptions.ForwardedHeadersKnownNetworks.Length > 0)
        {
            options.KnownNetworks.Clear();
            foreach (var cidr in webAppOptions.ForwardedHeadersKnownNetworks)
            {
                if (TryParseCidrNetwork(cidr, out var network))
                    options.KnownNetworks.Add(network);
            }
        }
    }

    private static bool TryParseCidrNetwork(string? cidr, [NotNullWhen(true)] out AspNetIPNetwork? network)
    {
        network = null;

        if (string.IsNullOrWhiteSpace(cidr))
            return false;

        var parts = cidr.Trim().Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        if (!IPAddress.TryParse(parts[0], out var prefix))
            return false;

        if (!int.TryParse(parts[1], out var prefixLength))
            return false;

        var maxBits = prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxBits)
            return false;

        network = new AspNetIPNetwork(prefix, prefixLength);
        return true;
    }
}
