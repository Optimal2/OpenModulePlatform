// File: OpenModulePlatform.Bootstrapper/ArtifactSourceStamp.cs
using System.Text.RegularExpressions;

namespace OpenModulePlatform.Bootstrapper;

/// <summary>
/// Resolves which source files feed a single artifact component: its project file, the transitive
/// ProjectReference closure (including cross-repository references reached through an MSBuild
/// property such as $(OpenModulePlatformRoot)), and the MSBuild directory files above them.
/// </summary>
/// <remarks>
/// R8-P6-3. The artifact build stamp used to be repository-wide -- git HEAD plus every dirty file
/// -- so one commit or one edited file invalidated the stamp of every component in the repository.
/// All of them were rebuilt at their unchanged versions and the host rejected each rebuilt package
/// with "already exists ... use a new version number for changed artifact content". The only
/// strategy that converged was bumping a repository's components all at once, at a cost of five
/// deploy rounds per batch.
///
/// This lives outside InstallerForm so it can be tested directly: the original defect was a
/// Path.Join against an already-absolute expanded property, which silently produced a nonexistent
/// path and made every component fall back to the repository-wide stamp -- exactly the failure the
/// fix was meant to remove, and invisible without a test.
/// </remarks>
internal static class ArtifactSourceStamp
{
    private static readonly Dictionary<string, Dictionary<string, string>> _msBuildPropertyMapCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Why the most recent scoping attempt declined; surfaced when OMP_STAMP_DIAG=1.</summary>
    internal static string? DeclineReason { get; private set; }

    internal static void ResetDeclineReason() => DeclineReason = null;

    /// <summary>Records why per-component scoping declined, surfaced when OMP_STAMP_DIAG=1.</summary>
    private static bool DeclineScopedStamp(string reason)
    {
        DeclineReason = reason;
        return false;
    }

