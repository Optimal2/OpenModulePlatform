using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.Auth.Models;
using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Security;

public sealed class OmpAuthSessionLifetimeConfigTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("[]")]
    public void Parse_WhenMissingOrInvalid_UsesBuiltInDefault(string? value)
    {
        var config = OmpAuthSessionLifetimeConfig.Parse(value);

        Assert.Equal(OmpAuthSessionLifetimeDefaults.BuiltInDefaultMinutes, config.DefaultMinutes);
        Assert.Equal(OmpAuthSessionLifetimeDefaults.BuiltInDefaultMinutes, config.ResolveMinutes(2));
        Assert.Empty(config.ProviderMinutes);
        Assert.True(config.UsedWholeSettingFallback);
    }

    [Fact]
    public void Parse_WhenProviderOverrideExists_ResolvesProviderSpecificMinutes()
    {
        var config = OmpAuthSessionLifetimeConfig.Parse("""{"0":600,"2":120,"7":"720"}""");

        Assert.False(config.UsedWholeSettingFallback);
        Assert.Equal(600, config.ResolveMinutes(null));
        Assert.Equal(600, config.ResolveMinutes(1));
        Assert.Equal(120, config.ResolveMinutes(2));
        Assert.Equal(720, config.ResolveMinutes(7));
    }

    [Fact]
    public void Parse_WhenFallbackIsMissing_UsesBuiltInDefaultForOtherProviders()
    {
        var config = OmpAuthSessionLifetimeConfig.Parse("""{"2":120}""");

        Assert.Equal(OmpAuthSessionLifetimeDefaults.BuiltInDefaultMinutes, config.ResolveMinutes(1));
        Assert.Equal(120, config.ResolveMinutes(2));
    }

    [Fact]
    public void Parse_WhenEntriesAreInvalid_IgnoresOnlyThoseEntries()
    {
        var config = OmpAuthSessionLifetimeConfig.Parse(
            """{"0":600,"-1":30,"abc":30,"2":0,"3":false,"4":121}""");

        Assert.Equal(600, config.ResolveMinutes(2));
        Assert.Equal(600, config.ResolveMinutes(3));
        Assert.Equal(121, config.ResolveMinutes(4));
        Assert.Equal(4, config.IgnoredEntryCount);
    }

    [Fact]
    public void Parse_WhenPositiveValuesAreOutOfRange_ClampsThem()
    {
        var config = OmpAuthSessionLifetimeConfig.Parse("""{"0":1,"2":999999}""");

        Assert.Equal(OmpAuthSessionLifetimeDefaults.MinimumMinutes, config.ResolveMinutes(null));
        Assert.Equal(OmpAuthSessionLifetimeDefaults.MaximumMinutes, config.ResolveMinutes(2));
    }

    [Fact]
    public async Task CreateAsync_WhenConfigCannotBeRead_UsesBuiltInDefaultSessionLifetime()
    {
        var configuration = new ConfigurationBuilder().Build();
        var db = new SqlConnectionFactory(configuration);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var configurationService = new OmpConfigurationService(
            db,
            cache,
            NullLogger<OmpConfigurationService>.Instance);
        var factory = new OmpAuthenticationPropertiesFactory(
            configurationService,
            NullLogger<OmpAuthenticationPropertiesFactory>.Instance);
        var user = new OmpAuthenticatedUser
        {
            ProviderId = 2,
            DisplayName = "Test User",
            Provider = "Test",
            ProviderUserKey = "test-user"
        };

        var before = DateTimeOffset.UtcNow;
        var properties = await factory.CreateAsync(user, CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        Assert.True(properties.IsPersistent);
        Assert.NotNull(properties.IssuedUtc);
        Assert.NotNull(properties.ExpiresUtc);
        Assert.InRange(properties.IssuedUtc.Value, before.AddSeconds(-1), after);
        Assert.Equal(
            OmpAuthSessionLifetimeDefaults.BuiltInDefaultMinutes,
            (properties.ExpiresUtc.Value - properties.IssuedUtc.Value).TotalMinutes);
    }
}
