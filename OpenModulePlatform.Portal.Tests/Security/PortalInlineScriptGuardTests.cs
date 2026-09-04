// File: OpenModulePlatform.Portal.Tests/Security/PortalInlineScriptGuardTests.cs
using System.Text.RegularExpressions;

namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// Pins the CSP migration (campaign csp-vagen-till-enforcement): the Portal's
/// script-src no longer carries 'unsafe-inline', so no Portal page may render an
/// executable inline &lt;script&gt; block. A new inline block must either move to a
/// static file under wwwroot/js or carry an explicit non-JavaScript type (data
/// blocks such as application/json are never executed and are allowed).
/// </summary>
public sealed class PortalInlineScriptGuardTests
{
    [Fact]
    public void PortalPages_HaveNoExecutableInlineScriptBlocks()
    {
        var pagesDirectory = GetRepositoryPath("OpenModulePlatform.Portal", "Pages");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(pagesDirectory, "*.cshtml", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(
                         content,
                         "<script\\b([^>]*)>",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var attributes = match.Groups[1].Value;
                var hasSource = Regex.IsMatch(attributes, "\\bsrc\\s*=", RegexOptions.IgnoreCase);
                var isDataBlock = Regex.IsMatch(
                    attributes,
                    "type\\s*=\\s*[\"'](?!text/javascript|module|application/javascript)[^\"']+[\"']",
                    RegexOptions.IgnoreCase);
                if (!hasSource && !isDataBlock)
                {
                    offenders.Add($"{Path.GetFileName(file)}: {match.Value.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Executable inline <script> blocks found (move them to wwwroot/js):\n - "
            + string.Join("\n - ", offenders));
    }

    [Fact]
    public void PortalConfiguredPolicy_DropsUnsafeInlineFromScriptSrc()
    {
        var appsettings = File.ReadAllText(
            GetRepositoryPath("OpenModulePlatform.Portal", "appsettings.json"));

        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", appsettings, StringComparison.Ordinal);
    }

    private static string GetRepositoryPath(params string[] relativePathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "OpenModulePlatform.slnx")))
            {
                var segments = new string[relativePathSegments.Length + 1];
                segments[0] = directory.FullName;
                Array.Copy(relativePathSegments, 0, segments, 1, relativePathSegments.Length);
                return Path.Join(segments);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate OpenModulePlatform repository root.");
    }
}
