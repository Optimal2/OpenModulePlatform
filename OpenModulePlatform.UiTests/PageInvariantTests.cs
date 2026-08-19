using System.Globalization;
using Microsoft.Playwright;
using OpenModulePlatform.TestSupport.Ui;

namespace OpenModulePlatform.UiTests;

/// <summary>
/// Runs the generic UI invariants over the Portal's pages at standard
/// viewports. Needs the app running (see PortalAppFixture) — skips with a
/// reason when the app or the browser is unavailable, so the suite stays
/// green on machines without the UI prerequisites.
/// </summary>
[Collection("ui")]
[Trait("Category", "Ui")]
public sealed class PortalPageInvariantTests(PlaywrightSessionFixture playwright, PortalAppFixture app)
{
    public static TheoryData<string, int, int> Cases()
    {
        // Excluded page:
        // - "/": the dashboard canvas deliberately widens the page chrome to
        //   the widget extents (portal-dashboard.js sets
        //   --portal-page-chrome-width) so the document pans horizontally at
        //   1366x768 and 375x812. The no-horizontal-overflow invariant cannot
        //   hold for the dashboard by design; re-add if the dashboard gets a
        //   viewport-contained scroll wrapper.
        string[] pages =
        [
            "/Notifications",
            "/status/404",
            "/Admin/Overview",
            "/Admin/Modules",
            "/Admin/ConfigSettings",
        ];
        return PageInvariantCases.Expand(pages);
    }

    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public async Task Page_has_no_broken_ui_patterns(string path, int width, int height)
    {
        Skip.IfNot(playwright.Available, playwright.UnavailableReason);
        Skip.IfNot(app.Available, app.UnavailableReason);

        await PageInvariantCases.AssertPageInvariantsAsync(playwright, app, path, width, height);
    }
}

/// <summary>
/// Runs the generic UI invariants over the Auth app's login page at standard
/// viewports.
/// </summary>
[Collection("ui")]
[Trait("Category", "Ui")]
public sealed class AuthPageInvariantTests(PlaywrightSessionFixture playwright, AuthAppFixture app)
{
    public static TheoryData<string, int, int> Cases()
        => PageInvariantCases.Expand(["/login"]);

    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public async Task Page_has_no_broken_ui_patterns(string path, int width, int height)
    {
        Skip.IfNot(playwright.Available, playwright.UnavailableReason);
        Skip.IfNot(app.Available, app.UnavailableReason);

        await PageInvariantCases.AssertPageInvariantsAsync(playwright, app, path, width, height);
    }
}

internal static class PageInvariantCases
{
    private static readonly (int Width, int Height)[] Viewports =
        [(1920, 1080), (1366, 768), (375, 812)];

    public static TheoryData<string, int, int> Expand(string[] pages)
    {
        var data = new TheoryData<string, int, int>();
        foreach (var page in pages)
        {
            foreach (var (width, height) in Viewports)
            {
                data.Add(page, width, height);
            }
        }

        return data;
    }

    public static async Task AssertPageInvariantsAsync(
        PlaywrightSessionFixture playwright, WebAppProcessFixture app, string path, int width, int height)
    {
        await using var context = await playwright.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height },
        });
        var page = await context.NewPageAsync();

        var response = await page.GotoAsync(app.BaseUrl + path, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        Assert.NotNull(response);

        // The Portal status page (/status/{code}) deliberately mirrors the
        // code it renders in its HTTP response, so expect that code there and
        // 200 everywhere else.
        var expectedStatus = path.StartsWith("/status/", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(path["/status/".Length..], CultureInfo.InvariantCulture)
            : 200;
        Assert.True(response.Status == expectedStatus, $"{path} answered {response.Status}, expected {expectedStatus}");

        var findings = await UiInvariantScanner.ScanAsync(page);

        Assert.True(findings.Count == 0,
            $"{path} at {width}x{height}:\n - " + string.Join("\n - ", findings));
    }
}
