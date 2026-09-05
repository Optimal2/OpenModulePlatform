// File: OpenModulePlatform.Portal.Tests/Security/IFrameModuleInlineScriptGuardTests.cs
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenModulePlatform.Web.iFrameWebAppModule.Security;

namespace OpenModulePlatform.Portal.Tests.Security;

/// <summary>
/// The iFrame module's deterministic gate for the CSP hardening (campaign
/// csp-sista-undantagen; gate added in iframe-csp-grinden-ar-inte-deterministisk),
/// mirroring <see cref="PortalInlineScriptGuardTests"/>. Until now the module's
/// "script-src 'self'" was pinned only by the skippable IFrameCspSmokeTests UI suite,
/// so reintroducing 'unsafe-inline' — or deleting the Policy key, which used to fall
/// back silently to the shared baseline WITH 'unsafe-inline' — produced no certain
/// red. These ordinary unit tests fail the build in both cases.
/// </summary>
public sealed class IFrameModuleInlineScriptGuardTests
{
    [Fact]
    public void ModulePages_HaveNoExecutableInlineScriptBlocks()
    {
        var pagesDirectory = GetRepositoryPath("OpenModulePlatform.Web.iFrameWebAppModule", "Pages");
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
            "Executable inline <script> blocks found in the iFrame module (move them to wwwroot/js):\n - "
            + string.Join("\n - ", offenders));
    }

    // Repository appsettings files carry a leading "// File:" comment line.
    private static readonly JsonDocumentOptions AppsettingsJsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [Fact]
    public void ModuleConfiguredPolicy_PresentAndDropsUnsafeInlineFromScriptSrc()
    {
        var appsettingsPath = GetRepositoryPath("OpenModulePlatform.Web.iFrameWebAppModule", "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(appsettingsPath), AppsettingsJsonOptions);

        // The Policy key must exist: before campaign
        // iframe-csp-grinden-ar-inte-deterministisk, losing it silently restored the
        // shared baseline's script-src 'unsafe-inline'. The middleware now falls back
        // to the module's own tightened policy, but the shipped configuration is still
        // the source of truth and must not go missing.
        var policy = TryGetConfiguredPolicy(document.RootElement);
        Assert.False(
            string.IsNullOrWhiteSpace(policy),
            "Portal:SecurityHeaders:ContentSecurityPolicy:Policy is missing or empty in the iFrame module's appsettings.json.");

        AssertScriptSrcHasNoUnsafeInline(policy!);
    }

    private static string? TryGetConfiguredPolicy(JsonElement root)
    {
        if (root.TryGetProperty("Portal", out var portal)
            && portal.TryGetProperty("SecurityHeaders", out var securityHeaders)
            && securityHeaders.TryGetProperty("ContentSecurityPolicy", out var csp)
            && csp.TryGetProperty("Policy", out var policyElement)
            && policyElement.ValueKind == JsonValueKind.String)
        {
            return policyElement.GetString();
        }

        return null;
    }

    [Fact]
    public void ModuleConfiguredPolicy_MatchesBuiltInFallbackBaseline()
    {
        // The middleware falls back to IFrameFrameSourcePolicy.ModulePolicyBaseline
        // when the configured Policy key is absent; the two must not drift apart, or
        // a lost key silently changes the policy again.
        using var document = JsonDocument.Parse(File.ReadAllText(
            GetRepositoryPath("OpenModulePlatform.Web.iFrameWebAppModule", "appsettings.json")),
            AppsettingsJsonOptions);
        var configured = document.RootElement
            .GetProperty("Portal")
            .GetProperty("SecurityHeaders")
            .GetProperty("ContentSecurityPolicy")
            .GetProperty("Policy")
            .GetString();

        Assert.Equal(IFrameFrameSourcePolicy.ModulePolicyBaseline, configured);
    }

    [Fact]
    public void ModuleFallbackBaseline_DropsUnsafeInlineFromScriptSrc()
    {
        AssertScriptSrcHasNoUnsafeInline(IFrameFrameSourcePolicy.ModulePolicyBaseline);
        Assert.Contains("frame-src 'self'", IFrameFrameSourcePolicy.ModulePolicyBaseline, StringComparison.Ordinal);
    }

    private static void AssertScriptSrcHasNoUnsafeInline(string policy)
    {
        var scriptSrc = policy
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(directive => directive.StartsWith("script-src", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(scriptSrc), $"Policy carries no script-src directive: {policy}");
        Assert.DoesNotContain("unsafe-inline", scriptSrc, StringComparison.OrdinalIgnoreCase);
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
