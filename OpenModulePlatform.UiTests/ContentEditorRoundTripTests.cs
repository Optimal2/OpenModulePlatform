using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace OpenModulePlatform.UiTests;

/// <summary>
/// Round-trip guard for the content editor (Pages/Admin/Edit.cshtml).
/// The page used to open HTML content in the visual ToastUI editor and rewrite
/// the textarea on submit, so an unchanged page was still transformed when it
/// was saved. The harness below reads the real Edit.cshtml from the repository
/// at test time, extracts its real inline script from the Scripts section, and
/// runs it against a minimal DOM with the same ids and data attributes, plus
/// the real vendored ToastUI bundle. Nothing here needs SQL Server and nothing
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

    // A page the visual editor carries without changing it.
    private const string SafeHtmlBody = "<p>Hello</p>";

    // Markdown the visual editor does not hand back unchanged: the editor
    // rewrites the emphasis marker, so a write-back that happens without a
    // user edit shows up in the posted body.
    private const string MarkdownBody =
        "# Title\n\nSome _text_ with a [link](https://example.test/p?a=1&b=2).";

    [Fact]
    public async Task Saving_unchanged_html_page_from_default_tab_keeps_it()
    {
        PageState? state = null;
        var result = await SubmitAsync(FixtureBody, "html", async page =>
        {
            state = await ReadStateAsync(page);
        });

        Assert.Empty(result.PageErrors);
        Assert.NotNull(result.PostedBody);

        // An HTML page opens in the source tab, so the visual editor never
        // touches the stored body.
        Assert.Equal("source", state!.ActiveTab);

        // The acceptance assertion: saving without touching anything must be a
        // no-op. Asserted as behaviour (byte-identical body plus intact query
        // parameters), so any lossless fix passes regardless of how it is
        // implemented. Only the line endings are normalised: a browser submits
        // a textarea value with CRLF line breaks while the fixture uses LF,
        // and that difference is not a content change.
        Assert.Equal(FixtureBody, NormalizeLineEndings(result.PostedBody!));

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
    public async Task Unsafe_html_page_clicking_visual_stays_in_source_and_warns()
    {
        PageState? state = null;
        var result = await SubmitAsync(FixtureBody, "html", async page =>
        {
            await page.ClickAsync("[data-editor-mode=\"visual\"]");
            state = await ReadStateAsync(page);
        });

        Assert.Empty(result.PageErrors);

        // The visual editor would change this page, so the click keeps the
        // source tab and explains why.
        Assert.Equal("source", state!.ActiveTab);
        Assert.True(state.WarningVisible);

        // The body is posted unchanged.
        Assert.NotNull(result.PostedBody);
        Assert.Equal(FixtureBody, NormalizeLineEndings(result.PostedBody!));
    }

    [Fact]
    public async Task Html_source_edited_into_an_unsafe_page_keeps_the_source_tab()
    {
        const string editedBody = "<p class=\"note\">Hello</p>";
        PageState? state = null;
        var result = await SubmitAsync(SafeHtmlBody, "html", async page =>
        {
            // The page was safe when it was loaded, but the source is edited
            // into a page the visual editor would change.
            await page.ClickAsync("[data-editor-mode=\"source\"]");
            await page.FillAsync("#Input_Body", editedBody);
            await page.ClickAsync("[data-editor-mode=\"visual\"]");
            state = await ReadStateAsync(page);
        });

        Assert.Empty(result.PageErrors);

        // The round trip is checked again when Visual is clicked, so the
        // edited page keeps its source and the warning is shown.
        Assert.Equal("source", state!.ActiveTab);
        Assert.True(state.WarningVisible);

        Assert.NotNull(result.PostedBody);
        Assert.Equal(editedBody, NormalizeLineEndings(result.PostedBody!));
    }

    [Fact]
    public async Task Safe_html_page_opened_in_visual_tab_is_saved_unchanged()
    {
        PageState? state = null;
        var result = await SubmitAsync(SafeHtmlBody, "html", async page =>
        {
            await page.ClickAsync("[data-editor-mode=\"visual\"]");
            state = await ReadStateAsync(page);
        });

        Assert.Empty(result.PageErrors);

        // The visual tab is offered for a page that survives the round trip.
        Assert.Equal("visual", state!.ActiveTab);
        Assert.False(state.WarningVisible);

        // Opening the visual tab without editing is not a change.
        Assert.NotNull(result.PostedBody);
        Assert.Equal(SafeHtmlBody, NormalizeLineEndings(result.PostedBody!));
    }

    [Fact]
    public async Task Safe_html_page_edited_in_visual_tab_is_saved_with_the_edit()
    {
        var result = await SubmitAsync(SafeHtmlBody, "html", async page =>
        {
            await page.ClickAsync("[data-editor-mode=\"visual\"]");
            await page.ClickAsync(".toastui-editor-ww-container .toastui-editor-contents");
            await page.Keyboard.PressAsync("End");
            await page.Keyboard.TypeAsync(" World");
        });

        Assert.Empty(result.PageErrors);
        Assert.NotNull(result.PostedBody);

        // An edit in the visual tab is written back to the stored body.
        Assert.Contains("World", result.PostedBody!, StringComparison.Ordinal);
        Assert.NotEqual(SafeHtmlBody, NormalizeLineEndings(result.PostedBody!));
    }

    [Fact]
    public async Task Markdown_page_saved_from_the_default_visual_tab_is_unchanged()
    {
        PageState? state = null;
        var result = await SubmitAsync(MarkdownBody, "markdown", async page =>
        {
            state = await ReadStateAsync(page);
        });

        Assert.Empty(result.PageErrors);

        // Markdown keeps the visual tab as its default.
        Assert.Equal("visual", state!.ActiveTab);

        // The visual editor normalises Markdown, so an untouched page must not
        // be written back.
        Assert.NotNull(result.PostedBody);
        Assert.Equal(MarkdownBody, NormalizeLineEndings(result.PostedBody!));
    }

    [Fact]
    public async Task Switching_html_between_source_and_visual_without_editing_keeps_the_body()
    {
        string? textareaValue = null;
        var result = await SubmitAsync(SafeHtmlBody, "html", async page =>
        {
            await page.ClickAsync("[data-editor-mode=\"source\"]");
            await page.ClickAsync("[data-editor-mode=\"visual\"]");
            textareaValue = (await ReadStateAsync(page)).TextareaValue;
        });

        Assert.Empty(result.PageErrors);

        // Switching tabs without editing leaves the textarea alone.
        Assert.Equal(SafeHtmlBody, textareaValue);

        Assert.NotNull(result.PostedBody);
        Assert.Equal(SafeHtmlBody, NormalizeLineEndings(result.PostedBody!));
    }

    [Fact]
    public async Task Clicking_the_active_visual_tab_keeps_html_edits()
    {
        string editorHtml = string.Empty;
        var result = await SubmitAsync(SafeHtmlBody, "html", async page =>
        {
            await page.ClickAsync("[data-editor-mode=\"visual\"]");
            await page.ClickAsync(".toastui-editor-ww-container .ProseMirror");
            await page.Keyboard.PressAsync("End");
            await page.Keyboard.TypeAsync(" EDITED");

            // Clicking the already active Visual tab is a no-op: the edit
            // above must still be in the editor afterwards.
            await page.ClickAsync("[data-editor-mode=\"visual\"]");
            var state = await ReadStateAsync(page);
            Assert.Equal("visual", state.ActiveTab);
            editorHtml = await page.EvaluateAsync<string>(
                "() => { const view = document.querySelector('.toastui-editor-ww-container .ProseMirror'); return view ? view.innerHTML : ''; }");
        });

        Assert.Empty(result.PageErrors);
        Assert.Contains("EDITED", editorHtml, StringComparison.Ordinal);

        Assert.NotNull(result.PostedBody);
        Assert.Contains("EDITED", result.PostedBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clicking_the_active_visual_tab_keeps_markdown_edits()
    {
        string editorHtml = string.Empty;
        var result = await SubmitAsync(MarkdownBody, "markdown", async page =>
        {
            // Markdown opens on the visual tab already.
            var state = await ReadStateAsync(page);
            Assert.Equal("visual", state.ActiveTab);

            await page.ClickAsync(".toastui-editor-ww-container .ProseMirror");
            await page.Keyboard.PressAsync("End");
            await page.Keyboard.TypeAsync(" EDITED");

            // Clicking the already active Visual tab is a no-op: the edit
            // above must still be in the editor afterwards.
            await page.ClickAsync("[data-editor-mode=\"visual\"]");
            state = await ReadStateAsync(page);
            Assert.Equal("visual", state.ActiveTab);
            editorHtml = await page.EvaluateAsync<string>(
                "() => { const view = document.querySelector('.toastui-editor-ww-container .ProseMirror'); return view ? view.innerHTML : ''; }");
        });

        Assert.Empty(result.PageErrors);
        Assert.Contains("EDITED", editorHtml, StringComparison.Ordinal);

        Assert.NotNull(result.PostedBody);
        Assert.Contains("EDITED", result.PostedBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_html_page_offers_visual_and_saves_empty()
    {
        PageState? before = null;
        PageState? after = null;
        var result = await SubmitAsync(string.Empty, "html", async page =>
        {
            // An empty page opens in the source tab with no warning.
            before = await ReadStateAsync(page);
            await page.ClickAsync("[data-editor-mode=\"visual\"]");
            after = await ReadStateAsync(page);
        });

        Assert.Empty(result.PageErrors);

        Assert.Equal("source", before!.ActiveTab);
        Assert.False(before.WarningVisible);

        // The visual tab is offered for an empty page and shows no warning.
        Assert.Equal("visual", after!.ActiveTab);
        Assert.False(after.WarningVisible);

        // Saving without editing still posts an empty body, not <p><br></p>.
        Assert.NotNull(result.PostedBody);
        Assert.Equal(string.Empty, NormalizeLineEndings(result.PostedBody!));
    }

    [Fact]
    public async Task Missing_editor_asset_hides_visual_tab_without_warning_and_saves_body()
    {
        PageState? state = null;
        var result = await SubmitAsync(
            FixtureBody,
            "html",
            async page =>
            {
                state = await ReadStateAsync(page);
            },
            includeEditorAsset: false);

        Assert.Empty(result.PageErrors);

        // With no editor bundle the visual tab is not offered and no warning
        // is shown for a page that was never compared.
        Assert.Equal("source", state!.ActiveTab);
        Assert.True(state.VisualTabHidden);
        Assert.False(state.WarningVisible);

        // The textarea still saves the body unchanged.
        Assert.NotNull(result.PostedBody);
        Assert.Equal(FixtureBody, NormalizeLineEndings(result.PostedBody!));
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
        var roundTripped = await RoundTripDirectlyAsync(FixtureBody, "html");

        Assert.NotEqual(FixtureBody, roundTripped);
        Assert.DoesNotContain("<style", roundTripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("content-link", roundTripped, StringComparison.Ordinal);
        Assert.Contains("&amp;amp;", roundTripped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Vendored_editor_round_trip_changes_the_markdown_fixture()
    {
        // Characterization test, not an acceptance test: it documents that the
        // visual editor does not hand Markdown back unchanged. That is why the
        // editor value is only written back when the user really changed it.
        var roundTripped = await RoundTripDirectlyAsync(MarkdownBody, "markdown");

        Assert.NotEqual(MarkdownBody, roundTripped);
    }

    private sealed record SubmitResult(string? PostedBody, string LinksJson, IReadOnlyList<string> PageErrors);

    private sealed class PageState
    {
        public string ActiveTab { get; set; } = string.Empty;

        public bool WarningVisible { get; set; }

        public bool VisualTabHidden { get; set; }

        public string TextareaValue { get; set; } = string.Empty;
    }

    // A browser submits a textarea value with CRLF line breaks while the
    // fixtures use LF. That difference is a transport detail, not a content
    // change, so it is normalised before the bodies are compared.
    private static string NormalizeLineEndings(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static async Task<SubmitResult> SubmitAsync(
        string source,
        string contentType = "html",
        Func<IPage, Task>? interact = null,
        bool includeEditorAsset = true)
    {
        var (editCshtml, editorJs) = ReadRepositoryInputs();
        var pageScript = ExtractInlineEditorScript(editCshtml);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        var pageErrors = new List<string>();
        page.PageError += (_, message) => pageErrors.Add(message);

        var harnessHtml = BuildHarnessHtml(includeEditorAsset ? editorJs : null, pageScript, source, contentType);
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

        // The page is submitted untouched unless the scenario asks for an
        // interaction first. The submit is done with the real submit control so
        // the form's submit handler (which pulls the visual editor value back
        // into the textarea) runs exactly as in production.
        if (interact is not null)
        {
            await interact(page);
        }

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

    private static async Task<PageState> ReadStateAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(StateProbeScript);
        return JsonSerializer.Deserialize<PageState>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static async Task<string> RoundTripDirectlyAsync(string source, string mode)
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
            """(args) => { const editor = new toastui.Editor({ el: document.getElementById('editor-host'), height: '300px', initialEditType: 'wysiwyg', previewStyle: 'tab', hideModeSwitch: true, initialValue: '' }); if (args.mode === 'markdown') { editor.setMarkdown(args.source || ''); return editor.getMarkdown(); } editor.setHTML(args.source || ''); return editor.getHTML(); }""",
            new { source, mode });

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

    private static string BuildHarnessHtml(string? editorJs, string pageScript, string source, string contentType)
    {
        // The textarea is HTML-escaped here exactly as Razor would render the
        // persisted body, so the script under test receives the true source.
        var escapedSource = source
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

        var markdownSelected = contentType == "markdown" ? string.Empty : " selected";
        var htmlSelected = contentType == "html" ? " selected" : string.Empty;

        return "<!doctype html><html><head><meta charset=\"utf-8\"></head><body>" +
            "<form method=\"post\" id=\"content-edit-form\" action=\"https://roundtrip.invalid/content-edit\">" +
            "<select id=\"Input_ContentType\" name=\"Input.ContentType\">" +
            "<option value=\"markdown\"" + markdownSelected + ">Markdown</option>" +
            "<option value=\"html\"" + htmlSelected + ">HTML</option>" +
            "<option value=\"html_file\">HTML file</option>" +
            "<option value=\"server_report\">Server report</option>" +
            "</select>" +
            "<div data-content-editor-field>" +
            "<div data-editor-tabs hidden>" +
            "<button type=\"button\" data-editor-mode=\"visual\" aria-selected=\"true\">Visual</button>" +
            "<button type=\"button\" data-editor-mode=\"source\" data-html-source-label=\"HTML source\" data-markdown-source-label=\"Markdown source\" aria-selected=\"false\">Markdown source</button>" +
            "</div>" +
            "<div id=\"content-editor\" hidden></div>" +
            "<div class=\"omp-feedback__alert status-banner\" role=\"alert\" data-editor-warning hidden>The visual editor cannot be used for this page because it would change the HTML. Edit the HTML source instead.</div>" +
            "<textarea id=\"Input_Body\" name=\"Input.Body\" rows=\"18\">" + escapedSource + "</textarea>" +
            "<span data-editor-hint=\"markdown\">Write Markdown content.</span>" +
            "<span data-editor-hint=\"html\" hidden>Write trusted HTML directly.</span>" +
            "</div>" +
            "<div data-html-file-field hidden></div>" +
            "<div data-server-report-field hidden></div>" +
            "<button type=\"submit\">Save page</button>" +
            "</form>" +
            (editorJs is null ? string.Empty : "<script>" + EscapeForInlineScript(editorJs) + "</script>") +
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

    // Reads the editor state that the page under test exposes: which tab is
    // selected, whether the warning is visible and what the textarea holds.
    private const string StateProbeScript =
        """() => { const active = document.querySelector('[data-editor-mode][aria-selected="true"]'); const warning = document.querySelector('[data-editor-warning]'); const visual = document.querySelector('[data-editor-mode="visual"]'); const textarea = document.getElementById('Input_Body'); return JSON.stringify({ activeTab: active ? active.getAttribute('data-editor-mode') : '', warningVisible: !!warning && !warning.hidden && warning.getClientRects().length > 0, visualTabHidden: !!visual && !!visual.hidden, textareaValue: textarea ? textarea.value : '' }); }""";
}
