using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace OpenModulePlatform.Portal.Tests.Integration;

/// <summary>
/// R3-E2: with OmpAuth:ValidateTopbarAntiforgery enabled, token-less POSTs to
/// the shared topbar endpoints must be rejected. The default (disabled) mode
/// is covered by <see cref="TopbarNotificationEndpointIntegrationTests"/>,
/// whose posts carry no token and succeed.
/// </summary>
[Collection(PushEventPipelineTestCollection.Name)]
public sealed class TopbarAntiforgeryValidationTests
{
    private readonly PushEventPipelineTestFixture _fixture;

    public TopbarAntiforgeryValidationTests(PushEventPipelineTestFixture fixture)
    {
        _fixture = fixture;
    }

    private HttpClient CreateValidatingClient()
    {
        var factory = _fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OmpAuth:ValidateTopbarAntiforgery", "true");
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add(
            "X-Test-User-Id",
            PushEventPipelineTestFixture.TestUserId.ToString(CultureInfo.InvariantCulture));
        return client;
    }

    [Theory]
    [InlineData("/notifications/mark-all-read")]
    [InlineData("/messages/mark-all-read")]
    [InlineData("/notifications/mark-read")]
    [InlineData("/navigation/favorites/toggle")]
    public async Task TokenlessPost_IsRejected_WhenValidationEnabled(string path)
    {
        using var client = CreateValidatingClient();

        using var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("returnUrl", "/notifications")
        ]);

        using var response = await client.PostAsync(path, form);

        // The platform's status-code handling turns the 400 into a redirect to
        // the shared status page for plain form posts; both shapes mean the
        // token-less POST was rejected before reaching the handler.
        var rejected =
            response.StatusCode == HttpStatusCode.BadRequest
            || (response.StatusCode == HttpStatusCode.Redirect
                && response.Headers.Location?.OriginalString == "/status/400");
        Assert.True(
            rejected,
            $"Expected a 400 rejection but got {(int)response.StatusCode} {response.StatusCode}, Location: {response.Headers.Location}");
    }
}

[Collection(PushEventPipelineTestCollection.Name)]
public sealed class TopbarAntiforgeryConfigDiagnosticTests
{
    private readonly PushEventPipelineTestFixture _fixture;

    public TopbarAntiforgeryConfigDiagnosticTests(PushEventPipelineTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DerivedFactory_WithoutValidation_StillAuthenticates()
    {
        var factory = _fixture.Factory.WithWebHostBuilder(_ => { });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add(
            "X-Test-User-Id",
            PushEventPipelineTestFixture.TestUserId.ToString(CultureInfo.InvariantCulture));

        using var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("returnUrl", "/notifications")
        ]);
        using var response = await client.PostAsync("/notifications/mark-all-read", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/notifications", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public void ValidateTopbarAntiforgerySetting_ReachesAppConfiguration()
    {
        var factory = _fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OmpAuth:ValidateTopbarAntiforgery", "true");
        });

        using var client = factory.CreateClient();
        var config = (Microsoft.Extensions.Configuration.IConfiguration)factory.Services
            .GetService(typeof(Microsoft.Extensions.Configuration.IConfiguration))!;

        Assert.Equal("true", config["OmpAuth:ValidateTopbarAntiforgery"], ignoreCase: true);
    }
}
