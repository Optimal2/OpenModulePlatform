// File: OpenModulePlatform.UiTests/PortalCspSmokeTests.cs
using Microsoft.Playwright;
using OpenModulePlatform.TestSupport.Ui;

namespace OpenModulePlatform.UiTests;

/// <summary>
/// CSP smoke test for the Portal inline-script migration (campaign
/// csp-vagen-till-enforcement): the Portal's script-src dropped 'unsafe-inline',
/// so every page that used to carry an inline block must now load clean — no
/// browser CSP console messages, no POSTs to /omp/csp-report, and no executable
/// inline script elements left in the rendered markup. Skips with a reason when
/// the app or browser is unavailable, like the other UI suites.
/// </summary>
[Collection("ui")]
[Trait("Category", "Ui")]
public sealed class PortalCspSmokeTests(PlaywrightSessionFixture playwright, PortalAppFixture app)
{
    // The parameterless pages that carried the 16 inline blocks before the
    // migration. Parameterised edit pages (Role, ModuleEdit, AppInstanceEdit,
    // InstanceTemplateAppEdit, Messages/Thread) load the same static files; the
    // static-file probe below covers those assets directly.
    public static TheoryData<string> Pages =>
    [
        "/Notifications",
        "/Messages",
        "/Admin/ArtifactUpload",
        "/Admin/ConfigSettings",
        "/Admin/IFrameUrls",
        "/Admin/Maintenance",
        "/Admin/ModulePackageImport",
        "/Admin/Navigation",
        "/Admin/UniversalPackageBuilder",
        "/Admin/PortalEntries"
    ];

    public static TheoryData<string> MigratedScripts =>
    [
        "/js/validation-scripts.js",
        "/js/notifications-page.js",
        "/js/messages-list-page.js",
        "/js/message-thread-page.js",
        "/js/admin-app-instance-edit.js",
        "/js/admin-artifact-upload.js",
        "/js/admin-config-settings.js",
        "/js/admin-iframe-urls.js",
        "/js/admin-instance-template-app-edit.js",
        "/js/admin-maintenance.js",
        "/js/admin-module-edit.js",
        "/js/admin-module-package-import.js",
        "/js/admin-navigation-links.js",
        "/js/admin-portal-entries.js",
        "/js/admin-universal-package-builder.js",
        "/js/admin-role-edit.js"
    ];

    [SkippableTheory]
    [MemberData(nameof(Pages))]
    public async Task Migrated_page_reports_no_csp_violations(string path)
    {
        Skip.IfNot(playwright.Available, playwright.UnavailableReason);
        Skip.IfNot(app.Available, app.UnavailableReason);

        await using var context = await playwright.Browser!.NewContextAsync();
        var page = await context.NewPageAsync();

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

        var response = await page.GotoAsync(
            app.BaseUrl + path,
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        Assert.NotNull(response);
        Assert.True(response.Status == 200, $"{path} answered {response.Status}, expected 200");

        // No executable inline scripts may remain: every <script> in the
        // rendered DOM needs a src (or a non-JavaScript data-block type).
        var inlineScripts = await page.EvalOnSelectorAllAsync<string[]>(
            "script",
            """els => els.filter(e => !e.src && (!e.type || /javascript|module|^importmap$/i.test(e.type))).map(e => e.outerHTML.slice(0, 120))""");
        Assert.True(
            inlineScripts.Length == 0,
            $"{path} still renders executable inline scripts:\n - " + string.Join("\n - ", inlineScripts));

        Assert.True(
            cspConsoleMessages.Count == 0,
            $"{path} produced CSP console messages:\n - " + string.Join("\n - ", cspConsoleMessages));
        Assert.True(
            cspReportPosts.Count == 0,
            $"{path} posted CSP violation reports:\n - " + string.Join("\n - ", cspReportPosts));
    }

    [SkippableTheory]
    [MemberData(nameof(MigratedScripts))]
    public async Task Migrated_static_script_is_served(string scriptPath)
    {
        Skip.IfNot(playwright.Available, playwright.UnavailableReason);
        Skip.IfNot(app.Available, app.UnavailableReason);

        await using var context = await playwright.Browser!.NewContextAsync();
        var response = await context.APIRequest.GetAsync(app.BaseUrl + scriptPath);
        Assert.True(response.Status == 200, $"{scriptPath} answered {response.Status}, expected 200");
    }
}
