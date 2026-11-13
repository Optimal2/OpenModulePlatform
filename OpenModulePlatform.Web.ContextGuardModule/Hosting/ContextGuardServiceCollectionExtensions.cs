using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Web.ContextGuardModule.Options;
using OpenModulePlatform.Web.ContextGuardModule.Services;
using OpenModulePlatform.Web.ContextGuardModule.Stores;

namespace OpenModulePlatform.Web.ContextGuardModule.Hosting;

public static class ContextGuardServiceCollectionExtensions
{
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
