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
        // Provisioning must never fail the fixture (offline machine, blocked download, no
        // display): it runs as a task whose fault is observed, so the skip reason covers
        // every exception type without a catch clause naming them.
        var provisioning = ProvisionAsync();
        await provisioning.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        if (provisioning.Exception is { } failure)
        {
            UnavailableReason = $"Chromium could not be started: {failure.GetBaseException().Message}";
        }
    }

    private async Task ProvisionAsync()
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

    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }
}
