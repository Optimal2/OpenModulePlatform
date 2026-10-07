using System.Text.Json;

namespace OpenModulePlatform.Installer.Core;

/// <summary>
/// Works out which ASP.NET Core runtime major the selected artifacts need, and
/// which hosting-bundle installer in the package's prereqs\ folder satisfies it.
/// </summary>
public static class RuntimeRequirementResolver
{
    public const int FallbackRuntimeMajor = 10;

    /// <summary>
    /// The required runtime major: the highest major named by any
    /// <c>*.runtimeconfig.json</c> in the artifact payload, or
    /// <see cref="FallbackRuntimeMajor"/> (this repository's target major) when
    /// the payload carries none.
    /// </summary>
    public static int ResolveRequiredMajor(IEnumerable<string> runtimeConfigJsonTexts)
    {
        var highest = 0;
        foreach (var text in runtimeConfigJsonTexts)
        {
            foreach (var version in ReadFrameworkVersions(text))
            {
                var dotIndex = version.IndexOf('.', StringComparison.Ordinal);
                if (dotIndex <= 0 || !int.TryParse(version[..dotIndex], out var major))
                {
                    continue;
                }

                highest = Math.Max(highest, major);
            }
        }

        return highest > 0 ? highest : FallbackRuntimeMajor;
    }

    /// <summary>
    /// Reads Microsoft.NETCore.App / Microsoft.AspNetCore.App framework versions
    /// from one runtimeconfig.json document. Malformed documents yield nothing.
    /// </summary>
    internal static IReadOnlyList<string> ReadFrameworkVersions(string runtimeConfigJsonText)
    {
        try
        {
            using var document = JsonDocument.Parse(runtimeConfigJsonText);
            if (!document.RootElement.TryGetProperty("runtimeOptions", out var options))
            {
                return [];
            }

            var versions = new List<string>();
            if (options.TryGetProperty("framework", out var single))
            {
                AddFrameworkVersion(single, versions);
            }

            if (options.TryGetProperty("frameworks", out var multiple) && multiple.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in multiple.EnumerateArray())
                {
                    AddFrameworkVersion(entry, versions);
                }
            }

            return versions;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void AddFrameworkVersion(JsonElement framework, List<string> versions)
    {
        if (framework.ValueKind != JsonValueKind.Object
            || !framework.TryGetProperty("name", out var name)
            || !framework.TryGetProperty("version", out var version))
        {
            return;
        }

        var frameworkName = name.GetString() ?? string.Empty;
        if ((frameworkName.Equals("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase)
                || frameworkName.Equals("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase))
            && version.GetString() is { Length: > 0 } versionText)
        {
            versions.Add(versionText);
        }
    }

    /// <summary>
    /// Picks the hosting-bundle installer for the required major from the
    /// installers found in prereqs\ (file names such as
    /// <c>dotnet-hosting-10.0.3-win.exe</c>). A bundle whose name carries no
    /// version is accepted only when it is the single candidate. Returns null
    /// when nothing matches.
    /// </summary>
    public static string? PickHostingBundleInstaller(IReadOnlyList<string> installerPaths, int requiredMajor)
    {
        if (installerPaths.Count == 0)
        {
            return null;
        }

        var withMatchingMajor = installerPaths
            .Where(path => HostingBundleMajor(Path.GetFileName(path)) == requiredMajor)
            .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (withMatchingMajor.Length > 0)
        {
            return withMatchingMajor[0];
        }

        var unversioned = installerPaths
            .Where(path => HostingBundleMajor(Path.GetFileName(path)) is null)
            .ToArray();
        return unversioned.Length == 1 && installerPaths.Count == 1
            ? unversioned[0]
            : null;
    }

    /// <summary>The major version a dotnet-hosting bundle file name advertises, or null.</summary>
    internal static int? HostingBundleMajor(string fileName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            fileName,
            @"dotnet-hosting-(?<major>\d+)\.",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["major"].Value, out var major)
            ? major
            : null;
    }
}
