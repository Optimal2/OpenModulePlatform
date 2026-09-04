using System.Text.RegularExpressions;
using Microsoft.Playwright;
using OpenModulePlatform.TestSupport.Ui;

namespace OpenModulePlatform.UiTests;

/// <summary>
/// CSP smoke test for the iFrame module dropping 'unsafe-inline' from script-src
/// (campaign csp-sista-undantagen): the module renders no inline scripts at all,
/// so Index and Standalone must load clean under "script-src 'self'" — no browser
/// CSP console messages, no POSTs to /omp/csp-report, no executable inline script
/// elements, and the emitted policy header must prove the exception is gone.
/// Skips with a reason when the app or browser is unavailable, like the other UI
/// suites.
/// </summary>
[Collection("ui")]
[Trait("Category", "Ui")]
public sealed partial class IFrameCspSmokeTests(PlaywrightSessionFixture playwright, IFrameAppFixture app)
{
    [SkippableFact]
    public async Task Index_page_reports_no_csp_violations()
    {
        Skip.IfNot(playwright.Available, playwright.UnavailableReason);
        Skip.IfNot(app.Available, app.UnavailableReason);

        await using var context = await playwright.Browser!.NewContextAsync();
        var page = await context.NewPageAsync();
        var (cspConsoleMessages, cspReportPosts) = HookCspObservers(page);

        var response = await page.GotoAsync(
            app.BaseUrl + "/",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        Assert.NotNull(response);
        Assert.True(response.Status == 200, $"/ answered {response.Status}, expected 200");

        AssertScriptSrcHasNoUnsafeInline(response, "/");
        await AssertNoInlineScripts(page, "/");

        Assert.True(
            cspConsoleMessages.Count == 0,
            "/ produced CSP console messages:\n - " + string.Join("\n - ", cspConsoleMessages));
        Assert.True(
            cspReportPosts.Count == 0,
            "/ posted CSP violation reports:\n - " + string.Join("\n - ", cspReportPosts));
    }

    [SkippableFact]
    public async Task Standalone_page_reports_no_csp_violations()
    {
        Skip.IfNot(playwright.Available, playwright.UnavailableReason);
        Skip.IfNot(app.Available, app.UnavailableReason);

        await using var context = await playwright.Browser!.NewContextAsync();

        // Standalone is "/standalone/{urlId:int}" and needs a row in omp_iframe.urls
        // that is enabled and allowed for the anonymous test role. Discover one from
        // the Index toolbar instead of hardcoding a database id: Index marks rows the
        // role may not open with the "--disabled" modifier.
        var indexHtml = await (await context.APIRequest.GetAsync(app.BaseUrl + "/")).TextAsync();
        var urlId = StandaloneCandidatesRegex().Matches(indexHtml)
            .Where(m => !m.Groups[1].Value.Contains("--disabled", StringComparison.Ordinal))
            .Select(m => m.Groups[2].Value)
            .FirstOrDefault();
        Skip.If(urlId is null, "no enabled iFrame URL is configured for the anonymous role in the test database");

        var path = $"/standalone/{urlId}";
        var page = await context.NewPageAsync();
        var (cspConsoleMessages, cspReportPosts) = HookCspObservers(page);

        var response = await page.GotoAsync(
            app.BaseUrl + path,
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        Assert.NotNull(response);
        Assert.True(response.Status == 200, $"{path} answered {response.Status}, expected 200");

        AssertScriptSrcHasNoUnsafeInline(response, path);
        await AssertNoInlineScripts(page, path);

        Assert.True(
            cspConsoleMessages.Count == 0,
            $"{path} produced CSP console messages:\n - " + string.Join("\n - ", cspConsoleMessages));
        Assert.True(
            cspReportPosts.Count == 0,
            $"{path} posted CSP violation reports:\n - " + string.Join("\n - ", cspReportPosts));
    }

    private static (List<string> ConsoleMessages, List<string> ReportPosts) HookCspObservers(IPage page)
    {
        var cspConsoleMessages = new List<string>();
        var cspReportPosts = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase)
                || message.Text.Contains("Refused to", StringComparison.OrdinalIgnoreCase))
            {
                cspConsoleMessages.Add(message.Text);
            }
        };
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("/omp/csp-report", StringComparison.OrdinalIgnoreCase))
            {
                cspReportPosts.Add(request.Url);
            }
        };

        return (cspConsoleMessages, cspReportPosts);
    }

    private static void AssertScriptSrcHasNoUnsafeInline(IResponse response, string path)
    {
        var headers = response.Headers;
        var policy = headers.TryGetValue("content-security-policy", out var enforcing)
            ? enforcing
            : headers.GetValueOrDefault("content-security-policy-report-only");
        Assert.False(string.IsNullOrWhiteSpace(policy), $"{path} emitted no Content-Security-Policy header");

        var scriptSrc = policy!
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(directive => directive.StartsWith("script-src", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(scriptSrc), $"{path} policy carries no script-src directive: {policy}");
        Assert.DoesNotContain("unsafe-inline", scriptSrc, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertNoInlineScripts(IPage page, string path)
    {
        // Every <script> in the rendered DOM needs a src (or a non-JavaScript
        // data-block type) — same contract as the Portal migration tests.
        var inlineScripts = await page.EvalOnSelectorAllAsync<string[]>(
            "script",
            """els => els.filter(e => !e.src && (!e.type || /javascript|module|^importmap$/i.test(e.type))).map(e => e.outerHTML.slice(0, 120))""");
        Assert.True(
            inlineScripts.Length == 0,
            $"{path} still renders executable inline scripts:\n - " + string.Join("\n - ", inlineScripts));
    }

    // Index toolbar links render as <a class="..." href="...urlId=5"> with the
    // "iframe-toolbar__link--disabled" modifier on unavailable rows.
    [GeneratedRegex("""<a class="([^"]*)" href="[^"]*urlId=(\d+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StandaloneCandidatesRegex();
}
