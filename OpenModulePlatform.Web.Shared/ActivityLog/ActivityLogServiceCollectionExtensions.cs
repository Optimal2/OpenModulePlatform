// File: OpenModulePlatform.Web.Shared/ActivityLog/ActivityLogServiceCollectionExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Web.Shared.ActivityLog;

public static class ActivityLogServiceCollectionExtensions
{
    /// <summary>
    /// Registers the module's activity log writer. Call once from the web app's
    /// Program.cs with the module's schema, module key and this app's component key;
    /// page models then take <see cref="ActivityLogWriter"/> by injection.
    /// </summary>
    public static IServiceCollection AddOmpActivityLog(this IServiceCollection services, ActivityLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.SchemaName) || string.IsNullOrWhiteSpace(options.ModuleKey) || string.IsNullOrWhiteSpace(options.AppKey))
        {
            throw new ArgumentException("SchemaName, ModuleKey and AppKey are all required.", nameof(options));
        }

        services.TryAddSingleton<SqlConnectionFactory>();
        services.TryAddSingleton(options);
        services.TryAddSingleton<ActivityLogWriter>();
        return services;
    }
}
