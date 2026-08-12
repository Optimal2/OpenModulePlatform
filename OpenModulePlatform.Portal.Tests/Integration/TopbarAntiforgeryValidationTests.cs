using System.Globalization;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OpenModulePlatform.Portal.Localization;
using OpenModulePlatform.Web.Shared.Security;

namespace OpenModulePlatform.Portal.Tests.Integration;

/// <summary>
/// R3-E2: with OmpAuth:ValidateTopbarAntiforgery enabled, token-less POSTs to
/// the shared topbar endpoints must be rejected while a valid token pair is
/// accepted. The default (disabled) mode is covered by
/// <see cref="TopbarNotificationEndpointIntegrationTests"/>, whose posts carry
/// no token and succeed.
/// </summary>
[Collection(PushEventPipelineTestCollection.Name)]
public sealed class TopbarAntiforgeryValidationTests
{
    private readonly PushEventPipelineTestFixture _fixture;

    public TopbarAntiforgeryValidationTests(PushEventPipelineTestFixture fixture)
    {
        _fixture = fixture;
    }

    // Every derived factory hosts a full app instance, including its own push
    // event dispatcher polling the shared fixture database — which would steal
    // outbox events from the pipeline delivery tests in this collection. Turn
    // the dispatcher off (these tests never need it) and dispose the factory
    // per test instead of leaving it to the fixture's teardown.
    private WebApplicationFactory<PortalResource> CreateDerivedFactory(bool validateTopbarAntiforgery)
    {
        return _fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("PushEvents:Dispatcher:Enabled", "false");
            if (validateTopbarAntiforgery)
            {
                builder.UseSetting("OmpAuth:ValidateTopbarAntiforgery", "true");
            }
        });
    }

    private static HttpClient CreateAuthenticatedClient(WebApplicationFactory<PortalResource> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add(
            "X-Test-User-Id",
            PushEventPipelineTestFixture.TestUserId.ToString(CultureInfo.InvariantCulture));
        return client;
    }

    [Fact]
    public async Task TokenedPost_Succeeds_WhenValidationEnabled()
    {
        using var factory = CreateDerivedFactory(validateTopbarAntiforgery: true);
        using var client = CreateAuthenticatedClient(factory);

        // Mint a matching cookie/request token pair straight from the app's
        // antiforgery service (same Data Protection key ring as the server),
        // sidestepping the fixture's inability to render full pages. The token
        // is bound to the authenticated identity, so mint it as the same
        // principal TestAuthHandler produces for the actual request.
        var antiforgery = (IAntiforgery)factory.Services.GetService(typeof(IAntiforgery))!;
        var identity = new ClaimsIdentity(
            [
                new Claim(
                    OmpAuthDefaults.UserIdClaimType,
                    PushEventPipelineTestFixture.TestUserId.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, $"test-user-{PushEventPipelineTestFixture.TestUserId}")
            ],
            TestAuthHandler.SchemeName);
        var mintContext = new DefaultHttpContext
        {
            RequestServices = factory.Services,
            User = new ClaimsPrincipal(identity)
        };
        var tokens = antiforgery.GetAndStoreTokens(mintContext);
        var setCookie = mintContext.Response.Headers.SetCookie.ToString();
        var cookiePair = setCookie.Split(';')[0];

        using var form = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("returnUrl", "/notifications"),
            new KeyValuePair<string, string>(tokens.FormFieldName ?? "__RequestVerificationToken", tokens.RequestToken!)
        ]);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/notifications/mark-all-read")
        {
            Content = form
        };
        request.Headers.Add("Cookie", cookiePair);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/notifications", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/notifications/mark-all-read")]
    [InlineData("/messages/mark-all-read")]
    [InlineData("/notifications/mark-read")]
    [InlineData("/navigation/favorites/toggle")]
    public async Task TokenlessPost_IsRejected_WhenValidationEnabled(string path)
    {
        using var factory = CreateDerivedFactory(validateTopbarAntiforgery: true);
        using var client = CreateAuthenticatedClient(factory);

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

    [Fact]
    public async Task DerivedFactory_WithoutValidation_StillAuthenticates()
    {
        using var factory = CreateDerivedFactory(validateTopbarAntiforgery: false);
        using var client = CreateAuthenticatedClient(factory);

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
        using var factory = CreateDerivedFactory(validateTopbarAntiforgery: true);
        using var client = factory.CreateClient();
        var config = (IConfiguration)factory.Services.GetService(typeof(IConfiguration))!;

        Assert.Equal("true", config["OmpAuth:ValidateTopbarAntiforgery"], ignoreCase: true);
    }
}
