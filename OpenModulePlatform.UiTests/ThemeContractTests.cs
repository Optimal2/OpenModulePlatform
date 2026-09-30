using System.Text.Json;
using Microsoft.Playwright;
using OpenModulePlatform.TestSupport.Ui;
using Xunit.Abstractions;

namespace OpenModulePlatform.UiTests;

/// <summary>
/// The theme contract (docs/THEME_CONTRACT.md) on the Portal: the System / Light / Dark
/// menu in the shared top bar, the stored OMP_THEME_PREFERENCE, System following
/// prefers-color-scheme, and readable text in both palettes.
/// </summary>
[Collection("ui")]
[Trait("Category", "Ui")]
public sealed class PortalThemeContractTests(PlaywrightSessionFixture playwright, PortalAppFixture app, ITestOutputHelper output)
{
    private const string PagePath = "/Admin/Overview";

    [SkippableFact]
    public Task Menu_switches_theme_and_the_choice_survives_reload()
        => ThemeScenarios.MenuSwitchesAndPersistsAsync(playwright, app, PagePath);

    [SkippableFact]
    public Task Menu_works_with_the_keyboard_and_returns_focus()
        => ThemeScenarios.KeyboardAsync(playwright, app, PagePath);

    [SkippableFact]
    public Task System_mode_follows_prefers_color_scheme()
        => ThemeScenarios.SystemFollowsColorSchemeAsync(playwright, app, PagePath);

    [SkippableFact]
    public Task Denied_storage_still_switches_for_the_session()
        => ThemeScenarios.DeniedStorageStillSwitchesAsync(playwright, app, PagePath);

    [SkippableFact]
    public Task A_newer_local_choice_beats_an_older_cookie()
        => ThemeScenarios.NewerLocalChoiceBeatsOlderCookieAsync(playwright, app, PagePath);

