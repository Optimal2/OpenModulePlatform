// File: OpenModulePlatform.UiTests/ExampleWidgetFragmentTests.cs
using Microsoft.Playwright;
using OpenModulePlatform.TestSupport.Ui;

namespace OpenModulePlatform.UiTests;

/// <summary>
/// The example web app module's <c>/widgets/overview</c> page is the
/// <c>module-fragment</c> dashboard widget the Portal embeds, so this suite boots the
/// module directly and asserts the fragment is a safe, self-contained dashboard widget:
/// it renders its content, links are relative (the Portal rewrites only relative links),
/// and it emits none of the elements or attributes the Portal sanitizer strips.
/// Skips with a reason when the app or browser is unavailable, like the other UI suites.
/// </summary>
[Collection("ui")]
[Trait("Category", "Ui")]
public sealed class ExampleWidgetFragmentTests(
    PlaywrightSessionFixture playwright,
    ExampleWebAppModuleFixture app,
    ExampleWebAppModuleAuthRequiredFixture authRequiredApp)
{
    [SkippableFact]
    public async Task Widget_fragment_renders_dashboard_content()
    {
        Skip.IfNot(playwright.Available, playwright.UnavailableReason);
        Skip.IfNot(app.Available, app.UnavailableReason);

        await using var context = await playwright.Browser!.NewContextAsync();
        var page = await context.NewPageAsync();
        var response = await page.GotoAsync(
            app.BaseUrl + "/widgets/overview",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        Assert.NotNull(response);
        Assert.True(response.Status == 200, $"/widgets/overview answered {response.Status}, expected 200");

        var html = await page.ContentAsync();
        Assert.Contains("Example Web App Module", html, StringComparison.Ordinal);
        Assert.Contains("Active configurations", html, StringComparison.Ordinal);
        Assert.Contains("Open configurations", html, StringComparison.Ordinal);

        // The fragment links are relative so the Portal can rewrite them against the
        // module's own address; an absolute or protocol-relative link would escape.
        var hrefs = await page.EvalOnSelectorAllAsync<string[]>(
            "a[href]",
            "els => els.map(e => e.getAttribute('href'))");
        Assert.True(hrefs.Length > 0, "the widget renders no links");
        foreach (var href in hrefs)
        {
            Assert.False(
                href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    || href.StartsWith("//", StringComparison.Ordinal),
                $"the widget emitted a non-relative link: {href}");
        }

        // Nothing the Portal sanitizer would strip may come from the fragment itself.
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        var onAttributes = await page.EvalOnSelectorAllAsync<string[]>(
            "*",
            "els => els.flatMap(e => [...e.attributes].map(a => a.name).filter(n => n.toLowerCase().startsWith('on')))");
        Assert.True(
            onAttributes.Length == 0,
            "the widget emitted inline event handlers:\n - " + string.Join("\n - ", onAttributes));
    }

    [SkippableFact]
    public async Task Widget_fragment_returns_401_without_cookie()
    {
        Skip.IfNot(playwright.Available, playwright.UnavailableReason);
        Skip.IfNot(authRequiredApp.Available, authRequiredApp.UnavailableReason);

        // The Portal fetches the fragment with the user's OMP cookie; a request without
        // it must answer 401 (not 403) so the caller can tell "not signed in" from
        // "no permission" and render the neutral placeholder.
        await using var context = await playwright.Browser!.NewContextAsync();
        var response = await context.APIRequest.GetAsync(authRequiredApp.BaseUrl + "/widgets/overview");
        Assert.Equal(401, response.Status);
    }
}
