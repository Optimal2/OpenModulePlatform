using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenModulePlatform.Auth.Models;
using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.TestSupport;
using OpenModulePlatform.Web.Shared.Services;

namespace OpenModulePlatform.Portal.Tests.Security;

public sealed class AuthConfigurationReadFailureTests
{
    [Fact]
    public async Task SessionLifetime_WhenConfigurationReadFails_DoesNotIssueDefaultLifetime()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var configuration = CreateUnavailableConfiguration(cache);
        var factory = new OmpAuthenticationPropertiesFactory(
            configuration,
            NullLogger<OmpAuthenticationPropertiesFactory>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.ApplyAsync(
            new AuthenticationProperties(),
            new OmpAuthenticatedUser { ProviderId = 1 },
            CancellationToken.None));
    }

    [Fact]
    public void ProvisioningMode_DistinguishesReadFailureFromUnset()
    {
        var source = OmpRepositoryFiles.ReadRepositoryTextFile(
            "OpenModulePlatform.Auth",
            "Services",
            "OmpAuthRepository.cs");
        var body = MethodBody(
            source,
            "private async Task<ExternalUserProvisioningMode> GetExternalUserProvisioningModeAsync",
            "private async Task<bool> ShouldAutoProvisionExternalUserAsync");

        Assert.Contains("ReadGlobalStringAsync", body, StringComparison.Ordinal);
        Assert.Contains("read.Failed", body, StringComparison.Ordinal);
    }

    private static OmpConfigurationService CreateUnavailableConfiguration(IMemoryCache cache)
    {
        var connectionString = new SqlConnectionStringBuilder(
            TestSqlConnection.ForDatabase("OmpMissing_" + Guid.NewGuid().ToString("N")))
        {
            ConnectTimeout = 1
        }.ConnectionString;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OmpDb"] = connectionString
            })
            .Build();
        return new OmpConfigurationService(
            new SqlConnectionFactory(configuration),
            cache,
            NullLogger<OmpConfigurationService>.Instance);
    }

    private static string MethodBody(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, startMarker);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, endMarker);
        return source[start..end];
    }
}
