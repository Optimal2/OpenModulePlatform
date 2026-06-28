using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Portal.Tests.Integration;

/// <summary>
/// Factory that hosts the Portal application for integration tests using the
/// in-process ASP.NET Core TestServer. It overrides the database connection string
/// and switches authentication to the test handler so the SignalR client can connect
/// with a known OMP user principal.
/// </summary>
public sealed class PortalWebApplicationFactory : WebApplicationFactory<PortalResource>
{
    private readonly PushEventPipelineTestFixture _fixture;

    public PortalWebApplicationFactory(PushEventPipelineTestFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OmpDb"] = _fixture.ConnectionString,
                ["PushEvents:Dispatcher:Enabled"] = "true",
                ["PushEvents:Dispatcher:PollingIntervalSeconds"] = "1",
                ["PushEvents:Dispatcher:LeaseSeconds"] = "5",
                ["PushEvents:Dispatcher:CleanupEnabled"] = "false",
                ["PushEvents:Producers:UseOutboxForNotificationStateChanges"] = "true"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                options.DefaultScheme = TestAuthHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName,
                _ => { });
        });
    }
}
