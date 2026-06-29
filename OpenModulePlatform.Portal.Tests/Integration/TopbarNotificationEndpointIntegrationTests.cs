using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenModulePlatform.Portal.Tests.Integration;

public sealed class TopbarNotificationEndpointIntegrationTests : IClassFixture<PushEventPipelineTestFixture>
{
    private readonly PushEventPipelineTestFixture _fixture;

    public TopbarNotificationEndpointIntegrationTests(PushEventPipelineTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MarkAllRead_FormPost_RedirectsBackToCurrentPage()
    {
        var client = _fixture.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add(
            "X-Test-User-Id",
            PushEventPipelineTestFixture.TestUserId.ToString(CultureInfo.InvariantCulture));

        using var response = await client.PostAsync(
            "/notifications/mark-all-read",
            new FormUrlEncodedContent([
                new KeyValuePair<string, string>("returnUrl", "/notifications?filter=unread")
            ]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/notifications?filter=unread", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task MarkAllRead_AjaxPost_ReturnsUnreadCountPayload()
    {
        var client = _fixture.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/notifications/mark-all-read")
        {
            Content = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("returnUrl", "/notifications")
            ])
        };
        request.Headers.Add(
            "X-Test-User-Id",
            PushEventPipelineTestFixture.TestUserId.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        using var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal(0, payload.GetProperty("unreadCount").GetInt32());
    }
}
