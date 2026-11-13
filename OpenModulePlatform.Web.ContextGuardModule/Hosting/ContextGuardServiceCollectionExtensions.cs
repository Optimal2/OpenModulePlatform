using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContextGuardModule.Options;
using OpenModulePlatform.Web.ContextGuardModule.Services;
using OpenModulePlatform.Web.ContextGuardModule.Stores;

namespace OpenModulePlatform.Web.ContextGuardModule.Hosting;

public static class ContextGuardServiceCollectionExtensions
{
    public const string ReceiveRateLimitPolicy = "context-guard-receive";
    public const string EndRateLimitPolicy = "context-guard-end";

    public static IServiceCollection AddContextGuardModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<ContextGuardOptions>, ContextGuardOptionsValidator>();
        services.AddOptions<ContextGuardOptions>()
            .Bind(configuration.GetSection(ContextGuardOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IHandoffStore, InMemoryHandoffStore>();
        services.AddSingleton<IContextStore, InMemoryContextStore>();
        services.AddSingleton<ContextGuardFormReader>();
        services.AddSingleton<ContextGuardService>();
        var guardOptions = configuration.GetSection(ContextGuardOptions.SectionName)
            .Get<ContextGuardOptions>() ?? new ContextGuardOptions();
        services.AddRateLimiter(configure =>
        {
            configure.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            configure.AddPolicy(ReceiveRateLimitPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = guardOptions.ReceiveMaxRequestsPerWindow,
                        Window = TimeSpan.FromSeconds(guardOptions.AnonymousRateLimitWindowSeconds),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
            configure.AddPolicy(EndRateLimitPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = guardOptions.EndMaxRequestsPerWindow,
                        Window = TimeSpan.FromSeconds(guardOptions.AnonymousRateLimitWindowSeconds),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
        });
        services.Configure<FormOptions>(form =>
        {
            var options = configuration.GetSection(ContextGuardOptions.SectionName)
                .Get<ContextGuardOptions>() ?? new ContextGuardOptions();
            form.ValueCountLimit = options.MaxFieldCount;
            form.MultipartBodyLengthLimit = options.MaxTotalFormBytes;
            form.ValueLengthLimit = options.MaxFieldValueLength;
        });
        return services;
    }
}
