using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenModulePlatform.Bootstrapper;

/// <summary>Keeps selective installer builds consistent with build-repository-objects.ps1.</summary>
internal static partial class OpenDocViewerArtifactIntegrity
{
    public static void Apply(string payloadRoot)
    {
        var indexPath = Path.Join(payloadRoot, "index.html");
        var configPath = Path.Join(payloadRoot, "odv.config.js");
        if (!File.Exists(indexPath) || !File.Exists(configPath))
        {
            return;
        }

        var html = File.ReadAllText(indexPath);
        var match = BootstrapScript().Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException("OpenDocViewer payload index.html is missing the data-odv-bootstrap script tag.");
        }

        var integrity = "sha384-" + Convert.ToBase64String(SHA384.HashData(File.ReadAllBytes(configPath)));
        var attribute = "data-odv-config-integrity=\"" + integrity + "\"";
        var tag = ConfigIntegrity().IsMatch(match.Value)
            ? ConfigIntegrity().Replace(match.Value, attribute)
            : match.Value[..^1] + " " + attribute + ">";
        var updated = html[..match.Index] + tag + html[(match.Index + match.Length)..];
        if (!string.Equals(html, updated, StringComparison.Ordinal))
        {
            File.WriteAllText(indexPath, updated, new UTF8Encoding(false));
        }
    }

    [GeneratedRegex("<script\\b[^>]*\\bdata-odv-bootstrap(?=[\\s=>])[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BootstrapScript();

    [GeneratedRegex("\\bdata-odv-config-integrity\\s*=\\s*(?:\"[^\"]*\"|'[^']*')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConfigIntegrity();
}
