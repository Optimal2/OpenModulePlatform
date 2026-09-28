using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Portal.Options;
using OpenModulePlatform.Portal.Services;

namespace OpenModulePlatform.Portal.Tests.Services;

public sealed class ModuleFragmentWidgetOptionsTests
{
    [Theory]
    [InlineData("http://user@module.example")]
    [InlineData("https://user:private-password@module.example")]
    [InlineData(" https://user%40name:private-password@module.example/path ")]
    public void UserInfo_IsRejectedEvenWithInsecureOptIn(string url)
    {
        var options = new ModuleFragmentWidgetOptions { InternalBaseUrl = url, AllowInsecureInternalBaseUrl = true };

        Assert.Equal(ModuleFragmentEndpointIssue.InternalBaseUrlUserInfo,
            ModuleFragmentEndpointCheck.Check(options, requestIsHttps: true, out var internalBase));
        Assert.Null(internalBase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("http://127.0.0.1:5000/path")]
    [InlineData(" https://module.example/path ")]
    public void SafeOrUnsetInternalBaseUrl_IsAccepted(string? url)
    {
        var options = new ModuleFragmentWidgetOptions { InternalBaseUrl = url };

        Assert.False(options.AllowInsecureInternalBaseUrl);
        Assert.Equal(ModuleFragmentEndpointIssue.None, ModuleFragmentEndpointCheck.CheckConfiguration(options));
    }

    [Theory]
    [InlineData("/relative")]
    [InlineData("ftp://module.example")]
    [InlineData("not a URL")]
    public void InvalidInternalBaseUrl_IsRejected(string url)
    {
        Assert.Equal(ModuleFragmentEndpointIssue.InvalidInternalBaseUrl,
            ModuleFragmentEndpointCheck.CheckConfiguration(new ModuleFragmentWidgetOptions { InternalBaseUrl = url }));
    }

    [Theory]
    [InlineData(false, false, ModuleFragmentEndpointIssue.None)]
    [InlineData(true, false, ModuleFragmentEndpointIssue.InsecureInternalBaseUrlForHttpsRequest)]
    [InlineData(true, true, ModuleFragmentEndpointIssue.None)]
    public void HttpInternalBaseUrl_IsBlockedForHttpsRequestsWithoutOptIn(
        bool requestIsHttps, bool allowInsecure, ModuleFragmentEndpointIssue expected)
    {
        var options = new ModuleFragmentWidgetOptions
        {
            InternalBaseUrl = "http://127.0.0.1:5000",
            AllowInsecureInternalBaseUrl = allowInsecure
        };

        Assert.Equal(expected, ModuleFragmentEndpointCheck.Check(options, requestIsHttps, out _));
    }

    [Theory]
    [InlineData("https://user:private-password@module.example", "contains user information")]
    [InlineData("ftp://module.example", "is not an absolute HTTP or HTTPS URL")]
    public async Task UnusableInternalBaseUrl_DoesNotPreventStartup_AndWarnsOnceWithoutCredentials(
        string url, string reason)
    {
        var logger = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logger));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ModuleFragmentEndpointCheck.InternalBaseUrlKey] = url })
            .Build();
        services.AddModuleFragmentWidgetOptions(configuration);
        using var provider = services.BuildServiceProvider();

        provider.GetService<IStartupValidator>()?.Validate();
        var options = provider.GetRequiredService<IOptionsMonitor<ModuleFragmentWidgetOptions>>().CurrentValue;
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }
        provider.GetRequiredService<ModuleFragmentEndpointDiagnostics>().Report(
            ModuleFragmentEndpointCheck.CheckConfiguration(options));

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(ModuleFragmentEndpointCheck.InternalBaseUrlKey, warning, StringComparison.Ordinal);
        Assert.Contains(reason, warning, StringComparison.Ordinal);
        Assert.Contains("disabled", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("private-password", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("module.example", warning, StringComparison.Ordinal);
    }

    internal sealed class CapturingLoggerProvider : ILoggerProvider, ILogger
    {
        public List<string> Warnings { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                lock (Warnings) { Warnings.Add(formatter(state, exception)); }
            }
        }
    }
}
