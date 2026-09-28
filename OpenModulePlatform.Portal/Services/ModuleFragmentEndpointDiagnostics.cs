using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Options;
using System.Collections.Concurrent;

namespace OpenModulePlatform.Portal.Services;

/// <summary>
/// Logs one actionable warning per process and endpoint issue. The configured URL is
/// never logged, so user information in it cannot reach the log.
/// </summary>
public sealed class ModuleFragmentEndpointDiagnostics
{
    private readonly ConcurrentDictionary<ModuleFragmentEndpointIssue, byte> _warned = new();
    private readonly ILogger<ModuleFragmentEndpointDiagnostics> _logger;

    public ModuleFragmentEndpointDiagnostics(ILogger<ModuleFragmentEndpointDiagnostics> logger)
    {
        _logger = logger;
    }

    public void Report(ModuleFragmentEndpointIssue issue)
    {
        if (issue == ModuleFragmentEndpointIssue.None || !_warned.TryAdd(issue, 0))
        {
            return;
        }

        switch (issue)
        {
            case ModuleFragmentEndpointIssue.InvalidInternalBaseUrl:
                _logger.LogWarning(
                    "Module-fragment dashboard widgets are disabled: configuration key {ConfigurationKey} is not an absolute HTTP or HTTPS URL. Correct the value, or remove it to use the default fragment routing.",
                    ModuleFragmentEndpointCheck.InternalBaseUrlKey);
                break;
            case ModuleFragmentEndpointIssue.InternalBaseUrlUserInfo:
                _logger.LogWarning(
                    "Module-fragment dashboard widgets are disabled: configuration key {ConfigurationKey} contains user information (username or password), which is never sent. Remove the user information from the URL.",
                    ModuleFragmentEndpointCheck.InternalBaseUrlKey);
                break;
            case ModuleFragmentEndpointIssue.InsecureInternalBaseUrlForHttpsRequest:
                _logger.LogWarning(
                    "Module-fragment dashboard widgets are blocked for HTTPS requests: configuration key {ConfigurationKey} uses HTTP and would send identity cookies without TLS. Change it to an HTTPS address, or set {AllowInsecureKey}=true if a TLS-terminating proxy makes this HTTP hop intentional.",
                    ModuleFragmentEndpointCheck.InternalBaseUrlKey,
                    ModuleFragmentEndpointCheck.AllowInsecureInternalBaseUrlKey);
                break;
        }
    }
}

/// <summary>
/// Reports request-independent endpoint issues once at startup, so an unusable
/// InternalBaseUrl is visible before the first dashboard load.
/// </summary>
internal sealed class ModuleFragmentEndpointStartupCheck : IHostedService
{
    private readonly IOptionsMonitor<ModuleFragmentWidgetOptions> _options;
    private readonly ModuleFragmentEndpointDiagnostics _diagnostics;

    public ModuleFragmentEndpointStartupCheck(
        IOptionsMonitor<ModuleFragmentWidgetOptions> options,
        ModuleFragmentEndpointDiagnostics diagnostics)
    {
        _options = options;
        _diagnostics = diagnostics;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _diagnostics.Report(ModuleFragmentEndpointCheck.CheckConfiguration(_options.CurrentValue));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class ModuleFragmentWidgetServiceCollectionExtensions
{
    /// <summary>
    /// Registers the module-fragment options without start validation: an unusable
    /// InternalBaseUrl disables only the module-fragment widgets (warning plus the
    /// Maintenance readiness check) instead of refusing to start the whole Portal.
    /// </summary>
    public static IServiceCollection AddModuleFragmentWidgetOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ModuleFragmentWidgetOptions>()
            .Bind(configuration.GetSection(ModuleFragmentWidgetOptions.SectionName));
        services.AddSingleton<ModuleFragmentEndpointDiagnostics>();
        services.AddHostedService<ModuleFragmentEndpointStartupCheck>();
        return services;
    }
}
