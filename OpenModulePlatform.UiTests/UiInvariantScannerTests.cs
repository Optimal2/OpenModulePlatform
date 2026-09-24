using Microsoft.Playwright;
using OpenModulePlatform.TestSupport.Ui;

namespace OpenModulePlatform.UiTests;

// No app or database: exercise the scanner against an isolated, layered control.
[Trait("Category", "Ui")]
public sealed class UiInvariantScannerTests(PlaywrightSessionFixture playwright)
    : IClassFixture<PlaywrightSessionFixture>
{
    [Theory]
    [InlineData("", false)]
    [InlineData("pointer-events: auto;", false)]
    [InlineData("pointer-events: none !important;", false)]
    [InlineData("", true, "rgb(11, 87, 208)")]
    [InlineData("display: none;", true)]
    [InlineData("visibility: hidden;", true)]
    [InlineData("opacity: 0;", true)]
    [InlineData("opacity: 0.5;", true)]
    [InlineData("transform: translateX(160px);", true)]
    [InlineData("background: white;", true)]
    [InlineData("background: rgba(11, 87, 208, 0);", true)]
    [InlineData("height: 24px;", true)]
    [InlineData("width: 40px;", true)]
    [InlineData("clip-path: inset(0 90px 0 0);", true)]
    [InlineData("z-index: -1;", true)]
    [InlineData("z-index: 2;", true)]
    public async Task Text_uses_only_a_visible_opaque_surface_behind_all_its_rectangles(
        string surfaceStyle, bool expectFinding, string textColor = "white")
    {
        // A missing browser must fail this regression proof, never silently skip it.
        Assert.True(playwright.Available, playwright.UnavailableReason);
        await using var context = await playwright.Browser!.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync(
            $$"""
            <style>
                body { background: white; }
                .track { position: relative; isolation: isolate; background: white; width: 320px; }
                .surface { position: absolute; top: 0; left: 0; width: 120px; height: 64px;
                    z-index: 0; background: rgb(11, 87, 208); pointer-events: none; }
                .option { position: relative; z-index: 1; display: inline-block; padding: 8px; min-width: 280px;
                    color: {{textColor}}; font: 16px/24px Arial; }
            </style>
            <div class="track">
                <span class="surface" style="{{surfaceStyle}}"></span>
                <label class="option">Selected<br>option</label>
            </div>
            """);

        var originalMarkup = await page.Locator("body").InnerHTMLAsync();
        var findings = await UiInvariantScanner.ScanAsync(page);

        if (expectFinding)
        {
            var finding = Assert.Single(findings);
            Assert.Contains("invisible text", finding);
            Assert.Contains("label.option", finding);
        }
        else
        {
            Assert.Empty(findings);
        }

        Assert.Equal(originalMarkup, await page.Locator("body").InnerHTMLAsync());
    }
}
