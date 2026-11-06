using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace OpenModulePlatform.UiTests;

/// <summary>
/// Round-trip guard for the content editor (Pages/Admin/Edit.cshtml).
/// The page opens HTML content in the visual ToastUI editor and rewrites the
/// textarea on submit, so an unchanged page is still transformed when saved.
/// The harness below reads the real Edit.cshtml from the repository at test
/// time, extracts its real inline script from the Scripts section, and runs
/// it against a minimal DOM with the same ids and data attributes, plus the
/// real vendored ToastUI bundle. Nothing here needs SQL Server and nothing
/// leaves the machine: the harness document and the form POST are served and
/// intercepted by a Playwright route on a stub origin, so no socket is ever
/// opened.
/// A browser or startup failure fails these tests, never skips them.
/// </summary>
[Trait("Category", "Ui")]
public sealed partial class ContentEditorRoundTripTests
{
    // Both spellings of the query string, each element carrying a class, plus
    // a style element: exactly the constructs the visual editor is accused of
    // destroying (classes, style elements and every query parameter after the
    // first are lost on save).
    private const string FixtureBody =
        "<style>.content-note { color: #123456; }</style>\n" +
        "<p><a class=\"content-link\" href=\"https://example.test/p?a=1&b=2&c=3\">Raw ampersands</a></p>\n" +
        "<p><a class=\"content-link\" href=\"https://example.test/p?a=1&amp;b=2&amp;c=3\">Escaped ampersands</a></p>";

    [Fact(Skip = "WP2 of the content-editor campaign (lossless save) has not landed yet: the visual editor rewrites HTML on submit, so this acceptance test stays skipped until that fix removes the Skip.")]
    public async Task Saving_unchanged_html_page_from_default_tab_keeps_it()
    {
        var result = await SubmitUnchangedAsync(FixtureBody);

        Assert.Empty(result.PageErrors);
        Assert.NotNull(result.PostedBody);

        // The acceptance assertion: saving without touching anything must be a
        // no-op. Asserted as behaviour (byte-identical body plus intact query
        // parameters), so any lossless fix passes regardless of how it is
        // implemented.
        Assert.Equal(FixtureBody, result.PostedBody);

        using var links = JsonDocument.Parse(result.LinksJson);
        Assert.Equal(2, links.RootElement.GetArrayLength());
        foreach (var link in links.RootElement.EnumerateArray())
        {
            var query = link.GetProperty("params");
            Assert.Equal("1", query.GetProperty("a").GetString());
            Assert.Equal("2", query.GetProperty("b").GetString());
            Assert.Equal("3", query.GetProperty("c").GetString());
        }
    }

