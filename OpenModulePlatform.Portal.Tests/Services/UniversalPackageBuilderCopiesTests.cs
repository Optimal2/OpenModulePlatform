using OpenModulePlatform.TestSupport;

namespace OpenModulePlatform.Portal.Tests.Services;

/// <summary>
/// The universal package builder exists twice: the standalone page
/// tools/universal-package-builder/index.html (no server, English only) and the Portal's
/// wwwroot/js/admin-universal-package-builder.js (texts from the page's localized labels).
/// The two cannot share a file, so this test holds them equal: the standalone script, with
/// each English text replaced by the Portal's label lookup, must be the Portal script line
/// for line, apart from the few lines only the Portal copy needs. A logic change made in one
/// copy only fails here.
/// </summary>
public sealed class UniversalPackageBuilderCopiesTests
{
    // English text in the standalone copy -> the expression the Portal copy uses instead.
    private static readonly (string Standalone, string Portal)[] LocalizedTexts =
    [
        ("class=\"danger\">Remove</button>", "class=\"danger\"></button>"),
        ("\"No items added.\" : `${items.length} item(s) ready.`", "labels.noItems : format(labels.itemReady, items.length)"),
        ("`Skipped duplicate package path: ${path}`", "format(labels.duplicate, path)"),
        ("\"Package key and package version are required.\"", "labels.required"),
        ("\"Host configurations and config overlays are host-specific. Set the target host profile or remove those objects before creating a global package.\"", "labels.hostScopeRequired"),
        ("`Created ${fileName}.`", "format(labels.created, fileName)")
    ];

    // Lines only the Portal copy has: its file header, strict mode, and the label plumbing.
    private static readonly string[] PortalOnlyLines =
    [
        "// File: OpenModulePlatform.Portal/wwwroot/js/admin-universal-package-builder.js",
        "'use strict';",
        "const labels = JSON.parse(document.querySelector(\"[data-labels]\").dataset.labels || \"{}\");",
        "const format = (template, value) => String(template || \"\").replace(\"{0}\", value);",
        "row.querySelector(\"button\").textContent = labels.remove;"
    ];

    [Fact]
    public void PortalBuilderScript_IsTheStandaloneBuilderScriptWithLocalizedTexts()
    {
        var html = File.ReadAllText(OmpRepositoryFiles.GetRepositoryPath("tools/universal-package-builder", "index.html"));
        var portal = File.ReadAllText(OmpRepositoryFiles.GetRepositoryPath("OpenModulePlatform.Portal", "wwwroot/js/admin-universal-package-builder.js"));

        const string open = "<script>";
        var start = html.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, "The standalone builder has no inline <script> block.");
        start += open.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        Assert.True(end > start, "The standalone builder's <script> block is not closed.");
        var standalone = html[start..end];

        foreach (var (standaloneText, portalText) in LocalizedTexts)
        {
            Assert.True(
                standalone.Contains(standaloneText, StringComparison.Ordinal),
                $"The standalone builder no longer contains: {standaloneText}");
            standalone = standalone.Replace(standaloneText, portalText, StringComparison.Ordinal);
        }

        var portalLines = Lines(portal).ToList();
        foreach (var line in PortalOnlyLines)
        {
            Assert.True(portalLines.Remove(line), $"The Portal builder no longer contains: {line}");
        }

        Assert.Equal(Lines(standalone).ToList(), portalLines);
    }

    // Indentation and blank lines differ between the copies and carry no logic.
    private static IEnumerable<string> Lines(string source)
        => source
            .Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0);
}
