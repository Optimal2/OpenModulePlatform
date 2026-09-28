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
    [Fact]
    public void UnknownWidgetType_IsRejectedBeforePersistence()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            PortalDashboardWidgetPackageService.ValidateJson(
                BuildDocument("\"payload\": \"opaque\"", "future-widget"), "future.widgets.json"));

        Assert.Contains("unsupported widgetType", error.Message, StringComparison.Ordinal);
        Assert.Contains("sample.overview", error.Message, StringComparison.Ordinal);
    }

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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(159)]
    [InlineData(1801)]
    [InlineData(100000)]
    public void TryParsePayload_WithDefaultWidthOutsideRange_IsIgnored(int defaultWidth)
    {
        var payload = ModuleFragmentWidget.SerializePayload(
            new ModuleFragmentWidgetConfig(AppKey, "/widgets/overview", DefaultWidth: defaultWidth));

        Assert.Null(ModuleFragmentWidget.TryParsePayload(payload));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(95)]
    [InlineData(1401)]
    [InlineData(100000)]
    public void TryParsePayload_WithDefaultHeightOutsideRange_IsIgnored(int defaultHeight)
    {
        var payload = ModuleFragmentWidget.SerializePayload(
            new ModuleFragmentWidgetConfig(AppKey, "/widgets/overview", DefaultHeight: defaultHeight));

        Assert.Null(ModuleFragmentWidget.TryParsePayload(payload));
    }

    [Theory]
    [InlineData(160, 96)]
    [InlineData(1800, 1400)]
    [InlineData(416, 320)]
    public void TryParsePayload_WithDefaultSizeInRange_IsAccepted(int defaultWidth, int defaultHeight)
    {
        var payload = ModuleFragmentWidget.SerializePayload(
            new ModuleFragmentWidgetConfig(AppKey, "/widgets/overview", defaultWidth, defaultHeight));

        var config = ModuleFragmentWidget.TryParsePayload(payload);
        Assert.NotNull(config);
        Assert.Equal(defaultWidth, config.DefaultWidth);
        Assert.Equal(defaultHeight, config.DefaultHeight);
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
    public void Sanitize_PreservesSwedishCharacters()
    {
        const string html = "<div><p>Åtgärder för ärenden: översikt och sökning</p><a href=\"ärenden/5\">Visa</a></div>";

        var result = ModuleFragmentHtmlSanitizer.Sanitize(html, ModuleBase);

        Assert.Contains("Åtgärder för ärenden: översikt och sökning", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("�", result.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_RemovesSvgUseForeignObjectAndXlinkHref()
    {
        const string html = """
            <svg viewBox="0 0 16 16" xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
              <use xlink:href="#icon-alert"></use>
              <foreignObject width="10" height="10"><iframe src="https://evil.example"></iframe></foreignObject>
              <rect x="0" y="0" width="16" height="16" fill="currentColor"></rect>
              <a xlink:href="javascript:alert(1)">x</a>
            </svg>
            """;

        var result = ModuleFragmentHtmlSanitizer.Sanitize(html, ModuleBase);

        Assert.DoesNotContain("<use", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("foreignObject", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("xlink", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", result.Html, StringComparison.OrdinalIgnoreCase);
        // The harmless rect survives, so the whole svg is not dropped wholesale.
        Assert.Contains("<rect", result.Html, StringComparison.OrdinalIgnoreCase);
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

    [Theory]
    [InlineData("/widgets/%2e%2e/admin")]
    [InlineData("/widgets/%252e%252e/admin")]
    [InlineData("/widgets/..%2fadmin")]
    [InlineData("/widgets/%c0%ae/admin")]
    public void RewriteHref_DropsPercentEncodedDotSegments(string href)
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
    [InlineData("https://user:private-password@module.example", "http")]
    [InlineData("http://user@module.example", "http")]
    [InlineData("http://127.0.0.1:5000", "https")]
    public async Task Fetch_UnsafeInternalBaseUrl_DoesNotSendIdentity(string internalBaseUrl, string scheme)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Html("<p>x</p>")));
        var service = CreateService(handler, new ModuleFragmentWidgetOptions { InternalBaseUrl = internalBaseUrl });
        var context = CreateContext();
        context.Request.Scheme = scheme;

        var result = await service.GetFragmentAsync(context, 7, Payload(), new HashSet<int> { 7 }, [App()], CancellationToken.None);

        Assert.False(result.IsLoaded);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("https://module.example:5000/ignored", "https://module.example/sample", "https://module.example:5000/sample/widgets/overview")]
    [InlineData(null, "https://module.example/sample", "https://module.example/sample/widgets/overview")]
    [InlineData(null, "https://untrusted.example/sample", "https://portal.example/sample/widgets/overview")]
    public void Https_TargetPrecedence_UsesOnlyOperatorConfiguredRemoteOrigins(
        string? internalBaseUrl, string moduleHref, string expected)
    {
        var context = CreateContext(host: "untrusted.example");
        context.Request.Scheme = "https";
        context.Request.Headers["X-Forwarded-Host"] = "other-untrusted.example";
        context.Connection.LocalPort = 0; // Public origin also works behind a proxy.

        var target = PortalModuleFragmentService.BuildTarget(context, moduleHref, "/widgets/overview",
            new ModuleFragmentWidgetOptions { InternalBaseUrl = internalBaseUrl }, "https://portal.example/portal");

        Assert.NotNull(target);
        Assert.Equal(expected, target.RequestUri.AbsoluteUri);
        Assert.Null(target.HostHeader);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000/ignored", true)]
    [InlineData("http://user:private-password@module.example", false)]
    [InlineData("https://user@module.example", false)]
    public async Task Fetch_InsecureInternalBaseUrlOptIn_NeverAllowsUserInfo(string internalBaseUrl, bool allowed)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Html("<p>x</p>")));
        var service = CreateService(handler, new ModuleFragmentWidgetOptions
        {
            InternalBaseUrl = internalBaseUrl,
            AllowInsecureInternalBaseUrl = true
        });
        var context = CreateContext();
        context.Request.Scheme = "https";

        var result = await service.GetFragmentAsync(context, 7, Payload(), new HashSet<int> { 7 }, [App()], CancellationToken.None);

        Assert.Equal(allowed, result.IsLoaded);
        if (allowed)
        {
            var request = Assert.Single(handler.Requests);
            Assert.Equal("http://127.0.0.1:5000/sample/widgets/overview", request.Uri);
            Assert.Null(request.Host);
        }
        else
        {
            Assert.Empty(handler.Requests);
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("//untrusted.example")]
    [InlineData("http://portal.example")]
    [InlineData("https://user:password@portal.example")]
    [InlineData("not a URL")]
    public void Https_WithoutUsableConfiguredOrigin_KeepsNetworkDestinationOnLoopback(string configuredOrigin)
    {
        var context = CreateContext(host: "untrusted.example");
        context.Request.Scheme = "https";

        var target = PortalModuleFragmentService.BuildTarget(context, "https://untrusted.example/sample", "/widgets/overview",
            new ModuleFragmentWidgetOptions(), configuredOrigin);

        Assert.NotNull(target);
        Assert.Equal("https://localhost:8088/sample/widgets/overview", target.RequestUri.AbsoluteUri);
        Assert.Equal("untrusted.example", target.HostHeader);
    }

    [Fact]
    public void Http_ConfiguredHttpsPortalOrigin_DoesNotChangeLocalRouting()
    {
        var target = PortalModuleFragmentService.BuildTarget(CreateContext(), "/sample", "/widgets/overview",
            new ModuleFragmentWidgetOptions(), "https://portal.example");

        Assert.NotNull(target);
        Assert.Equal("http://localhost:8088/sample/widgets/overview", target.RequestUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("https", "untrusted.example:443", null, "https://localhost:8088/sample/widgets/overview")]
    [InlineData("https", "untrusted.example:443", "https://portal.example", "https://portal.example/sample/widgets/overview")]
    [InlineData("http", "untrusted.example:80", null, "http://localhost:8088/sample/widgets/overview")]
    public void Fetch_DefaultPortInClientHost_IsNotMistakenForRegisteredRemoteOrigin(
        string scheme, string host, string? configuredOrigin, string expected)
    {
        var context = CreateContext(host: host);
        context.Request.Scheme = scheme;
        var href = AppLinkBuilder.ResolveHref(context.Request, App());

        var target = PortalModuleFragmentService.BuildTarget(context, href!, "/widgets/overview",
            new ModuleFragmentWidgetOptions(), configuredOrigin);

        Assert.NotNull(target);
        Assert.Equal(expected, target.RequestUri.AbsoluteUri);
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
    public void OrphanedWidget_NullEmptyOrKnownPayload_IsNotOrphan()
    {
        Assert.False(PortalDashboardService.IsOrphanedWidget(new DashboardWidgetDefinition
        {
            WidgetType = "portal",
            Payload = null
        }));
        Assert.False(PortalDashboardService.IsOrphanedWidget(new DashboardWidgetDefinition
        {
            WidgetType = "portal",
            Payload = string.Empty
        }));
        Assert.False(PortalDashboardService.IsOrphanedWidget(new DashboardWidgetDefinition
        {
            WidgetType = "portal",
            Payload = "admin-overview"
        }));
        Assert.False(PortalDashboardService.IsOrphanedWidget(new DashboardWidgetDefinition
        {
            WidgetType = ModuleFragmentWidget.WidgetType,
            Payload = ModuleFragmentWidget.SerializePayload(new ModuleFragmentWidgetConfig(AppKey, "/widgets/overview"))
        }));
    }

    [Fact]
    public void OrphanedWidget_UnknownPayload_IsOrphan()
    {
        Assert.True(PortalDashboardService.IsOrphanedWidget(new DashboardWidgetDefinition
        {
            WidgetType = "portal",
            Payload = "legacy.private-module-key"
        }));
    }

    [Fact]
    public void PayloadFingerprint_IsDeterministicAndHidesThePayload()
    {
        const string payload = "legacy.private-module-key-do-not-log";

        var first = PortalDashboardService.PayloadFingerprint(payload);
        var second = PortalDashboardService.PayloadFingerprint(payload);

        Assert.Equal(first, second);
        Assert.DoesNotContain(payload, first, StringComparison.Ordinal);
        Assert.DoesNotContain("private", first, StringComparison.Ordinal);
        Assert.Matches(@"^\d+:[0-9A-F]{8}$", first);
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

    [Fact]
    public async Task Fetch_WithUtf8Bom_PreservesSwedishCharacters()
    {
        // A module may serve its fragment as UTF-8 with a leading BOM; the fetch decodes
        // the bytes and the sanitizer parses the markup, so the Swedish characters must
        // survive intact and nothing is replaced by U+FFFD.
        var html = "﻿<div><p>Åtgärder för ärenden, översikt</p></div>";
        var handler = new StubHandler((_, _) => Task.FromResult(Html(html)));
        var service = CreateService(handler, new ModuleFragmentWidgetOptions());

        var result = await service.GetFragmentAsync(CreateContext(), 7, Payload(), new HashSet<int> { 7 }, [App()], CancellationToken.None);

        Assert.True(result.IsLoaded);
        Assert.Contains("Åtgärder för ärenden, översikt", result.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("�", result.Html, StringComparison.Ordinal);
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

    [Fact]
    public async Task InvalidPayload_WarnsOncePerWidgetAcrossServiceInstances_WithoutLoggingPayload()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Html("unused")));
        var logger = new PayloadWarningLogger();
        const int firstId = 190001;
        const int secondId = 190002;
        var ids = new HashSet<int> { firstId, secondId };
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var service = CreateService(handler, new ModuleFragmentWidgetOptions(), logger);
            var result = await service.GetFragmentsAsync(CreateContext(),
                [new(firstId, "secret-invalid-payload"), new(secondId, "{}")], ids, [App()], CancellationToken.None);
            Assert.All(result.Values, value => Assert.False(value.IsLoaded));
        }

        Assert.Empty(handler.Requests);
        Assert.Equal(2, logger.Warnings.Count);
        Assert.All(logger.Warnings, message =>
        {
            Assert.Contains("module-fragment", message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-invalid-payload", message, StringComparison.Ordinal);
        });
        Assert.Contains(logger.Warnings, message => message.Contains(firstId.ToString(), StringComparison.Ordinal));
        Assert.Contains(logger.Warnings, message => message.Contains(secondId.ToString(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(190004, "portal", "unrecognized-private-payload")]
    [InlineData(190005, "future-widget", null)]
    public void UnrecognizedPayload_WarnsOnceWithoutLoggingPayload(int widgetId, string type, string? payload)
    {
        var logger = new PayloadWarningLogger();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var service = new PortalDashboardService(null!, null!, logger);
            service.LogUnrecognizedPayload(new DashboardWidgetDefinition
            {
                WidgetId = widgetId, WidgetType = type, Payload = payload
            });
        }
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(type, warning, StringComparison.Ordinal);
        Assert.DoesNotContain("unrecognized-private-payload", warning, StringComparison.Ordinal);
    }

    private sealed class PayloadWarningLogger : Microsoft.Extensions.Logging.ILogger<PortalModuleFragmentService>,
        Microsoft.Extensions.Logging.ILogger<PortalDashboardService>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    private static PortalModuleFragmentService CreateService(StubHandler handler, ModuleFragmentWidgetOptions options,
        Microsoft.Extensions.Logging.ILogger<PortalModuleFragmentService>? logger = null)
        => new(
            new StubHttpClientFactory(handler),
            new MemoryCache(new MemoryCacheOptions()),
            new StaticOptionsMonitor<ModuleFragmentWidgetOptions>(options),
            Microsoft.Extensions.Options.Options.Create(new OmpAuthOptions()),
            Microsoft.Extensions.Options.Options.Create(new WebAppOptions()),
            logger ?? NullLogger<PortalModuleFragmentService>.Instance);

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
