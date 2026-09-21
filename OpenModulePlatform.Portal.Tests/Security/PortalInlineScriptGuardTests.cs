// File: OpenModulePlatform.Portal.Tests/Security/PortalInlineScriptGuardTests.cs
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenModulePlatform.TestSupport;

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
        var pagesDirectory = OmpRepositoryFiles.GetRepositoryPath("OpenModulePlatform.Portal", "Pages");
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

    // The checked-in appsettings.json never reaches a host: the artifact payload strips
    // it and HostAgent writes Packaging/appsettings.json (merged over its built-in
    // template) instead. Both files are therefore checked, and the packaged policy is
    // pinned to the checked-in one so the strict policy cannot be lost on the way out.
    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("Packaging/appsettings.json")]
    public void PortalConfiguredPolicy_DropsUnsafeInlineFromScriptSrc(string relativePath)
    {
        var appsettings = File.ReadAllText(
            OmpRepositoryFiles.GetRepositoryPath("OpenModulePlatform.Portal", relativePath));

        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", appsettings, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedPortalConfiguration_CarriesTheCheckedInPolicyAndNLogSection()
    {
        using var checkedIn = JsonDocument.Parse(
            File.ReadAllText(OmpRepositoryFiles.GetRepositoryPath("OpenModulePlatform.Portal", "appsettings.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        using var packaged = JsonDocument.Parse(
            File.ReadAllText(OmpRepositoryFiles.GetRepositoryPath("OpenModulePlatform.Portal", "Packaging", "appsettings.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

        var checkedInPolicy = checkedIn.RootElement
            .GetProperty("Portal").GetProperty("SecurityHeaders").GetProperty("ContentSecurityPolicy").GetProperty("Policy")
            .GetString();
        var packagedPolicy = packaged.RootElement
            .GetProperty("Portal").GetProperty("SecurityHeaders").GetProperty("ContentSecurityPolicy").GetProperty("Policy")
            .GetString();

        Assert.False(string.IsNullOrWhiteSpace(checkedInPolicy));
        Assert.Equal(checkedInPolicy, packagedPolicy);
        Assert.True(packaged.RootElement.TryGetProperty("NLog", out var nlog));
        Assert.True(nlog.TryGetProperty("targets", out _));
    }
}