    public static TheoryData<string, string> ReadabilityCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in ReadablePages)
        {
            data.Add(path, "light");
            data.Add(path, "dark");
        }

        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(ReadabilityCases))]
    public Task Text_is_readable_in_both_themes(string path, string theme)
        => ThemeScenarios.TextIsReadableAsync(
            playwright, app, output, path, theme, "portal" + path.Replace('/', '-').ToLowerInvariant(), Specimens.GetValueOrDefault(path));

    private static readonly string[] ReadablePages =
    [
        "/Admin/Overview",
        "/Admin/Modules",
        "/Admin/ConfigSettings",
        "/Notifications",
        "/Admin/SystemLog",
        "/Admin/HostDeployments",
        "/Admin/Maintenance",
        // A conversation page. The test runs anonymously against whatever the local
        // database holds, so the group thread itself comes from the specimen below.
        "/messages/1",
    ];

    // States a page renders only with particular data: an error log row, a failed
    // integrity check, a group conversation, a failed send. Each specimen is the
    // page's own markup for that state, inserted into the live page so it is
    // measured with the real stylesheet, in both palettes and (for .btn) on hover.
    // Keep them in step with the views named in the comments.
    private static readonly Dictionary<string, string> Specimens = new(StringComparer.Ordinal)
    {
        // portal-dashboard.js (the dashboard itself is outside this matrix, see
        // PortalPageInvariantTests): the unsaved-draft banner and its error variant.
        ["/Admin/Overview"] = """
            <div class="dashboard-draft-banner"><span class="dashboard-draft-banner__message">You have unsaved dashboard changes.</span>
            <span class="dashboard-draft-banner__actions"><button type="button" class="btn btn-secondary btn-sm">Discard</button> <button type="button" class="btn btn-primary btn-sm">Save</button></span></div>
            <div class="dashboard-draft-banner dashboard-draft-banner--error"><span class="dashboard-draft-banner__message">The dashboard could not be saved.</span></div>
            """,
        // SystemLog.cshtml.cs LevelPillClass.
        ["/Admin/SystemLog"] = """
            <div class="card"><span class="pill pill-danger">Error</span> <span class="pill pill-warning">Warn</span></div>
            """,
        // HostDeployments.cshtml integrity columns.
        ["/Admin/HostDeployments"] = """
            <div class="card"><span class="integrity-pill integrity-pill--error">Mismatch</span> <span class="integrity-pill integrity-pill--ok">Consistent</span>
            <span class="integrity-pill integrity-pill--warning">Warning</span></div>
            """,
        // Maintenance.cshtml retention actions, enabled.
        ["/Admin/Maintenance"] = """
            <div class="toolbar"><button type="button" class="btn btn-success">Confirm change</button>
            <button type="button" class="btn btn-warning">Cancel</button>
            <button type="button" class="btn btn-danger">Queue artifact cleanup</button></div>
            """,
        // Thread.cshtml + _ThreadMessages.cshtml: a group conversation with all six
        // sender colours, an own message and the failed-send banner.
        ["/messages/1"] = """
            <section class="card portal-message-thread"><div class="portal-message-thread__scroll">
            <div class="portal-message-thread__error-banner validation-summary-errors"><ul><li>The message could not be sent.</li></ul></div>
            <div class="portal-message-thread__messages">
            <article class="portal-message-thread__message portal-message-thread__message--sender-1"><span class="portal-message-thread__avatar" aria-hidden="true">A</span><div class="portal-message-thread__bubble"><div class="portal-message-thread__meta"><strong>Sender one</strong><span>09:01</span></div><p>Incoming message in a group.</p></div></article>
            <article class="portal-message-thread__message portal-message-thread__message--sender-2"><span class="portal-message-thread__avatar" aria-hidden="true">B</span><div class="portal-message-thread__bubble"><div class="portal-message-thread__meta"><strong>Sender two</strong><span>09:02</span></div><p>Incoming message in a group.</p></div></article>
            <article class="portal-message-thread__message portal-message-thread__message--sender-3"><span class="portal-message-thread__avatar" aria-hidden="true">C</span><div class="portal-message-thread__bubble"><div class="portal-message-thread__meta"><strong>Sender three</strong><span>09:03</span></div><p>Incoming message in a group.</p></div></article>
            <article class="portal-message-thread__message portal-message-thread__message--sender-4"><span class="portal-message-thread__avatar" aria-hidden="true">D</span><div class="portal-message-thread__bubble"><div class="portal-message-thread__meta"><strong>Sender four</strong><span>09:04</span></div><p>Incoming message in a group.</p></div></article>
            <article class="portal-message-thread__message portal-message-thread__message--sender-5"><span class="portal-message-thread__avatar" aria-hidden="true">E</span><div class="portal-message-thread__bubble"><div class="portal-message-thread__meta"><strong>Sender five</strong><span>09:05</span></div><p>Incoming message in a group.</p></div></article>
            <article class="portal-message-thread__message portal-message-thread__message--sender-6"><span class="portal-message-thread__avatar" aria-hidden="true">F</span><div class="portal-message-thread__bubble"><div class="portal-message-thread__meta"><strong>Sender six</strong><span>09:06</span></div><p>Incoming message in a group.</p></div></article>
            <article class="portal-message-thread__message is-own"><div class="portal-message-thread__bubble"><div class="portal-message-thread__meta"><strong>Me</strong><span>09:07</span></div><p>Own message.</p></div><span class="portal-message-thread__avatar" aria-hidden="true">M</span></article>
            </div></div></section>
            """,
    };
}

/// <summary>
/// The theme contract on the Auth login page, which has no top bar and runs under
/// the strictest CSP (script-src 'self', style-src 'self').
/// </summary>
[Collection("ui")]
[Trait("Category", "Ui")]
public sealed class AuthThemeContractTests(PlaywrightSessionFixture playwright, AuthAppFixture app, ITestOutputHelper output)
{
    private const string PagePath = "/login";

    [SkippableFact]
    public Task Menu_switches_theme_and_the_choice_survives_reload()
        => ThemeScenarios.MenuSwitchesAndPersistsAsync(playwright, app, PagePath);

    [SkippableFact]
    public Task Menu_works_with_the_keyboard_and_returns_focus()
        => ThemeScenarios.KeyboardAsync(playwright, app, PagePath);

    [SkippableFact]
    public Task System_mode_follows_prefers_color_scheme()
        => ThemeScenarios.SystemFollowsColorSchemeAsync(playwright, app, PagePath);

    [SkippableFact]
    public Task Denied_storage_still_switches_for_the_session()
        => ThemeScenarios.DeniedStorageStillSwitchesAsync(playwright, app, PagePath);

