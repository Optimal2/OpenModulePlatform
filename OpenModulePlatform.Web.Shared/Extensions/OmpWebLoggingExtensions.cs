using Microsoft.AspNetCore.Builder;
using NLog.Web;
using Microsoft.Extensions.Logging;

namespace OpenModulePlatform.Web.Shared.Extensions;

/// <summary>
/// Configures the shared logging conventions used by OMP web hosts.
/// </summary>
public static class OmpWebLoggingExtensions
{
    public static WebApplicationBuilder AddOmpWebLogging(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Host.UseNLog(new NLogAspNetCoreOptions
        {
            RemoveLoggerFactoryFilter = true,
            ShutdownOnDispose = true,
            RegisterHttpContextAccessor = true
        });

        return builder;
    }
}
