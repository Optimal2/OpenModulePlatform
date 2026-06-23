using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Portal.Tests.Security;

public sealed class OmpOidcAuthenticationRegistrationTests
{
    [Fact]
    public async Task AddOmpOidcAuthentication_WhenDisabled_DoesNotRegisterOidcScheme()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["OmpAuth:Oidc:Enabled"] = "false"
        });
        var services = CreateBaseServices(configuration);

        var status = services.AddOmpOidcAuthentication(configuration);

        using var provider = services.BuildServiceProvider();
        var schemeProvider = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.False(status.IsEnabled);
        Assert.Equal("OpenID Connect", status.DisplayName);
        Assert.False(provider.GetRequiredService<OmpOidcProviderStatus>().IsEnabled);
        Assert.NotNull(await schemeProvider.GetSchemeAsync(OmpAuthDefaults.AuthenticationScheme));
        Assert.Null(await schemeProvider.GetSchemeAsync(OmpAuthDefaults.OidcAuthenticationScheme));
    }

    [Fact]
    public async Task AddOmpOidcAuthentication_WhenRequiredSettingsAreMissing_HidesProvider()
    {
        var loggerProvider = new CapturingLoggerProvider();
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["OmpAuth:Oidc:Enabled"] = "true",
            ["OmpAuth:Oidc:DisplayName"] = "Local AD FS",
            ["OmpAuth:Oidc:ClientId"] = "omp-local-auth"
        });
        var services = CreateBaseServices(configuration, loggerProvider);

        var status = services.AddOmpOidcAuthentication(configuration);

        using var provider = services.BuildServiceProvider();
        var schemeProvider = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        var startupFilter = Assert.Single(provider.GetServices<IStartupFilter>());
        var configure = startupFilter.Configure(_ => { });
        configure(new ApplicationBuilder(provider));

        Assert.False(status.IsEnabled);
        Assert.Equal("Local AD FS", status.DisplayName);
        Assert.False(provider.GetRequiredService<OmpOidcProviderStatus>().IsEnabled);
        Assert.Null(await schemeProvider.GetSchemeAsync(OmpAuthDefaults.OidcAuthenticationScheme));
        Assert.Contains(
            loggerProvider.Entries,
            entry => entry.Level == LogLevel.Warning &&
                     entry.Category == "OpenModulePlatform.Auth.Oidc" &&
                     entry.Message.Contains(
                         "OIDC sign-in is disabled due to invalid configuration",
                         StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddOmpOidcAuthentication_WithLocalSimulatedConfig_RegistersSafeOidcOptions()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["OmpAuth:Oidc:Enabled"] = "true",
            ["OmpAuth:Oidc:DisplayName"] = "Local simulated AD FS",
            ["OmpAuth:Oidc:ProviderName"] = "ADFS",
            ["OmpAuth:Oidc:Authority"] = "https://idp.local.test/adfs",
            ["OmpAuth:Oidc:ClientId"] = "omp-local-auth",
            ["OmpAuth:Oidc:ClientSecret"] = "local-test-client-secret-placeholder",
            ["OmpAuth:Oidc:CallbackPath"] = "/signin-oidc",
            ["OmpAuth:Oidc:ResponseType"] = "code",
            ["OmpAuth:Oidc:Scopes:0"] = "openid",
            ["OmpAuth:Oidc:Scopes:1"] = "profile",
            ["OmpAuth:Oidc:Scopes:2"] = "allatclaims",
            ["OmpAuth:Oidc:ClaimTypes:NameClaimType"] = "upn",
            ["OmpAuth:Oidc:ClaimTypes:GroupsClaimType"] = "roles"
        });
        var services = CreateBaseServices(configuration);

        var status = services.AddOmpOidcAuthentication(configuration);

        using var provider = services.BuildServiceProvider();
        var schemeProvider = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        var options = provider
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OmpAuthDefaults.OidcAuthenticationScheme);

        Assert.True(status.IsEnabled);
        Assert.Equal("Local simulated AD FS", status.DisplayName);
        Assert.NotNull(await schemeProvider.GetSchemeAsync(OmpAuthDefaults.OidcAuthenticationScheme));
        Assert.Equal(OmpAuthDefaults.AuthenticationScheme, options.SignInScheme);
        Assert.Equal("https://idp.local.test/adfs", options.Authority);
        Assert.Equal("omp-local-auth", options.ClientId);
        Assert.Equal("/signin-oidc", options.CallbackPath.Value);
        Assert.Equal(OpenIdConnectResponseType.Code, options.ResponseType);
        Assert.True(options.UsePkce);
        Assert.False(options.SaveTokens);
        Assert.False(options.GetClaimsFromUserInfoEndpoint);
        Assert.False(options.MapInboundClaims);
        Assert.Contains("openid", options.Scope);
        Assert.Contains("profile", options.Scope);
        Assert.Contains("allatclaims", options.Scope);
        Assert.Equal("upn", options.TokenValidationParameters.NameClaimType);
        Assert.Equal("roles", options.TokenValidationParameters.RoleClaimType);
    }

    private static IServiceCollection CreateBaseServices(
        IConfiguration configuration,
        ILoggerProvider? loggerProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            if (loggerProvider is not null)
            {
                builder.AddProvider(loggerProvider);
            }
        });
        services.AddOmpCookieAuthentication(configuration);
        return services;
    }

    private static IConfiguration CreateConfiguration(IReadOnlyDictionary<string, string?> values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName)
            => new CapturingLogger(categoryName, Entries);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly List<LogEntry> _entries;

        public CapturingLogger(string category, List<LogEntry> entries)
        {
            _category = category;
            _entries = entries;
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add(new LogEntry(logLevel, _category, formatter(state, exception)));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed record LogEntry(LogLevel Level, string Category, string Message);
}