    [SkippableFact]
    public Task A_newer_local_choice_beats_an_older_cookie()
        => ThemeScenarios.NewerLocalChoiceBeatsOlderCookieAsync(playwright, app, PagePath);

    [SkippableTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public Task Text_is_readable_in_both_themes(string theme)
        => ThemeScenarios.TextIsReadableAsync(playwright, app, output, PagePath, theme, "login", specimen: null);
}

internal static class ThemeScenarios
{
    private const string PreferenceName = "OMP_THEME_PREFERENCE";
    private const string OtherAppPreferenceName = "ODV_USER_PREFERENCES";
    private const string OtherAppPreferenceValue = "{\"theme\":\"normal\",\"zoom\":1.25}";

    // Minimum text contrast the pages must keep in both palettes. WCAG AA for normal
    // text is 4.5:1; the gate sits at 3:1 (AA large text / non-text) for now, while
    // the remaining hard-coded colours are migrated (docs/THEME_CONTRACT.md). Every
    // element between 3:1 and 4.5:1 is reported as a warning without failing.
    private const double MinimumContrast = 3.0;
    private const double TargetContrast = 4.5;

    private static readonly string ThemeOf = "() => [document.documentElement.getAttribute('data-theme'), document.documentElement.getAttribute('data-theme-mode')]";

    public static async Task MenuSwitchesAndPersistsAsync(PlaywrightSessionFixture playwright, WebAppProcessFixture app, string path)
    {
        Skip.IfNot(playwright.Available, playwright.UnavailableReason);
        Skip.IfNot(app.Available, app.UnavailableReason);

        await using var context = await playwright.Browser!.NewContextAsync(new BrowserNewContextOptions { ColorScheme = ColorScheme.Light });
        await context.AddInitScriptAsync($"localStorage.setItem('{OtherAppPreferenceName}', '{OtherAppPreferenceValue}');");
        var page = await context.NewPageAsync();
        var cspMessages = CollectCspMessages(page);
        await GotoAsync(page, app, path);

        // The init file runs synchronously in <head>, so the theme is set before the first paint.
        var headScript = await page.EvaluateAsync<bool>(
            "() => [...document.head.querySelectorAll('script[src]')].some(s => /\\/js\\/omp-theme\\.js$/.test(new URL(s.src).pathname) && !s.defer && !s.async)");
        Assert.True(headScript, $"{path}: omp-theme.js is not a synchronous <head> script");
        Assert.Equal(["light", "system"], await page.EvaluateAsync<string[]>(ThemeOf));
        Assert.Equal("light", await page.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).colorScheme"));

        var toggle = page.Locator("[data-omp-theme-toggle]").First;
        await toggle.ClickAsync();
        var menu = page.Locator("[data-omp-theme-menu]").First;
        await menu.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });
        Assert.Equal("true", await toggle.GetAttributeAsync("aria-expanded"));
        Assert.Equal("true", await menu.Locator("[data-omp-theme-option=system]").GetAttributeAsync("aria-checked"));

