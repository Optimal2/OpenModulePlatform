using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContextGuardModule.Options;

namespace OpenModulePlatform.Web.ContextGuardModule.Hosting;

public static class ContextGuardCspExtensions
{
    public static WebApplication UseContextGuardCsp(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var options = context.RequestServices.GetRequiredService<IOptions<ContextGuardOptions>>().Value;
            var origins = options.Profiles.Values
                .Select(profile => Uri.TryCreate(profile.TargetUrl, UriKind.Absolute, out var uri)
                    ? uri.GetLeftPart(UriPartial.Authority)
                    : null)
                .Where(origin => !string.IsNullOrWhiteSpace(origin))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var targets = string.Join(' ', origins);
            var suffix = string.IsNullOrWhiteSpace(targets) ? string.Empty : " " + targets;
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; " +
                $"base-uri 'self'; frame-ancestors 'self'; form-action 'self'{suffix}; frame-src 'self'{suffix}";
            await next();
        });
        return app;
    }
}
