using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using OpenModulePlatform.Web.Shared.Localization;
using OpenModulePlatform.Web.Shared.Navigation;
using OpenModulePlatform.Web.Shared.Runtime;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Navigation;

public sealed class PortalTopBarRuntimeAdvisoryTests
{
    [Fact]
    public void AdminOutdated_AddsExactlyOneWarningNamingBothVersions()
    {
        using var provider = CreateServices();
        using var culture = new CultureScope("en-US");

        var model = CreateModel(
            isPortalAdmin: true,
            OmpRuntimeAdvisory.Compare(new Version(10, 0, 14), "10.0.15"),
            provider.GetRequiredService<IStringLocalizer<SharedResource>>());

        var banner = Assert.Single(model.Banners);
        Assert.Equal(0, banner.BannerId);
        Assert.Equal(BannerService.LevelWarning, banner.Level);
        Assert.Equal("warning", banner.LevelName);
        Assert.Equal("Older .NET runtime on the server", banner.Title);
        Assert.Contains("10.0.14", banner.Content, StringComparison.Ordinal);
        Assert.Contains("10.0.15", banner.Content, StringComparison.Ordinal);
        Assert.Equal(
            "The server is running .NET 10.0.14. OMP is built for 10.0.15, which includes the latest security fixes. Update the ASP.NET Core Hosting Bundle on the server.",
            banner.Content);
    }

    [Fact]
    public void NonAdminOutdated_DoesNotAddRuntimeWarning()
    {
        using var provider = CreateServices();
        using var culture = new CultureScope("en-US");

        var model = CreateModel(
            isPortalAdmin: false,
            OmpRuntimeAdvisory.Compare(new Version(10, 0, 14), "10.0.15"),
            provider.GetRequiredService<IStringLocalizer<SharedResource>>());

        Assert.Empty(model.Banners);
    }

    [Fact]
    public void AdminWithCurrentRuntime_DoesNotAddRuntimeWarning()
    {
        using var provider = CreateServices();
        using var culture = new CultureScope("en-US");

        var model = CreateModel(
            isPortalAdmin: true,
            OmpRuntimeAdvisory.Compare(new Version(10, 0, 15), "10.0.15"),
            provider.GetRequiredService<IStringLocalizer<SharedResource>>());

        Assert.Empty(model.Banners);
    }

    [Fact]
    public void AdminOutdated_AppendsAfterAllThreeDatabaseBanners()
    {
        using var provider = CreateServices();
        using var culture = new CultureScope("en-US");
        var databaseBanners = Enumerable.Range(1, 3)
            .Select(id => new PortalTopBarBanner(
                id,
                $"Database banner {id}",
                "Database content",
                BannerService.LevelAnnouncement,
                BannerService.LevelName(BannerService.LevelAnnouncement),
                string.Empty,
                null,
                null))
            .ToArray();

        var model = CreateModel(
            isPortalAdmin: true,
            OmpRuntimeAdvisory.Compare(new Version(10, 0, 14), "10.0.15"),
            provider.GetRequiredService<IStringLocalizer<SharedResource>>(),
            databaseBanners);

        Assert.Equal(4, model.Banners.Count);
        Assert.Equal(databaseBanners, model.Banners.Take(3));
        Assert.Equal(0, model.Banners[3].BannerId);
        Assert.Equal("warning", model.Banners[3].LevelName);
    }

    [Theory]
    [InlineData("sv", "Äldre .NET-version på servern", "Servern kör .NET 10.0.14. OMP är byggt för 10.0.15, som innehåller de senaste säkerhetsrättningarna. Uppdatera ASP.NET Core Hosting Bundle på servern.")]
    [InlineData("sv-SE", "Äldre .NET-version på servern", "Servern kör .NET 10.0.14. OMP är byggt för 10.0.15, som innehåller de senaste säkerhetsrättningarna. Uppdatera ASP.NET Core Hosting Bundle på servern.")]
    [InlineData("en", "Older .NET runtime on the server", "The server is running .NET 10.0.14. OMP is built for 10.0.15, which includes the latest security fixes. Update the ASP.NET Core Hosting Bundle on the server.")]
    [InlineData("en-US", "Older .NET runtime on the server", "The server is running .NET 10.0.14. OMP is built for 10.0.15, which includes the latest security fixes. Update the ASP.NET Core Hosting Bundle on the server.")]
    public void RuntimeWarning_UsesSharedLocalization(
        string cultureName,
        string expectedTitle,
        string expectedContent)
    {
        using var provider = CreateServices();
        using var culture = new CultureScope(cultureName);

        var model = CreateModel(
            isPortalAdmin: true,
            OmpRuntimeAdvisory.Compare(new Version(10, 0, 14), "10.0.15"),
            provider.GetRequiredService<IStringLocalizer<SharedResource>>());

        var banner = Assert.Single(model.Banners);
        Assert.Equal(expectedTitle, banner.Title);
        Assert.Equal(expectedContent, banner.Content);
    }

    [Fact]
    public void AdvisoryFailure_KeepsDatabaseBannersAndDoesNotThrow()
    {
        using var provider = CreateServices();
        var databaseBanners = new[]
        {
            new PortalTopBarBanner(1, "Database", "Content", 1, "announcement", string.Empty, null, null)
        };

        var banners = PortalTopBarService.AppendRuntimeAdvisoryBanner(
            databaseBanners,
            isPortalAdmin: true,
            new StubRuntimeAdvisorySource(() => throw new InvalidOperationException("Metadata failure")),
            provider.GetRequiredService<IStringLocalizer<SharedResource>>());

        Assert.Same(databaseBanners, banners);
    }

    internal static PortalTopBarModel CreateModel(
        bool isPortalAdmin,
        OmpRuntimeAdvisoryResult advisory,
        IStringLocalizer<SharedResource> localizer,
        IReadOnlyList<PortalTopBarBanner>? databaseBanners = null)
    {
        var banners = PortalTopBarService.AppendRuntimeAdvisoryBanner(
            databaseBanners ?? Array.Empty<PortalTopBarBanner>(),
            isPortalAdmin,
            new StubRuntimeAdvisorySource(() => advisory),
            localizer);

        return new PortalTopBarModel
        {
            IsVisible = true,
            IsPortalAdmin = isPortalAdmin,
            PortalLink = new PortalTopBarLink("Open Module Platform", "/"),
            Banners = banners
        };
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        return services.BuildServiceProvider();
    }

    internal sealed class StubRuntimeAdvisorySource(Func<OmpRuntimeAdvisoryResult> getAdvisory)
        : IOmpRuntimeAdvisorySource
    {
        public OmpRuntimeAdvisoryResult GetAdvisory() => getAdvisory();
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

        public CultureScope(string cultureName)
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
        }
    }
}
