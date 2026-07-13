using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Hosting;
using OpenModulePlatform.HostAgent.Runtime.Models;
using OpenModulePlatform.HostAgent.Runtime.Services;
using OpenModulePlatform.HostAgent.WindowsService.Services;

var runOnce = args.Any(static arg => string.Equals(arg, "--run-once", StringComparison.OrdinalIgnoreCase));
var startupServiceName = GetArgumentValue(args, "--service-name");
var hostArgs = args
    .Where(static arg => !string.Equals(arg, "--run-once", StringComparison.OrdinalIgnoreCase))
    .Where(static arg => !arg.StartsWith("--service-name=", StringComparison.OrdinalIgnoreCase))
    .Where(static arg => !arg.StartsWith("--runtime-mode=", StringComparison.OrdinalIgnoreCase))
    .Where(static arg => !arg.StartsWith("--takeover-from=", StringComparison.OrdinalIgnoreCase))
    .Where(static arg => !string.Equals(arg, "--takeover", StringComparison.OrdinalIgnoreCase))
    .ToArray();

var builder = Host.CreateDefaultBuilder(hostArgs)
    .UseWindowsService(options =>
    {
        options.ServiceName = string.IsNullOrWhiteSpace(startupServiceName)
            ? "OMP.HostAgent"
            : startupServiceName.Trim();
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .UseNLog()
    .ConfigureServices((context, services) =>
    {
        services.Configure<HostAgentSettings>(context.Configuration.GetSection("HostAgent"));
        services.AddSingleton(_ => CreateProcessContext(args, context.Configuration.GetSection("HostAgent").Get<HostAgentSettings>()));
        services.AddSingleton<SqlConnectionFactory>();
        services.AddSingleton<ISqlConnectionFactory>(static sp => sp.GetRequiredService<SqlConnectionFactory>());
        services.AddSingleton<OmpHostArtifactRepository>();
        services.AddSingleton<IOmpHostArtifactRepository>(static sp => sp.GetRequiredService<OmpHostArtifactRepository>());
        services.AddSingleton<ArtifactProvisioner>();
        services.AddSingleton<ArtifactZipImportService>();
        services.AddSingleton<WebAppDeploymentService>();
        services.AddSingleton<ServiceAppDeploymentService>();
        services.AddSingleton<HostAgentSelfUpgradeService>();
        services.AddSingleton<HostAgentFileMirrorService>();
        services.AddHttpClient(WebAppHealthMonitor.PortalHealthHttpClientName);
        services.AddHttpClient(WebAppHealthMonitor.PortalHealthAllowInvalidTlsHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            });
        services.AddSingleton<WebAppHealthMonitor>();
        services.AddSingleton<HostResourceCollector>();
        services.AddSingleton<HostAgentJobProcessor>();
        services.AddSingleton<HostAgentCredentialStoreService>();
        services.AddSingleton<DeploySetConsistencyService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<HostAgentEngine>();
        services.AddHostedService<HostAgentHostedService>();
        if (OperatingSystem.IsWindows())
        {
            services.AddHostedService<HostAgentRpcHostedService>();
        }
    });

try
{
    using var host = builder.Build();
    if (runOnce)
    {
        var engine = host.Services.GetRequiredService<HostAgentEngine>();
        try
        {
            await engine.RunOnceAsync(CancellationToken.None);
        }
        finally
        {
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await engine.ShutdownAsync(shutdownCts.Token);
        }

        return;
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    WriteEmergencyStartupFailure(ex);
    throw;
}

static HostAgentProcessContext CreateProcessContext(string[] args, HostAgentSettings? settings)
{
    var serviceName = GetArgumentValue(args, "--service-name")
        ?? settings?.ServiceName
        ?? "OMP.HostAgent";
    var version = settings?.Version ?? string.Empty;
    var runtimeMode = GetArgumentValue(args, "--runtime-mode")
        ?? (args.Any(static arg => string.Equals(arg, "--takeover", StringComparison.OrdinalIgnoreCase))
            ? HostAgentRuntimeMode.Takeover
            : settings?.RuntimeMode)
        ?? HostAgentRuntimeMode.Normal;
    var takeoverFrom = GetArgumentValue(args, "--takeover-from")
        ?? settings?.TakeoverFromServiceName;

    return new HostAgentProcessContext(serviceName, version, runtimeMode, takeoverFrom);
}

static string? GetArgumentValue(string[] args, string name)
{
    var prefix = name + "=";
    var match = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return match is null ? null : match[prefix.Length..];
}

static void WriteEmergencyStartupFailure(Exception exception)
{
    try
    {
        var logDirectory = Path.Join(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Join(
            logDirectory,
            $"hostagent-startup-error-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.log");

        File.WriteAllText(
            logPath,
            $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{exception}",
            System.Text.Encoding.UTF8);
    }
    catch
    {
        // Last-resort diagnostic logging must never hide the original startup failure.
    }
}