        await menu.Locator("[data-omp-theme-option=dark]").ClickAsync();
        await ExpectThemeAsync(page, "dark", "dark");
        Assert.Equal("dark", await page.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).colorScheme"));
        await menu.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 5000 });
        Assert.True(await toggle.EvaluateAsync<bool>("el => el === document.activeElement"), $"{path}: focus did not return to the theme toggle");

        var cookie = (await context.CookiesAsync()).SingleOrDefault(c => c.Name == PreferenceName);
        Assert.NotNull(cookie);
        Assert.Equal("/", cookie.Path);
        Assert.Equal(SameSiteAttribute.Lax, cookie.SameSite);
        Assert.False(cookie.HttpOnly);
        Assert.False(cookie.Domain.StartsWith('.'), $"cookie must be host-only by default, got domain {cookie.Domain}");
        AssertPreference(Uri.UnescapeDataString(cookie.Value), "dark");
        AssertPreference(await page.EvaluateAsync<string>($"() => localStorage.getItem('{PreferenceName}')"), "dark");

        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        Assert.Equal(["dark", "dark"], await page.EvaluateAsync<string[]>(ThemeOf));

        // Back to System: the stored value changes, the palette follows the (light) system.
        await toggle.ClickAsync();
        Assert.Equal("true", await menu.Locator("[data-omp-theme-option=dark]").GetAttributeAsync("aria-checked"));
        await menu.Locator("[data-omp-theme-option=system]").ClickAsync();
        await ExpectThemeAsync(page, "light", "system");

        // Another application's preferences are never touched.
        Assert.Equal(OtherAppPreferenceValue, await page.EvaluateAsync<string>($"() => localStorage.getItem('{OtherAppPreferenceName}')"));
        Assert.DoesNotContain(await context.CookiesAsync(), c => c.Name == OtherAppPreferenceName);
        Assert.True(cspMessages.Count == 0, $"{path}: CSP messages:\n - " + string.Join("\n - ", cspMessages));
    }

    public static async Task KeyboardAsync(PlaywrightSessionFixture playwright, WebAppProcessFixture app, string path)
    {
        Skip.IfNot(playwright.Available, playwright.UnavailableReason);
        Skip.IfNot(app.Available, app.UnavailableReason);

        await using var context = await playwright.Browser!.NewContextAsync(new BrowserNewContextOptions { ColorScheme = ColorScheme.Light });
        var page = await context.NewPageAsync();
        await GotoAsync(page, app, path);

        var toggle = page.Locator("[data-omp-theme-toggle]").First;
        var menu = page.Locator("[data-omp-theme-menu]").First;
        await toggle.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        Assert.True(await menu.IsVisibleAsync(), $"{path}: Enter did not open the theme menu");
        Assert.Equal("system", await ActiveOptionAsync(page));

        await page.Keyboard.PressAsync("ArrowDown");
        Assert.Equal("light", await ActiveOptionAsync(page));
        await page.Keyboard.PressAsync("ArrowDown");
        Assert.Equal("dark", await ActiveOptionAsync(page));
        await page.Keyboard.PressAsync("ArrowDown");
        Assert.Equal("system", await ActiveOptionAsync(page));
        await page.Keyboard.PressAsync("End");
        Assert.Equal("dark", await ActiveOptionAsync(page));

        // Escape closes without choosing and puts focus back on the toggle.
        await page.Keyboard.PressAsync("Escape");
        Assert.False(await menu.IsVisibleAsync(), $"{path}: Escape did not close the theme menu");
        Assert.True(await toggle.EvaluateAsync<bool>("el => el === document.activeElement"), $"{path}: Escape did not return focus to the toggle");
        Assert.Equal(["light", "system"], await page.EvaluateAsync<string[]>(ThemeOf));

        // ArrowDown on the toggle opens, Enter on an option chooses it.
        await page.Keyboard.PressAsync("ArrowDown");
        await page.Keyboard.PressAsync("ArrowDown");
        await page.Keyboard.PressAsync("ArrowDown");
        Assert.Equal("dark", await ActiveOptionAsync(page));
        await page.Keyboard.PressAsync("Enter");
        await ExpectThemeAsync(page, "dark", "dark");
        Assert.True(await toggle.EvaluateAsync<bool>("el => el === document.activeElement"), $"{path}: focus did not return to the toggle after a choice");
    }

    public static async Task SystemFollowsColorSchemeAsync(PlaywrightSessionFixture playwright, WebAppProcessFixture app, string path)
    {
        Skip.IfNot(playwright.Available, playwright.UnavailableReason);
        Skip.IfNot(app.Available, app.UnavailableReason);

        await using var context = await playwright.Browser!.NewContextAsync(new BrowserNewContextOptions { ColorScheme = ColorScheme.Dark });
        var page = await context.NewPageAsync();
        await GotoAsync(page, app, path);
        Assert.Equal(["dark", "system"], await page.EvaluateAsync<string[]>(ThemeOf));

        // A system change while the page is open follows through in System mode...
        await page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Light });
        await page.WaitForFunctionAsync("() => document.documentElement.getAttribute('data-theme') === 'light'");
        Assert.Equal(["light", "system"], await page.EvaluateAsync<string[]>(ThemeOf));

        // ...but never overrides an explicit choice.
        await page.EvaluateAsync("() => window.ompTheme.setMode('light')");
        await page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Dark });
        await page.WaitForTimeoutAsync(100);
        Assert.Equal(["light", "light"], await page.EvaluateAsync<string[]>(ThemeOf));
    }

    public static async Task DeniedStorageStillSwitchesAsync(PlaywrightSessionFixture playwright, WebAppProcessFixture app, string path)
    {
        Skip.IfNot(playwright.Available, playwright.UnavailableReason);
        Skip.IfNot(app.Available, app.UnavailableReason);

        await using var context = await playwright.Browser!.NewContextAsync(new BrowserNewContextOptions { ColorScheme = ColorScheme.Light });
        // Both stores refuse, like a browser with site data blocked.
        await context.AddInitScriptAsync("""
            Object.defineProperty(window, 'localStorage', { get() { throw new DOMException('denied', 'SecurityError'); } });
            Object.defineProperty(Document.prototype, 'cookie', { get() { throw new DOMException('denied', 'SecurityError'); }, set() { throw new DOMException('denied', 'SecurityError'); } });
            """);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await GotoAsync(page, app, path);
        Assert.Equal(["light", "system"], await page.EvaluateAsync<string[]>(ThemeOf));

        await page.Locator("[data-omp-theme-toggle]").First.ClickAsync();
        await page.Locator("[data-omp-theme-menu] [data-omp-theme-option=dark]").First.ClickAsync();
        await ExpectThemeAsync(page, "dark", "dark");

        // Focus/visibility re-reads must keep the session choice, not fall back to System.
        await page.EvaluateAsync("() => window.dispatchEvent(new Event('focus'))");
        Assert.Equal(["dark", "dark"], await page.EvaluateAsync<string[]>(ThemeOf));

        // A DOM patcher (Blazor enhanced navigation) that drops the attributes makes the
        // script restore them from storage. With both stores denied only the session
        // value knows the choice was Dark; without it this falls back to System = light.
        await page.EvaluateAsync("() => { document.documentElement.removeAttribute('data-theme'); document.documentElement.removeAttribute('data-theme-mode'); }");
        await ExpectThemeAsync(page, "dark", "dark");
        Assert.DoesNotContain(errors, e => e.Contains("omp-theme", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task NewerLocalChoiceBeatsOlderCookieAsync(PlaywrightSessionFixture playwright, WebAppProcessFixture app, string path)
    {
        Skip.IfNot(playwright.Available, playwright.UnavailableReason);
        Skip.IfNot(app.Available, app.UnavailableReason);

        await using var context = await playwright.Browser!.NewContextAsync(new BrowserNewContextOptions { ColorScheme = ColorScheme.Light });
        // Cookie writes are silently dropped and an old Light choice stays readable,
        // while localStorage works: the mirror ends up newer than the cookie.
        var staleCookie = Uri.EscapeDataString("{\"version\":1,\"mode\":\"light\",\"revision\":\"1-stale\"}");
        await context.AddInitScriptAsync(
            "Object.defineProperty(Document.prototype, 'cookie', { get() { return '" + PreferenceName + "=" + staleCookie + "'; }, set() { } });");
        var page = await context.NewPageAsync();
        await GotoAsync(page, app, path);
        Assert.Equal(["light", "light"], await page.EvaluateAsync<string[]>(ThemeOf));

        await page.EvaluateAsync("() => window.ompTheme.setMode('dark')");
        await ExpectThemeAsync(page, "dark", "dark");
        AssertPreference(await page.EvaluateAsync<string>($"() => localStorage.getItem('{PreferenceName}')"), "dark");

        // The revision orders the stores: the older cookie must not undo the choice...
        await page.EvaluateAsync("() => window.dispatchEvent(new Event('focus'))");
        Assert.Equal(["dark", "dark"], await page.EvaluateAsync<string[]>(ThemeOf));

        // ...nor win on the next page load.
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        Assert.Equal(["dark", "dark"], await page.EvaluateAsync<string[]>(ThemeOf));
    }

    public static async Task TextIsReadableAsync(
        PlaywrightSessionFixture playwright, WebAppProcessFixture app, ITestOutputHelper output, string path, string theme, string screenshotName, string? specimen)
    {
        Skip.IfNot(playwright.Available, playwright.UnavailableReason);
        Skip.IfNot(app.Available, app.UnavailableReason);

        await using var context = await playwright.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = ColorScheme.Light,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
        });
        var host = new Uri(app.BaseUrl).Host;
        await context.AddCookiesAsync(
        [
            new Cookie
            {
                Name = PreferenceName,
                Value = Uri.EscapeDataString($"{{\"version\":1,\"mode\":\"{theme}\",\"revision\":\"ui-test\"}}"),
                Domain = host,
                Path = "/",
                SameSite = SameSiteAttribute.Lax,
            },
        ]);
        var page = await context.NewPageAsync();
        await GotoAsync(page, app, path);
        Assert.Equal([theme, theme], await page.EvaluateAsync<string[]>(ThemeOf));

        // The generic invariants include "text colour equals background colour". They
        // judge the page as served, before any specimen is added.
        var findings = await UiInvariantScanner.ScanAsync(page);
        Assert.True(findings.Count == 0, $"{path} in {theme}:\n - " + string.Join("\n - ", findings));

        if (specimen is not null)
        {
            await page.EvaluateAsync(
                "(html) => { const host = document.createElement('div'); host.setAttribute('data-theme-specimen', ''); host.innerHTML = html; (document.querySelector('main') || document.body).prepend(host); }",
                specimen);
        }

        var screenshotDirectory = Path.Join(app.RepoRoot, "TestResults", "ui-theme");
        Directory.CreateDirectory(screenshotDirectory);
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Join(screenshotDirectory, $"{screenshotName}-{theme}.png"),
            FullPage = true,
        });

        var contrast = await page.EvaluateAsync<ContrastReport>(ContrastScript);
        Assert.True(contrast.Checked > 0, $"{path} in {theme}: no text measured");
        var measured = contrast.Items.ToList();

        // Hover states: every button in the content area, one at a time, measured once
        // its transition has finished.
        var buttons = page.Locator("main .btn");
        var buttonCount = Math.Min(await buttons.CountAsync(), 80);
        for (var i = 0; i < buttonCount; i++)
        {
            var button = buttons.Nth(i);
            if (!await button.IsVisibleAsync())
            {
                continue;
            }

            await button.HoverAsync(new LocatorHoverOptions { Force = true, Timeout = 5000 });
            await button.EvaluateAsync("el => Promise.all(el.getAnimations().map(a => a.finished.catch(() => null)))");
            var hovered = await button.EvaluateAsync<ContrastReport>(ContrastScript);
            measured.AddRange(hovered.Items.Select(item => { item.Element += ":hover"; return item; }));
        }

        var warnings = measured.Where(item => item.Ratio >= MinimumContrast).ToArray();
        if (warnings.Length > 0)
        {
            var report = $"{path} in {theme}: {warnings.Length} text elements below {TargetContrast}:1 (warning, not failing yet)\n - "
                + string.Join("\n - ", warnings.Select(Describe));
            output.WriteLine("WARNING " + report);
            await File.WriteAllTextAsync(Path.Join(screenshotDirectory, $"{screenshotName}-{theme}.contrast-warnings.txt"), report);
        }

        var failing = measured.Where(item => item.Ratio < MinimumContrast).ToArray();
        Assert.True(
            failing.Length == 0,
            $"{path} in {theme}: {failing.Length} text elements below {MinimumContrast}:1\n - "
                + string.Join("\n - ", failing.Select(Describe)));
    }

    private static string Describe(ContrastItem item)
        => $"{item.Ratio:0.00}:1 {item.Element} \"{item.Text}\" ({item.Color} on {item.Background})";

    private static async Task GotoAsync(IPage page, WebAppProcessFixture app, string path)
    {
        var response = await page.GotoAsync(app.BaseUrl + path, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        Assert.NotNull(response);
        Assert.True(response.Status == 200, $"{path} answered {response.Status}, expected 200");
    }

    // The switch acts synchronously, but a full-suite run on a busy machine has shown one
    // snapshot read losing a race; wait for the state instead of sampling it once.
    private static async Task ExpectThemeAsync(IPage page, string theme, string mode)
    {
        try
        {
            await page.WaitForFunctionAsync(
                "([t, m]) => document.documentElement.getAttribute('data-theme') === t && document.documentElement.getAttribute('data-theme-mode') === m",
                new object[] { theme, mode },
                new PageWaitForFunctionOptions { Timeout = 5000 });
        }
        catch (TimeoutException)
        {
            Assert.Equal([theme, mode], await page.EvaluateAsync<string[]>(ThemeOf));
        }
    }

    private static Task<string> ActiveOptionAsync(IPage page)
        => page.EvaluateAsync<string>("() => document.activeElement && document.activeElement.getAttribute('data-omp-theme-option')");

    private static List<string> CollectCspMessages(IPage page)
    {
        var messages = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase)
                || message.Text.Contains("Refused to", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(message.Text);
            }
        };
        return messages;
    }

    private static void AssertPreference(string? json, string expectedMode)
    {
        Assert.False(string.IsNullOrEmpty(json), "no stored preference");
        using var document = JsonDocument.Parse(json!);
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(expectedMode, document.RootElement.GetProperty("mode").GetString());
        Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("revision").GetString()));
    }

    public sealed class ContrastReport
    {
        public int Checked { get; set; }
        public ContrastItem[] Items { get; set; } = [];
    }

    public sealed class ContrastItem
    {
        public double Ratio { get; set; }
        public string Element { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string Color { get; set; } = string.Empty;
        public string Background { get; set; } = string.Empty;
    }

    // Contrast of every visible element with its own text against the nearest opaque
    // background, alpha-blended: the whole document, or one element and what it holds
    // when called on an element handle. Only elements below 4.5:1 are returned.
    // Background images and gradients are not measured.
    private const string ContrastScript = """
        (target) => {
            const parse = (value) => {
                const m = value.match(/rgba?\(([^)]+)\)/);
                if (!m) { return null; }
                const p = m[1].split(/[ ,\/]+/).filter(Boolean).map(Number);
                return { r: p[0], g: p[1], b: p[2], a: p.length > 3 ? p[3] : 1 };
            };
            const blend = (top, bottom) => ({
                r: top.r * top.a + bottom.r * (1 - top.a),
                g: top.g * top.a + bottom.g * (1 - top.a),
                b: top.b * top.a + bottom.b * (1 - top.a),
                a: 1,
            });
            const backgroundOf = (el) => {
                const layers = [];
                for (let node = el; node; node = node.parentElement) {
                    const c = parse(getComputedStyle(node).backgroundColor);
                    if (c && c.a > 0) { layers.push(c); if (c.a >= 1) { break; } }
                }
                let result = { r: 255, g: 255, b: 255, a: 1 };
                if (layers.length > 0 && layers[layers.length - 1].a >= 1) { result = layers.pop(); }
                while (layers.length > 0) { result = blend(layers.pop(), result); }
                return result;
            };
            const luminance = (c) => {
                const f = (v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
                return 0.2126 * f(c.r) + 0.7152 * f(c.g) + 0.0722 * f(c.b);
            };
            const describe = (el) => el.tagName.toLowerCase() + (el.id ? '#' + el.id : '') + (el.classList.length ? '.' + [...el.classList].join('.') : '');
            const ownText = (el) => [...el.childNodes].filter(n => n.nodeType === 3).map(n => n.textContent).join('').trim();
            const items = [];
            let checked = 0;
            const candidates = target instanceof Element ? [target, ...target.querySelectorAll('*')] : document.querySelectorAll('body *');
            for (const el of candidates) {
                if (!el.checkVisibility || !el.checkVisibility({ opacityProperty: true, visibilityProperty: true })) { continue; }
                // Decorative (aria-hidden) glyphs and screen-reader-only text are not read visually.
                if (el.closest('[aria-hidden="true"]')) { continue; }
                const rect = el.getBoundingClientRect();
                if (rect.width <= 1 || rect.height <= 1) { continue; }
                const text = ownText(el);
                if (text.length === 0) { continue; }
                const style = getComputedStyle(el);
                const fg = parse(style.color);
                if (!fg) { continue; }
                const bg = backgroundOf(el);
                const color = fg.a < 1 ? blend(fg, bg) : fg;
                const l1 = luminance(color), l2 = luminance(bg);
                const ratio = (Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05);
                checked++;
                if (ratio < 4.5) {
                    items.push({
                        ratio: Math.round(ratio * 100) / 100,
                        element: describe(el),
                        text: text.slice(0, 40),
                        color: style.color,
                        background: `rgb(${Math.round(bg.r)}, ${Math.round(bg.g)}, ${Math.round(bg.b)})`,
                    });
                }
            }
            return { checked, items };
        }
        """;
}