    [Fact]
    public async Task Vendored_editor_round_trip_is_lossy_for_the_fixture()
    {
        // Characterization test, not an acceptance test: it documents that the
        // vendored ToastUI bundle itself rewrites the fixture, which is why
        // the visual tab cannot be trusted with such a page. It passes both
        // before and after the lossless-save fix (the fix routes around the
        // visual editor instead of changing the bundle). If it starts failing
        // after a vendored editor upgrade, the round trip became lossless and
        // this guard should be reconsidered.
        var roundTripped = await RoundTripDirectlyAsync(FixtureBody);

        Assert.NotEqual(FixtureBody, roundTripped);
        Assert.DoesNotContain("<style", roundTripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("content-link", roundTripped, StringComparison.Ordinal);
        Assert.Contains("&amp;amp;", roundTripped, StringComparison.Ordinal);
    }

    private sealed record SubmitResult(string? PostedBody, string LinksJson, IReadOnlyList<string> PageErrors);

    private static async Task<SubmitResult> SubmitUnchangedAsync(string source)
    {
        var (editCshtml, editorJs) = ReadRepositoryInputs();
        var pageScript = ExtractInlineEditorScript(editCshtml);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var pageErrors = new List<string>();
        page.PageError += (_, message) => pageErrors.Add(message);

        var harnessHtml = BuildHarnessHtml(editorJs, pageScript, source);
        var posted = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/*", async route =>
        {
            if (string.Equals(route.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                posted.TrySetResult(route.Request.PostData);
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "text/html",
                    Body = "<!doctype html><html><body>saved</body></html>"
                });
            }
            else if (route.Request.IsNavigationRequest)
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "text/html",
                    Body = harnessHtml
                });
            }
            else
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 200, Body = string.Empty });
            }
        });

        // A real https origin served entirely from the route above: no socket
        // ever leaves the machine, and web storage behaves as in production.
        await page.GotoAsync(HarnessUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load });

        // The default tab is visual and the page is submitted untouched: no
        // tab clicks, no edits. Click the real submit control so the form's
        // submit handler (which pulls the visual editor value back into the
        // textarea) runs exactly as in production.
        await page.ClickAsync("#content-edit-form button[type=submit]");
        var postData = await posted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(postData);
        var postedBody = GetFormField(postData, "Input.Body");
        Assert.NotNull(postedBody);

        // Parse the posted body back in the browser, so entity handling matches
        // what a reader would actually get.
        var linksJson = await page.EvaluateAsync<string>(LinkProbeScript, postedBody);

        return new SubmitResult(postedBody, linksJson, pageErrors);
    }

    private static async Task<string> RoundTripDirectlyAsync(string source)
    {
        var (_, editorJs) = ReadRepositoryInputs();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var pageErrors = new List<string>();
        page.PageError += (_, message) => pageErrors.Add(message);

        var probeHtml =
            "<!doctype html><html><head><meta charset=\"utf-8\"></head><body>" +
            "<div id=\"editor-host\"></div>" +
            "<script>" + EscapeForInlineScript(editorJs) + "</script>" +
            "</body></html>";
        await page.RouteAsync("**/*", async route =>
        {
            if (route.Request.IsNavigationRequest)
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "text/html",
                    Body = probeHtml
                });
            }
            else
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 200, Body = string.Empty });
            }
        });

        await page.GotoAsync(HarnessUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load });

        var roundTripped = await page.EvaluateAsync<string>(
            """(html) => { const editor = new toastui.Editor({ el: document.getElementById('editor-host'), height: '300px', initialEditType: 'wysiwyg', previewStyle: 'tab', hideModeSwitch: true, initialValue: '' }); editor.setHTML(html || ''); return editor.getHTML(); }""",
            source);

        Assert.Empty(pageErrors);
        return roundTripped;
    }

    private static (string EditCshtml, string EditorJs) ReadRepositoryInputs()
    {
        var editPath = FindRepositoryFile(
            "OpenModulePlatform.Web.ContentWebAppModule", "Pages", "Admin", "Edit.cshtml");
        var editorPath = FindRepositoryFile(
            "OpenModulePlatform.Web.ContentWebAppModule", "wwwroot", "lib", "toastui-editor", "toastui-editor-all.min.js");
        return (File.ReadAllText(editPath), File.ReadAllText(editorPath));
    }

    private static string FindRepositoryFile(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = directory.FullName;
            foreach (var segment in relativePath)
            {
                candidate = Path.Combine(candidate, segment);
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        Assert.Fail($"Repository file not found: {string.Join("/", relativePath)}.");
        throw new InvalidOperationException("Unreachable: Assert.Fail always throws.");
    }

    private static string ExtractInlineEditorScript(string editCshtml)
    {
        var scriptsSection = editCshtml.IndexOf("@section Scripts", StringComparison.Ordinal);
        Assert.True(scriptsSection >= 0, "Edit.cshtml carries no Scripts section.");

        string? script = null;
        foreach (Match match in InlineScriptRegex().Matches(editCshtml))
        {
            if (match.Index > scriptsSection
                && match.Groups[1].Value.Contains("content-edit-form", StringComparison.Ordinal))
            {
                script = match.Groups[1].Value;
                break;
            }
        }

        Assert.False(
            string.IsNullOrWhiteSpace(script),
            "No inline editor script found in the Scripts section of Edit.cshtml.");
        return script!;
    }

    private static string BuildHarnessHtml(string editorJs, string pageScript, string source)
    {
        // The textarea is HTML-escaped here exactly as Razor would render the
        // persisted body, so the script under test receives the true source.
        var escapedSource = source
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

        return "<!doctype html><html><head><meta charset=\"utf-8\"></head><body>" +
            "<form method=\"post\" id=\"content-edit-form\" action=\"https://roundtrip.invalid/content-edit\">" +
            "<select id=\"Input_ContentType\" name=\"Input.ContentType\">" +
            "<option value=\"markdown\">Markdown</option>" +
            "<option value=\"html\" selected>HTML</option>" +
            "<option value=\"html_file\">HTML file</option>" +
            "<option value=\"server_report\">Server report</option>" +
            "</select>" +
            "<div data-content-editor-field>" +
            "<div data-editor-tabs hidden>" +
            "<button type=\"button\" data-editor-mode=\"visual\" aria-selected=\"true\">Visual</button>" +
            "<button type=\"button\" data-editor-mode=\"source\" data-html-source-label=\"HTML source\" data-markdown-source-label=\"Markdown source\" aria-selected=\"false\">Markdown source</button>" +
            "</div>" +
            "<div id=\"content-editor\" hidden></div>" +
            "<textarea id=\"Input_Body\" name=\"Input.Body\" rows=\"18\">" + escapedSource + "</textarea>" +
            "<span data-editor-hint=\"markdown\">Write Markdown content.</span>" +
            "<span data-editor-hint=\"html\" hidden>Write trusted HTML directly.</span>" +
            "</div>" +
            "<div data-html-file-field hidden></div>" +
            "<div data-server-report-field hidden></div>" +
            "<button type=\"submit\">Save page</button>" +
            "</form>" +
            "<script>" + EscapeForInlineScript(editorJs) + "</script>" +
            "<script>" + pageScript + "</script>" +
            "</body></html>";
    }

    private static string EscapeForInlineScript(string javaScript)
        // A literal "</script" inside an inline block would close the element
        // early; "<\/" is the same string in JavaScript and keeps the block intact.
        => InlineScriptCloserRegex().Replace(javaScript, "<\\/script");

    private static string? GetFormField(string postData, string fieldName)
    {
        foreach (var pair in postData.Split('&'))
        {
            var equals = pair.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            var name = System.Net.WebUtility.UrlDecode(pair[..equals]);
            if (string.Equals(name, fieldName, StringComparison.Ordinal))
            {
                return System.Net.WebUtility.UrlDecode(pair[(equals + 1)..]);
            }
        }

        return null;
    }

    [GeneratedRegex(@"<script(?![^>]*\bsrc\b)[^>]*>(.*?)</script\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex InlineScriptRegex();

    [GeneratedRegex("</script", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InlineScriptCloserRegex();

    // Stub origin served entirely from a Playwright route: no socket ever
    // leaves the machine, and the document gets a real origin so web storage
    // behaves as in production. The form posts back to the same stub origin
    // and is intercepted before any network access.
    private const string HarnessUrl = "https://roundtrip.invalid/content-edit";

    private const string LinkProbeScript =
        """(html) => { const doc = new DOMParser().parseFromString(html, 'text/html'); return JSON.stringify(Array.from(doc.querySelectorAll('a')).map(a => { let params = null; try { const url = new URL(a.getAttribute('href'), 'https://example.test/'); params = { a: url.searchParams.get('a'), b: url.searchParams.get('b'), c: url.searchParams.get('c') }; } catch (e) { params = { error: String((e && e.message) || e) }; } return { href: a.getAttribute('href'), params }; })); }""";
}
