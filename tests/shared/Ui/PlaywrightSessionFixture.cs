using Microsoft.Playwright;
using Xunit;

namespace OpenModulePlatform.TestSupport.Ui;

/// <summary>
/// One headless Chromium for a UI test collection. If the browser cannot be
/// provisioned (offline machine, blocked download) the fixture reports itself
/// unavailable and tests skip with the reason instead of erroring — the local
/// gates must stay runnable on machines without the UI prerequisites.
/// </summary>
public sealed class PlaywrightSessionFixture : IAsyncLifetime
{
    public IBrowser? Browser { get; private set; }
    public string UnavailableReason { get; private set; } = "not initialized";

    private IPlaywright? _playwright;

    public bool Available => Browser is not null;

    public async Task InitializeAsync()
    {
        try
        {
            // No-op when the browser is already present; downloads it otherwise.
            var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exitCode != 0)
            {
                UnavailableReason = $"playwright install chromium exited with {exitCode}";
                return;
            }

            _playwright = await Playwright.CreateAsync();
            Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        }
        catch (Exception ex)
        {
            UnavailableReason = $"Chromium could not be started: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }
}
