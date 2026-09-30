using OpenModulePlatform.TestSupport.Ui;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace OpenModulePlatform.UiTests;

/// <summary>
/// The theme contract (docs/THEME_CONTRACT.md) on the platform's module web apps: the
/// examples (Razor, Blazor, service and worker web parts) and the content and iFrame
/// modules. Same scenarios and the same 4.5:1 text contrast gate as the Portal
/// (<see cref="PortalThemeContractTests"/>), in both palettes and on hover, here
/// including the app's own header, navigation and theme menu.
/// </summary>
public abstract class ModuleThemeContractTests(PlaywrightSessionFixture playwright, WebAppProcessFixture app, ITestOutputHelper output)
{
    // Measured again on hover: controls and table rows in the content area, and the
    // links and own theme menu of the app's header (the shared top bar has its own tests).
    private const string HoverSelector =
        "main .btn, main button, main .button, main .action-link, main .grid tbody tr, main a.omp-error-view__button, "
        + ".app-header .nav a, .app-header .topbar-nav a, .app-header .app-theme-switch [data-omp-theme-toggle]";

    /// <summary>Screenshot and report prefix, for example "example-webapp".</summary>
    protected abstract string AppName { get; }

    /// <summary>Pages measured in both palettes; the first one also runs the switch scenario.</summary>
    protected abstract IReadOnlyList<string> Pages { get; }

    /// <summary>
    /// Markup for states a page renders only with particular data (status notices,
    /// validation errors, danger zones), inserted into the live page so it is measured
    /// with the app's real stylesheet. Keep in step with the views.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string> Specimens { get; } = new Dictionary<string, string>();

    /// <summary>
    /// Status code of a page that answers with an error but is not under /status/
    /// (for example a view whose record does not exist); other pages expect 200.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, int> PageStatuses { get; } = new Dictionary<string, int>();

    [SkippableFact]
    public Task Menu_switches_theme_and_the_choice_survives_reload()
        => ThemeScenarios.MenuSwitchesAndPersistsAsync(playwright, app, Pages[0]);

    [SkippableFact]
    public Task Printing_keeps_the_light_palette()
        => ThemeScenarios.PrintKeepsTheLightPaletteAsync(playwright, app, Pages[0]);