    /// <summary>
    /// Resolves the project file for a component whose <c>projectPath</c> is either a
    /// directory (the common case) or the project file itself.
    /// </summary>
    internal static string? TryResolveComponentProjectFile(string sourceRoot, string projectPath)
    {
        try
        {
            var candidate = Path.GetFullPath(Path.Join(sourceRoot, projectPath));
            if (!Program.IsSameOrChildPath(sourceRoot, candidate))
            {
                return null;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (!Directory.Exists(candidate))
            {
                return null;
            }

            // A single project per directory is the convention across every repository here.
            // More than one is ambiguous, so decline rather than guess.
            var projects = Directory.GetFiles(candidate, "*.??proj", SearchOption.TopDirectoryOnly);
            return projects.Length == 1 ? projects[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds <paramref name="projectFile"/> and every project it references transitively to
    /// <paramref name="projectFiles"/>. Returns false when a reference points outside the
    /// source root or the graph is deeper than expected, so the caller can fall back.
    /// </summary>
    internal static bool TryCollectProjectClosure(
        string projectFile,
        string sourceRoot,
        SortedSet<string> projectFiles,
        int depth)
    {
        if (depth > 32)
        {
            return false;
        }

        if (!File.Exists(projectFile) || !projectFiles.Add(projectFile))
        {
            return depth > 0 || File.Exists(projectFile);
        }

        var projectDirectory = Path.GetDirectoryName(projectFile);
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return false;
        }

        string projectText;
        try
        {
            projectText = File.ReadAllText(projectFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        foreach (Match match in Regex.Matches(
            projectText,
            @"<ProjectReference\s+[^>]*Include\s*=\s*""([^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5)))
        {
            var include = match.Groups[1].Value.Trim();
            if (include.Length == 0)
            {
                return DeclineScopedStamp($"empty ProjectReference in {projectFile}");
            }

            if (include.Contains('$'))
            {
                // Cross-repository references go through a property such as
                // $(OpenModulePlatformRoot). Expand what we can; anything left unresolved
                // means we decline rather than under-scope, because a missed reference would
                // mean a missed rebuild and a stale artifact on the host.
                var expanded = TryExpandMsBuildPropertyReference(include, projectFile);
                if (expanded is null)
                {
                    return DeclineScopedStamp($"unresolved property in '{include}' ({projectFile})");
                }

                include = expanded;
            }

            string referencePath;
            try
            {
                // An expanded property yields an absolute path (the cross-repository case),
                // and Path.Join would concatenate rather than replace it.
                var includePath = include.Replace('\\', Path.DirectorySeparatorChar);
                referencePath = Path.GetFullPath(Path.IsPathRooted(includePath)
                    ? includePath
                    : Path.Join(projectDirectory, includePath));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }

            // A resolved path that does not exist means the expansion was wrong, so decline.
            // Cross-repository targets are in scope: OpenModulePlatform.Web.Shared genuinely
            // feeds every consuming module's artifact.
            if (!File.Exists(referencePath))
            {
                return DeclineScopedStamp($"reference not found: {referencePath} ({projectFile})");
            }

            if (!TryCollectProjectClosure(referencePath, sourceRoot, projectFiles, depth + 1))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Expands the MSBuild property references in a ProjectReference include, using the
    /// properties defined by the project file and its ancestor directory build files.
    /// Returns <c>null</c> when anything remains unresolved.
    /// </summary>
    /// <remarks>
    /// This is not a general MSBuild evaluator and does not try to be. It covers the one
    /// pattern these repositories use -- a repository-root property such as
    /// $(OpenModulePlatformRoot) defined in Directory.Build.targets from
    /// $(MSBuildThisFileDirectory) and normalised through
    /// $([System.IO.Path]::GetFullPath(...)) -- and declines everything else. The caller also
    /// requires the expanded path to exist, so a wrong expansion degrades to the repository-
    /// wide stamp rather than to a missed rebuild.
    /// </remarks>
    internal static string? TryExpandMsBuildPropertyReference(string include, string projectFile)
    {
        var properties = GetMsBuildPropertyMap(projectFile);
        var expanded = ExpandMsBuildProperties(include, properties, depth: 0);
        return expanded is null || expanded.Contains('$') ? null : expanded;
    }

    private static Dictionary<string, string> GetMsBuildPropertyMap(string projectFile)
    {
        if (_msBuildPropertyMapCache.TryGetValue(projectFile, out var cached))
        {
            return cached;
        }

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Root-most first so a nearer file can override, matching MSBuild import order.
        var files = new List<string>();
        var current = Path.GetDirectoryName(projectFile);
        for (var level = 0; level < 16 && !string.IsNullOrWhiteSpace(current); level++)
        {
            foreach (var name in (string[])["Directory.Build.props", "Directory.Build.targets"])
            {
                var path = Path.Join(current, name);
                if (File.Exists(path))
                {
                    files.Add(Path.GetFullPath(path));
                }
            }

            if (Directory.Exists(Path.Join(current, ".git")) || File.Exists(Path.Join(current, ".git")))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }

        files.Reverse();
        files.Add(projectFile);

        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var definingDirectory = Path.GetDirectoryName(file) ?? string.Empty;
            foreach (Match match in Regex.Matches(
                text,
                @"<([A-Za-z_][A-Za-z0-9_]*)(?:\s+[^>]*)?>([^<>]*)</\1>",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(5)))
            {
                var name = match.Groups[1].Value;
                var rawValue = match.Groups[2].Value.Trim();
                if (rawValue.Length == 0)
                {
                    continue;
                }

                // MSBuild's own well-known property, relative to the file that uses it.
                var value = rawValue.Replace(
                    "$(MSBuildThisFileDirectory)",
                    definingDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);

                var resolved = ExpandMsBuildProperties(value, properties, depth: 0);
                if (resolved is null || resolved.Contains('$'))
                {
                    continue;
                }

                properties[name] = resolved;
            }
        }

        _msBuildPropertyMapCache[projectFile] = properties;
        return properties;
    }

    private static string? ExpandMsBuildProperties(
        string value,
        Dictionary<string, string> properties,
        int depth)
    {
        if (depth > 8)
        {
            return null;
        }

        // $([System.IO.Path]::GetFullPath('X')) -- the only static method used here.
        var fullPathMatch = Regex.Match(
            value,
            @"^\$\(\[System\.IO\.Path\]::GetFullPath\('(.*)'\)\)$",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));
        if (fullPathMatch.Success)
        {
            var inner = ExpandMsBuildProperties(fullPathMatch.Groups[1].Value, properties, depth + 1);
            if (inner is null || inner.Contains('$'))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(inner);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        return Regex.Replace(
            value,
            @"\$\(([A-Za-z_][A-Za-z0-9_]*)\)",
            match => properties.TryGetValue(match.Groups[1].Value, out var replacement)
                ? replacement
                : match.Value,
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));
    }
}
