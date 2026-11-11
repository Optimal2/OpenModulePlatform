using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenModulePlatform.EventPublisher;
using OpenModulePlatform.Portal.Tests.Services;
using OpenModulePlatform.TestSupport;
using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Navigation;
using OpenModulePlatform.Web.Shared.Notifications;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Runtime;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;
using OpenModulePlatform.Web.Shared.Telemetry;

namespace OpenModulePlatform.Portal.Tests.Navigation;

public sealed class PortalTopBarRuntimeAdvisoryBuildTests
    : IClassFixture<PortalTopBarRuntimeAdvisoryBuildTestFixture>
{
    private readonly PortalTopBarRuntimeAdvisoryBuildTestFixture _fixture;

    public PortalTopBarRuntimeAdvisoryBuildTests(PortalTopBarRuntimeAdvisoryBuildTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateAsync_AdminOutdated_AppendsOneWarningAfterDatabaseBanners()
    {
        var result = await BuildTopBarAsync(isPortalAdmin: true);

        Assert.True(result.Model.IsPortalAdmin);
        Assert.Equal(2, result.Model.Banners.Count);
        Assert.Equal(result.DatabaseBannerId, result.Model.Banners[0].BannerId);
        Assert.Equal("Database banner", result.Model.Banners[0].Title);

        var warning = result.Model.Banners[1];
        Assert.Equal(0, warning.BannerId);
        Assert.Equal(BannerService.LevelWarning, warning.Level);
        Assert.Equal("warning", warning.LevelName);
        Assert.Contains("10.0.14", warning.Content, StringComparison.Ordinal);
        Assert.Contains("10.0.15", warning.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_NonAdminOutdated_KeepsOnlyDatabaseBanners()
    {
        var result = await BuildTopBarAsync(isPortalAdmin: false);

        Assert.False(result.Model.IsPortalAdmin);
        var databaseBanner = Assert.Single(result.Model.Banners);
        Assert.Equal(result.DatabaseBannerId, databaseBanner.BannerId);
        Assert.Equal("Database banner", databaseBanner.Title);
        Assert.DoesNotContain(result.Model.Banners, banner => banner.BannerId == 0);
    }

    private async Task<(PortalTopBarModel Model, long DatabaseBannerId)> BuildTopBarAsync(bool isPortalAdmin)
    {
        await _fixture.ResetDatabaseBannersAsync();
        await using var provider = CreateServices();
        await using var scope = provider.CreateAsyncScope();

        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, isPortalAdmin ? "portal-admin" : "portal-user")],
            authenticationType: "test"));
        var httpContext = new DefaultHttpContext { User = user };
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("portal.example.test");
        httpContext.Items["OMP.RbacService.RoleContext"] = new UserRoleContext
        {
            EffectivePermissions = isPortalAdmin
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PortalAdminNavigation.AdminPermission }
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;

        var databaseBannerId = await scope.ServiceProvider.GetRequiredService<BannerService>().CreateAsync(
            new BannerCreateRequest(
                "Database banner",
                "Database content",
                BannerService.StatusActive,
                BannerService.LevelAnnouncement),
            [new BannerTargetRequest(BannerService.TargetGlobal, null)],
            CancellationToken.None);

        var options = new WebAppOptions
        {
            DefaultCulture = "en-US",
            SupportedCultures = ["en-US"],
            PortalTopBar = new PortalTopBarOptions { Enabled = true },
            TopBarPolling = new TopBarPollingOptions { Enabled = false }
        };
        var model = await scope.ServiceProvider.GetRequiredService<PortalTopBarService>().CreateAsync(
            options,
            httpContext.Request,
            user,
            CancellationToken.None);

        return (model, databaseBannerId);
    }

    private ServiceProvider CreateServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OmpDb"] = _fixture.ConnectionString
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        services.Configure<OmpAuthOptions>(_ => { });
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<SqlConnectionFactory>();
        services.AddScoped<OmpConfigurationService>();
        services.AddScoped<RbacService>();
        services.AddScoped<OmpBrandingService>();
        services.AddScoped<CultureSelectionService>();
        services.AddSingleton<ITopBarNotificationStatePublisher, NoOpTopBarNotificationStatePublisher>();
        services.AddSingleton<IPushEventPublisher, NoOpPushEventPublisher>();
        services.AddScoped<NotificationService>();
        services.AddScoped<MessageService>();
        services.AddScoped<BannerService>();
        services.AddSingleton(new OmpPerformanceTelemetryOptions { Enabled = false });
        services.AddSingleton<OmpPerformanceTelemetry>();
        services.AddSingleton<IOmpRuntimeAdvisorySource>(
            new PortalTopBarRuntimeAdvisoryTests.StubRuntimeAdvisorySource(
                () => OmpRuntimeAdvisory.Compare(new Version(10, 0, 14), "10.0.15")));
        services.AddScoped<PortalTopBarService>();
        return services.BuildServiceProvider();
    }

    private sealed class NoOpPushEventPublisher : IPushEventPublisher
    {
        public Task<long> PublishAsync(PushEvent pushEvent, CancellationToken ct) => Task.FromResult(0L);
    }

    private sealed class NoOpTopBarNotificationStatePublisher : ITopBarNotificationStatePublisher
    {
        public Task NotifyChangedAsync(int userId, CancellationToken ct) => Task.CompletedTask;

        public Task NotifyChangedAsync(int userId, int? unreadCount, CancellationToken ct) => Task.CompletedTask;
    }
}

public sealed class PortalTopBarRuntimeAdvisoryBuildTestFixture : IAsyncLifetime
{
    public static readonly string DatabaseName = OmpTestDatabaseNames.ForPortalTests("TopBarRuntimeAdvisory");

    public string ConnectionString { get; } = TestSqlConnection.ForDatabase(DatabaseName);

    public async Task InitializeAsync()
    {
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await OmpTestDatabaseProvisioner.CreateDatabaseAsync(
            master.ConnectionString,
            $"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}];");
        await CoreSetupScript.ApplyAsync(ConnectionString);
    }

    public async Task ResetDatabaseBannersAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "DELETE FROM omp.banner_targets; DELETE FROM omp.banners;",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        try
        {
            await using var connection = new SqlConnection(master.ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];",
                connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            OmpTestCleanupLog.RecordFailure(
                nameof(PortalTopBarRuntimeAdvisoryBuildTests),
                $"Could not drop test database '{DatabaseName}': {ex.Message}");
        }
    }
}