    [SkippableTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Text_is_readable_in_both_themes(string theme)
    {
        // Every page is measured even when an earlier one fails, so one run lists them all.
        var failures = new List<string>();
        foreach (var path in Pages)
        {
            try
            {
                await ThemeScenarios.TextIsReadableAsync(
                    playwright,
                    app,
                    output,
                    path,
                    theme,
                    AppName + ScreenshotSuffix(path),
                    Specimens.GetValueOrDefault(path),
                    HoverSelector,
                    PageStatuses.TryGetValue(path, out var status) ? status : null);
            }
            catch (XunitException ex)
            {
                failures.Add(ex.Message);
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }

    private static string ScreenshotSuffix(string path)
    {
        var trimmed = path.Split('?')[0].Trim('/');
        return trimmed.Length == 0 ? string.Empty : "-" + trimmed.Replace('/', '-').ToLowerInvariant();
    }
}

[Collection("ui")]
[Trait("Category", "Ui")]
public sealed class ExampleWebAppThemeContractTests(PlaywrightSessionFixture playwright, ExampleWebAppModuleFixture app, ITestOutputHelper output)
    : ModuleThemeContractTests(playwright, app, output)
{
    protected override string AppName => "example-webapp";

    protected override IReadOnlyList<string> Pages { get; } =
        ["/", "/configurations", "/opendocviewer-demo", "/status/404"];

    // Index.cshtml: the notices after a queued or refused action.
    protected override IReadOnlyDictionary<string, string> Specimens { get; } = new Dictionary<string, string>
    {
        ["/"] = """
            <div class="notice notice--success" role="status">The configuration was saved.</div>
            <div class="notice notice--warning" role="status">The configuration could not be saved.</div>
            """,
    };
}

[Collection("ui")]
[Trait("Category", "Ui")]
public sealed class ExampleServiceAppThemeContractTests(PlaywrightSessionFixture playwright, ExampleServiceAppModuleFixture app, ITestOutputHelper output)
    : ModuleThemeContractTests(playwright, app, output)
{
    protected override string AppName => "example-serviceapp";

    protected override IReadOnlyList<string> Pages { get; } =
        ["/", "/appinstances", "/configurations", "/jobs", "/opendocviewer-demo", "/status/404"];
}

[Collection("ui")]
[Trait("Category", "Ui")]
public sealed class ExampleWorkerAppThemeContractTests(PlaywrightSessionFixture playwright, ExampleWorkerAppModuleFixture app, ITestOutputHelper output)
    : ModuleThemeContractTests(playwright, app, output)
{
    protected override string AppName => "example-workerapp";

    protected override IReadOnlyList<string> Pages { get; } =
        ["/", "/appinstances", "/configurations", "/jobs", "/opendocviewer-demo", "/status/404"];
}

[Collection("ui")]
[Trait("Category", "Ui")]
public sealed class ExampleWebAppBlazorThemeContractTests(PlaywrightSessionFixture playwright, ExampleWebAppBlazorModuleFixture app, ITestOutputHelper output)
    : ModuleThemeContractTests(playwright, app, output)
{
    protected override string AppName => "example-blazor";

    protected override IReadOnlyList<string> Pages { get; } =
        ["/", "/configurations", "/configurations/edit", "/opendocviewer-demo"];

    // Configurations/Edit.razor: the status banner and the EditForm's validation output
    // (ValidationSummary and ValidationMessage).
    protected override IReadOnlyDictionary<string, string> Specimens { get; } = new Dictionary<string, string>
    {
        ["/configurations/edit"] = """
            <div class="card status-banner">The configuration was saved.</div>
            <ul class="validation-errors"><li class="validation-message">Config JSON is not valid JSON.</li></ul>
            <div class="validation-message">Config JSON is required.</div>
            """,
    };
}

[Collection("ui")]
[Trait("Category", "Ui")]
public sealed class ContentAppThemeContractTests(PlaywrightSessionFixture playwright, ContentAppFixture app, ITestOutputHelper output)
    : ModuleThemeContractTests(playwright, app, output)
{
    protected override string AppName => "content";

    // The admin pages need a content manager (403 for the anonymous test user), so
    // their markup is measured through the specimen on the start page, which loads the
    // same stylesheet.
    protected override IReadOnlyList<string> Pages { get; } = ["/", "/status/404"];

    // Page.cshtml (page body with code), the server report partial and
    // omp-server-report.js (errors), Admin/Index.cshtml (pills, primary button, table)
    // and Admin/Edit.cshtml (editor tabs, editor field, validation, enable/disable and
    // the delete zone).
    protected override IReadOnlyDictionary<string, string> Specimens { get; } = new Dictionary<string, string>
    {
        ["/"] = """
            <div class="card status-banner">The page was saved.</div>
            <div class="server-report"><div class="server-report__error">The report could not be run.</div><p class="server-report__empty">No rows.</p></div>
            <section class="server-report--error"><p>The report is not available.</p></section>
            <article class="content-page"><div class="content-page__header"><h1>Page title</h1></div>
            <div class="content-page__body"><p>Use <code>omp.pages</code> for the list.</p><pre>select 1;</pre></div></article>
            <div class="toolbar"><a class="button" href="#">Open page</a> <a class="button button--primary" href="#">New page</a>
            <button type="button" class="button--primary">Save page</button> <button type="button" class="button--danger">Disable</button>
            <button type="button" class="button--secondary">Enable</button></div>
            <p><span class="pill">HTML files: 2</span> <span class="pill pill--ok">Enabled</span></p>
            <table class="grid"><thead><tr><th>Title</th><th>State</th></tr></thead>
            <tbody><tr><td>First page</td><td><span class="pill pill--ok">Enabled</span></td></tr><tr><td>Second page</td><td><span class="pill">Disabled</span></td></tr></tbody></table>
            <div class="editor-tabs" role="tablist"><button type="button" class="editor-tab" role="tab" aria-selected="true">Visual</button><button type="button" class="editor-tab" role="tab" aria-selected="false">Source</button></div>
            <div class="content-editor-field"><label for="specimen-body">Body</label><textarea id="specimen-body">Text</textarea><span class="field-validation-error">The body is required.</span></div>
            <section class="card content-metadata"><h2>Details</h2><dl><dt>Created</dt><dd>2026-09-30</dd></dl></section>
            <section class="card danger-zone"><h2>Delete page</h2><p class="muted">Deleting cannot be undone.</p><button type="button" class="button--danger">Delete permanently</button></section>
            """,
    };
}

[Collection("ui")]
[Trait("Category", "Ui")]
public sealed class IFrameAppThemeContractTests(PlaywrightSessionFixture playwright, IFrameAppFixture app, ITestOutputHelper output)
    : ModuleThemeContractTests(playwright, app, output)
{
    protected override string AppName => "iframe";

    // /standalone/0 is the standalone view (Standalone.cshtml, HideModuleTopbar) of a URL
    // that is not configured: it answers 404 with the message card, inside the shared top bar.
    protected override IReadOnlyList<string> Pages { get; } = ["/", "/status/404", "/standalone/0"];

    protected override IReadOnlyDictionary<string, int> PageStatuses { get; } = new Dictionary<string, int> { ["/standalone/0"] = 404 };

    // _IFrameDisplay.cshtml: the message shown when no URL can be displayed.
    protected override IReadOnlyDictionary<string, string> Specimens { get; } = new Dictionary<string, string>
    {
        ["/"] = """
            <section class="card iframe-message"><h2>iFrame unavailable</h2><p class="muted">The selected URL is not allowed.</p></section>
            """,
    };
}

/// <summary>
/// A module web app with the shared top bar switched off (PortalTopBar:Enabled=false)
/// renders the theme menu in its own header instead: exactly one, and it works.
/// </summary>
[Collection("ui")]
[Trait("Category", "Ui")]
public sealed class ExampleWebAppWithoutTopBarThemeContractTests(PlaywrightSessionFixture playwright, ExampleWebAppModuleNoTopBarFixture app, ITestOutputHelper output)
    : ModuleThemeContractTests(playwright, app, output)
{
    protected override string AppName => "example-webapp-no-topbar";

    protected override IReadOnlyList<string> Pages { get; } = ["/"];
}

/// <summary>
/// The Blazor example with the shared top bar switched off: MainLayout.razor renders the
/// shared Blazor theme menu in its own header instead, exactly once.
/// </summary>
[Collection("ui")]
[Trait("Category", "Ui")]
public sealed class ExampleWebAppBlazorWithoutTopBarThemeContractTests(PlaywrightSessionFixture playwright, ExampleWebAppBlazorModuleNoTopBarFixture app, ITestOutputHelper output)
    : ModuleThemeContractTests(playwright, app, output)
{
    protected override string AppName => "example-blazor-no-topbar";

    protected override IReadOnlyList<string> Pages { get; } = ["/", "/configurations"];
}

/// <summary>
/// The iFrame module with the shared top bar switched off: the module header carries the
/// theme menu, and the standalone view (no module header) a slim row with only the menu.
/// </summary>
[Collection("ui")]
[Trait("Category", "Ui")]
public sealed class IFrameAppWithoutTopBarThemeContractTests(PlaywrightSessionFixture playwright, IFrameAppNoTopBarFixture app, ITestOutputHelper output)
    : ModuleThemeContractTests(playwright, app, output)
{
    protected override string AppName => "iframe-no-topbar";

    protected override IReadOnlyList<string> Pages { get; } = ["/", "/standalone/0"];

    protected override IReadOnlyDictionary<string, int> PageStatuses { get; } = new Dictionary<string, int> { ["/standalone/0"] = 404 };
}
