using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.Portal.Models;
using OpenModulePlatform.Portal.Options;
using OpenModulePlatform.Portal.Services;
using OpenModulePlatform.Web.Shared.Options;
using OpenModulePlatform.Web.Shared.Security;
using System.Net;
using System.Security.Claims;
using System.Text;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// The generic module-fragment widget type: definition validation, sanitization of the
/// module-served HTML, and the server-side fetch (permission gate, timeout, cache and
/// identity forwarding).
/// </summary>
public sealed class ModuleFragmentWidgetTests
{
    private const string AppKey = "sample_module_web";
    private const string ModuleBase = "http://portal.example:8088/sample";

    // ----- definition validation -------------------------------------------------------

    [Fact]
    public void Definition_WithValidFragmentPath_IsAccepted()
    {
        PortalDashboardWidgetPackageService.ValidateJson(
            BuildDocument("\"appKey\": \"sample_module_web\", \"fragmentPath\": \"/widgets/overview?rows=5\", \"defaultWidth\": 480, \"defaultHeight\": 240"),
            "test.json");
    }

    [Theory]
    [InlineData("widgets/overview")]
    [InlineData("//evil.example/widgets")]
    [InlineData("https://evil.example/widgets")]
    [InlineData("/widgets/../admin")]
    [InlineData("/widgets/%2e%2e/admin")]
    [InlineData("/widgets/./overview")]
    [InlineData("/widgets\\\\overview")]
    [InlineData("/widgets/overview#top")]
    [InlineData("/user@evil.example/x")]
    [InlineData("/javascript:alert(1)")]
    [InlineData("")]
    public void Definition_WithInvalidFragmentPath_IsRejected(string fragmentPath)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PortalDashboardWidgetPackageService.ValidateJson(
            BuildDocument($"\"appKey\": \"sample_module_web\", \"fragmentPath\": \"{fragmentPath}\""),
            "test.json"));
        Assert.Contains("fragmentPath", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Definition_WithoutAppKey_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PortalDashboardWidgetPackageService.ValidateJson(
            BuildDocument("\"fragmentPath\": \"/widgets/overview\""),
            "test.json"));
        Assert.Contains("appKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Definition_WithPayloadInsteadOfFields_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => PortalDashboardWidgetPackageService.ValidateJson(
            BuildDocument("\"payload\": \"/widgets/overview\", \"appKey\": \"sample_module_web\", \"fragmentPath\": \"/widgets/overview\""),
            "test.json"));
    }

    [Theory]
    [InlineData("\"defaultWidth\": 100")]
    [InlineData("\"defaultWidth\": 5000")]
    [InlineData("\"defaultHeight\": 10")]
    [InlineData("\"defaultHeight\": 2000")]
    public void Definition_WithDefaultSizeOutsideRange_IsRejected(string sizeProperty)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PortalDashboardWidgetPackageService.ValidateJson(
            BuildDocument($"\"appKey\": \"sample_module_web\", \"fragmentPath\": \"/widgets/overview\", {sizeProperty}"),
            "test.json"));
        Assert.Contains("must be between", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Definition_FragmentFieldsOnPortalWidget_AreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => PortalDashboardWidgetPackageService.ValidateJson(
            BuildDocument("\"fragmentPath\": \"/widgets/overview\"", widgetType: "portal"),
            "test.json"));
    }

    [Fact]
    public void DefaultSize_ComesFromDefinition_OrModuleFragmentDefault()
    {
        var configured = new DashboardWidgetDefinition
        {
            WidgetType = ModuleFragmentWidget.WidgetType,
            Payload = ModuleFragmentWidget.SerializePayload(new ModuleFragmentWidgetConfig(AppKey, "/widgets/overview", 640, 200))
        };
        var unconfigured = new DashboardWidgetDefinition
        {
            WidgetType = ModuleFragmentWidget.WidgetType,
            Payload = ModuleFragmentWidget.SerializePayload(new ModuleFragmentWidgetConfig(AppKey, "/widgets/overview"))
        };

        Assert.Equal(640, PortalDashboardService.GetDefaultWidgetWidth(configured));
        Assert.Equal(200, PortalDashboardService.GetDefaultWidgetHeight(configured));
        Assert.Equal(416, PortalDashboardService.GetDefaultWidgetWidth(unconfigured));
        Assert.Equal(320, PortalDashboardService.GetDefaultWidgetHeight(unconfigured));
    }

    [Fact]
    public void StoredPayload_ThatNoLongerValidates_IsIgnored()
    {
        var tampered = "{\"appKey\":\"sample_module_web\",\"fragmentPath\":\"//evil.example/x\"}";
        Assert.Null(ModuleFragmentWidget.TryParsePayload(tampered));
        Assert.Null(ModuleFragmentWidget.TryParsePayload("not json"));
    }

    [Fact]
    public async Task HostAgentImportPayload_IsReadByTheRenderingPath()
    {
        // Universal packages are imported by the HostAgent through DashboardWidgetPackageReader,
        // not by this Portal service. The payload it produces must be the one the Portal
        // renders from, byte for byte, so a Portal re-import sees the row as unchanged.
        var json = BuildDocument("\"appKey\": \"sample_module_web\", \"fragmentPath\": \"/widgets/overview?rows=5\", \"defaultWidth\": 480");
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var package = await new DashboardWidgetPackageReader().ReadAsync(stream, "test.json", CancellationToken.None);

        var payload = Assert.Single(package.Widgets).Payload;
        var config = ModuleFragmentWidget.TryParsePayload(payload);
        Assert.NotNull(config);
        Assert.Equal(AppKey, config.AppKey);
        Assert.Equal("/widgets/overview?rows=5", config.FragmentPath);
        Assert.Equal(480, config.DefaultWidth);
        Assert.Null(config.DefaultHeight);
        Assert.Equal(
            ModuleFragmentWidget.SerializePayload(new ModuleFragmentWidgetConfig(AppKey, "/widgets/overview?rows=5", 480)),
            payload);
    }

    // ----- sanitization ----------------------------------------------------------------

    [Fact]
    public void Sanitize_RemovesScriptStyleHandlersAndEmbeddedContent()
    {
        const string html = """
            <div class="card" onclick="alert(1)" style="color:red" id="x">
              <script>alert('script-body')</script>
              <style>.card{display:none}</style>
              <iframe src="https://evil.example"></iframe>
              <object data="https://evil.example/x.swf"></object>
              <img src="x" onerror="alert(1)">
              <form action="/steal"><input name="a"></form>
              <span onmouseover="alert(2)">kept text</span>
            </div>
            """;

        var result = ModuleFragmentHtmlSanitizer.Sanitize(html, ModuleBase);

        Assert.True(result.IsLoaded);
        Assert.Contains("kept text", result.Html, StringComparison.Ordinal);
        Assert.Contains("class=\"card\"", result.Html, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "<script", "script-body", "<style", "display:none", "<iframe", "<object", "<img", "<form", "<input", "onclick", "onmouseover", "onerror", "style=", "id=" })
        {
            Assert.DoesNotContain(forbidden, result.Html, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Sanitize_KeepsAllowedElementsAndAttributes()
    {
        const string html = """
            <div class="dashboard-module-widget" aria-label="Summary" data-module-row="3">
              <p><strong>Bold</strong> <em>italic</em></p>
              <ul><li>one</li></ul>
              <table><thead><tr><th colspan="2">Head</th></tr></thead><tbody><tr><td>a</td><td>b</td></tr></tbody></table>
              <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M0 0h16v16H0z" fill="currentColor"></path></svg>
            </div>
            """;

        var result = ModuleFragmentHtmlSanitizer.Sanitize(html, ModuleBase);

        foreach (var expected in new[] { "<div", "<p>", "<strong>", "<em>", "<ul>", "<li>", "<table>", "<thead>", "<tbody>", "<tr>", "<th", "<td>", "<svg", "<path", "aria-label=\"Summary\"", "aria-hidden=\"true\"", "data-module-row=\"3\"", "colspan=\"2\"", "d=\"M0 0h16v16H0z\"" })
        {
            Assert.Contains(expected, result.Html, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Sanitize_RemovesPortalReservedDataAttributes()
    {
        const string html = "<div data-dashboard-widget data-widget-remove data-portal-entry=\"1\" data-module-key=\"ok\">x</div>";

        var result = ModuleFragmentHtmlSanitizer.Sanitize(html, ModuleBase);

        Assert.DoesNotContain("data-dashboard-widget", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-widget-remove", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-portal-entry", result.Html, StringComparison.Ordinal);
        Assert.Contains("data-module-key=\"ok\"", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_ReadsAndStripsWidgetModeSuggestion()
    {
        var result = ModuleFragmentHtmlSanitizer.Sanitize("<div data-widget-mode=\"wide\">x</div>", ModuleBase);

        Assert.Equal("is-wide", result.ModeClass);
        Assert.DoesNotContain("data-widget-mode", result.Html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("jobs/5", "http://portal.example:8088/sample/jobs/5")]
    [InlineData("/jobs/5?tab=hits", "http://portal.example:8088/sample/jobs/5?tab=hits")]
    [InlineData("/sample/jobs/5", "http://portal.example:8088/sample/jobs/5")]
    [InlineData("http://portal.example:8088/sample/jobs/5", "http://portal.example:8088/sample/jobs/5")]
    public void RewriteHref_ResolvesLinksAgainstTheModuleBase(string href, string expected)
    {
        Assert.Equal(expected, ModuleFragmentHtmlSanitizer.RewriteHref(href, ModuleBase));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData(" JavaScript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("https://evil.example/sample/jobs")]
    [InlineData("http://portal.example:8088/admin")]
    [InlineData("http://portal.example:8088/sample/../admin")]
    [InlineData("//evil.example/x")]
    [InlineData("/sample/../admin")]
    [InlineData("jobs/../../admin")]
    [InlineData("/%2e%2e/admin")]
    [InlineData("\\\\evil.example\\share")]
    [InlineData("#top")]
    public void RewriteHref_DropsLinksOutsideTheModule(string href)
    {
        Assert.Null(ModuleFragmentHtmlSanitizer.RewriteHref(href, ModuleBase));
    }

    [Fact]
    public void Sanitize_RewritesAndDropsLinksInMarkup()
    {
        const string html = "<a class=\"go\" href=\"jobs/5\">ok</a><a href=\"javascript:alert(1)\">bad</a><a href=\"https://evil.example/\">away</a>";

        var result = ModuleFragmentHtmlSanitizer.Sanitize(html, ModuleBase);

        Assert.Contains("href=\"http://portal.example:8088/sample/jobs/5\"", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.example", result.Html, StringComparison.Ordinal);
        Assert.Contains(">bad</a>", result.Html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(200, "is-narrow")]
    [InlineData(359, "is-narrow")]
    [InlineData(360, "is-medium")]
    [InlineData(639, "is-medium")]
    [InlineData(640, "is-wide")]
    public void WidthClass_FollowsTheDocumentedBreakpoints(int width, string expected)
    {
        Assert.Equal(expected, ModuleFragmentWidget.GetWidthClass(width));
    }

    // ----- fetch -----------------------------------------------------------------------

    [Fact]
    public async Task Fetch_ForwardsOnlyIdentityCookies_AndSanitizes()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Html("<div class=\"x\">hello<script>bad()</script><a href=\"jobs/1\">j</a></div>")));
        var service = CreateService(handler, new ModuleFragmentWidgetOptions());
        var context = CreateContext();

        var result = await service.GetFragmentAsync(context, 7, Payload(), new HashSet<int> { 7 }, [App()], CancellationToken.None);

        Assert.True(result.IsLoaded);
        Assert.Contains("hello", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", result.Html, StringComparison.Ordinal);
        Assert.Contains("href=\"http://portal.example:8088/sample/jobs/1\"", result.Html, StringComparison.Ordinal);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:8088/sample/widgets/overview", request.Uri);
        Assert.Equal("portal.example:8088", request.Host);
        Assert.Equal(".OpenModulePlatform.Auth=chunks-2; .OpenModulePlatform.AuthC1=part1; .OpenModulePlatform.AuthC2=part2; omp_active_role=5", request.Cookie);
        Assert.DoesNotContain("unrelated", request.Cookie ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("1", request.FragmentHeader);
    }

    [Fact]
    public async Task Fetch_NeverFollowsAClientSuppliedHostName()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Html("<p>x</p>")));
        var service = CreateService(handler, new ModuleFragmentWidgetOptions());
        var context = CreateContext(host: "internal-admin.example:9999");

        await service.GetFragmentAsync(context, 7, Payload(), new HashSet<int> { 7 }, [App()], CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("http://localhost:8088/", request.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_UsesConfiguredInternalBaseUrl()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Html("<p>x</p>")));
        var service = CreateService(handler, new ModuleFragmentWidgetOptions { InternalBaseUrl = "http://127.0.0.1:5000/ignored-path" });

        await service.GetFragmentAsync(CreateContext(), 7, Payload(), new HashSet<int> { 7 }, [App()], CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:5000/sample/widgets/overview", request.Uri);
        Assert.Null(request.Host);
    }

    [Fact]
    public async Task Fetch_Timeout_ReturnsPlaceholderWithoutDetails()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Html("<p>late</p>");
        });
        var service = CreateService(handler, new ModuleFragmentWidgetOptions { TimeoutMilliseconds = 250 });

        var started = DateTime.UtcNow;
        var result = await service.GetFragmentAsync(CreateContext(), 7, Payload(), new HashSet<int> { 7 }, [App()], CancellationToken.None);

        Assert.False(result.IsLoaded);
        Assert.Equal(string.Empty, result.Html);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Fetch_NonSuccessStatus_ReturnsPlaceholder(HttpStatusCode status)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("Stack trace at C:\\inetpub\\module\\Secret.cs", Encoding.UTF8, "text/html")
        }));
        var service = CreateService(handler, new ModuleFragmentWidgetOptions());

        var result = await service.GetFragmentAsync(CreateContext(), 7, Payload(), new HashSet<int> { 7 }, [App()], CancellationToken.None);

        Assert.False(result.IsLoaded);
        Assert.Equal(string.Empty, result.Html);
    }

    [Fact]
    public async Task Fetch_NonHtmlOrOversizedResponse_ReturnsPlaceholder()
    {
        var json = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        }));
        var big = new StubHandler((_, _) => Task.FromResult(Html(new string('a', 4096))));

        var jsonResult = await CreateService(json, new ModuleFragmentWidgetOptions())
            .GetFragmentAsync(CreateContext(), 7, Payload(), new HashSet<int> { 7 }, [App()], CancellationToken.None);
        var bigResult = await CreateService(big, new ModuleFragmentWidgetOptions { MaxResponseBytes = 1024 })
            .GetFragmentAsync(CreateContext(), 7, Payload(), new HashSet<int> { 7 }, [App()], CancellationToken.None);

        Assert.False(jsonResult.IsLoaded);
        Assert.False(bigResult.IsLoaded);
    }

    [Fact]
    public async Task Fetch_WidgetWithoutPermission_IsNeverRequested()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Html("<p>secret</p>")));
        var service = CreateService(handler, new ModuleFragmentWidgetOptions());

        var result = await service.GetFragmentAsync(CreateContext(), 7, Payload(), new HashSet<int>(), [App()], CancellationToken.None);

        Assert.False(result.IsLoaded);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void WidgetPermissionRules_DenyUsersWithoutTheRoleOrPermission()
    {
        var restrictions = new Dictionary<int, List<PortalDashboardService.WidgetAccessRule>>
        {
            [7] = [new PortalDashboardService.WidgetAccessRule(null, "Sample.View"), new PortalDashboardService.WidgetAccessRule(3, null)]
        };

        Assert.False(PortalDashboardService.CanAccessWidget(7, restrictions, new HashSet<int> { 1 }, new HashSet<string> { "Other.View" }));
        Assert.True(PortalDashboardService.CanAccessWidget(7, restrictions, new HashSet<int>(), new HashSet<string> { "Sample.View" }));
        Assert.True(PortalDashboardService.CanAccessWidget(7, restrictions, new HashSet<int> { 3 }, new HashSet<string>()));
    }

    [Fact]
    public async Task Fetch_ModuleAppWithoutPermission_IsNeverRequested()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Html("<p>secret</p>")));
        var service = CreateService(handler, new ModuleFragmentWidgetOptions());

        var result = await service.GetFragmentAsync(CreateContext(), 7, Payload(), new HashSet<int> { 7 }, [], CancellationToken.None);

        Assert.False(result.IsLoaded);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Fetch_IsCachedPerUserAndWidget()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Html("<p>x</p>")));
        var service = CreateService(handler, new ModuleFragmentWidgetOptions { CacheSeconds = 60 });

        await service.GetFragmentAsync(CreateContext(userId: "7"), 7, Payload(), new HashSet<int> { 7 }, [App()], CancellationToken.None);
        await service.GetFragmentAsync(CreateContext(userId: "7"), 7, Payload(), new HashSet<int> { 7 }, [App()], CancellationToken.None);
        Assert.Single(handler.Requests);

        await service.GetFragmentAsync(CreateContext(userId: "8"), 7, Payload(), new HashSet<int> { 7 }, [App()], CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);

        await service.GetFragmentAsync(CreateContext(userId: "8"), 9, Payload(), new HashSet<int> { 9 }, [App()], CancellationToken.None);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Fetch_WithCachingDisabled_RequestsEveryTime()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Html("<p>x</p>")));
        var service = CreateService(handler, new ModuleFragmentWidgetOptions { CacheSeconds = 0 });

        await service.GetFragmentAsync(CreateContext(), 7, Payload(), new HashSet<int> { 7 }, [App()], CancellationToken.None);
        await service.GetFragmentAsync(CreateContext(), 7, Payload(), new HashSet<int> { 7 }, [App()], CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
    }

    // ----- helpers ---------------------------------------------------------------------

    private static string BuildDocument(string fragmentFields, string widgetType = ModuleFragmentWidget.WidgetType)
        => $$"""
            {
              "format": "omp.portal.dashboard.widgets",
              "formatVersion": 1,
              "widgets": [
                {
                  "widgetKey": "sample.overview",
                  "widgetVersion": "1.0.0",
                  "title": "Overview",
                  "widgetType": "{{widgetType}}",
                  {{fragmentFields}},
                  "permissionNames": [],
                  "roleNames": []
                }
              ]
            }
            """;

    private static string Payload()
        => ModuleFragmentWidget.SerializePayload(new ModuleFragmentWidgetConfig(AppKey, "/widgets/overview"));

    private static PortalAppEntry App()
        => new()
        {
            AppInstanceId = Guid.NewGuid(),
            AppInstanceKey = "sample_module_web_1",
            AppKey = AppKey,
            DisplayName = "Sample",
            RoutePath = "/sample"
        };

    private static DefaultHttpContext CreateContext(string host = "portal.example:8088", string userId = "7")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString(host);
        context.Connection.LocalPort = 8088;
        context.Request.Headers.Cookie =
            ".OpenModulePlatform.Auth=chunks-2; unrelated=tracking; .OpenModulePlatform.AuthC1=part1; .OpenModulePlatform.AuthC2=part2; omp_active_role=5; .OpenModulePlatform.AuthOther=no";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(OmpAuthDefaults.UserIdClaimType, userId)],
            "test"));
        return context;
    }

    private static HttpResponseMessage Html(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static PortalModuleFragmentService CreateService(StubHandler handler, ModuleFragmentWidgetOptions options)
        => new(
            new StubHttpClientFactory(handler),
            new MemoryCache(new MemoryCacheOptions()),
            new StaticOptionsMonitor<ModuleFragmentWidgetOptions>(options),
            Microsoft.Extensions.Options.Options.Create(new OmpAuthOptions()),
            NullLogger<PortalModuleFragmentService>.Instance);

    private sealed record CapturedRequest(string Uri, string? Host, string? Cookie, string? FragmentHeader);

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(new CapturedRequest(
                    request.RequestUri!.ToString(),
                    request.Headers.Host,
                    request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null,
                    request.Headers.TryGetValues(PortalModuleFragmentService.FragmentRequestHeader, out var marker) ? marker.Single() : null));
            }

            return respond(request, cancellationToken);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
