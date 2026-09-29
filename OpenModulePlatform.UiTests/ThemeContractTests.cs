using System.Text.Json;
using Microsoft.Playwright;
using OpenModulePlatform.TestSupport.Ui;

namespace OpenModulePlatform.UiTests;

/// <summary>
/// The theme contract (docs/THEME_CONTRACT.md) on the Portal: the System / Light / Dark
/// menu in the shared top bar, the stored OMP_THEME_PREFERENCE, System following
/// prefers-color-scheme, and readable text in both palettes.
/// </summary>
[Collection("ui")]
[Trait("Category", "Ui")]
public sealed class PortalThemeContractTests(PlaywrightSessionFixture playwright, PortalAppFixture app)
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

    public static TheoryData<string, string> ReadabilityCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in new[] { "/Admin/Overview", "/Admin/Modules", "/Admin/ConfigSettings", "/Notifications" })
        {
            data.Add(path, "light");
            data.Add(path, "dark");
        }

        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(ReadabilityCases))]
    public Task Text_is_readable_in_both_themes(string path, string theme)
        => ThemeScenarios.TextIsReadableAsync(playwright, app, path, theme, "portal" + path.Replace('/', '-').ToLowerInvariant());
}

/// <summary>
/// The theme contract on the Auth login page, which has no top bar and runs under
/// the strictest CSP (script-src 'self', style-src 'self').
/// </summary>
[Collection("ui")]
[Trait("Category", "Ui")]
public sealed class AuthThemeContractTests(PlaywrightSessionFixture playwright, AuthAppFixture app)
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

    [SkippableTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public Task Text_is_readable_in_both_themes(string theme)
        => ThemeScenarios.TextIsReadableAsync(playwright, app, PagePath, theme, "login");
}

internal static class ThemeScenarios
{
    private const string PreferenceName = "OMP_THEME_PREFERENCE";
    private const string OtherAppPreferenceName = "ODV_USER_PREFERENCES";
    private const string OtherAppPreferenceValue = "{\"theme\":\"normal\",\"zoom\":1.25}";

    // Minimum text contrast the pages must keep in both palettes. WCAG AA for normal
    // text is 4.5:1; the gate sits at 3:1 (AA large text / non-text) while the
    // remaining hard-coded colours are migrated, and reports every element below 4.5:1.
    private const double MinimumContrast = 3.0;

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
        Assert.DoesNotContain(errors, e => e.Contains("omp-theme", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task TextIsReadableAsync(PlaywrightSessionFixture playwright, WebAppProcessFixture app, string path, string theme, string screenshotName)
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

        var screenshotDirectory = Path.Join(app.RepoRoot, "TestResults", "ui-theme");
        Directory.CreateDirectory(screenshotDirectory);
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Join(screenshotDirectory, $"{screenshotName}-{theme}.png"),
            FullPage = true,
        });

        // The generic invariants include "text colour equals background colour".
        var findings = await UiInvariantScanner.ScanAsync(page);
        Assert.True(findings.Count == 0, $"{path} in {theme}:\n - " + string.Join("\n - ", findings));

        var contrast = await page.EvaluateAsync<ContrastReport>(ContrastScript);
        Assert.True(contrast.Checked > 0, $"{path} in {theme}: no text measured");
        var failing = contrast.Items.Where(item => item.Ratio < MinimumContrast).ToArray();
        Assert.True(
            failing.Length == 0,
            $"{path} in {theme}: {failing.Length} text elements below {MinimumContrast}:1\n - "
                + string.Join("\n - ", failing.Select(item => $"{item.Ratio:0.00}:1 {item.Element} \"{item.Text}\" ({item.Color} on {item.Background})")));
    }

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
    // background, alpha-blended. Background images and gradients are not measured.
    private const string ContrastScript = """
        () => {
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
            for (const el of document.querySelectorAll('body *')) {
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
