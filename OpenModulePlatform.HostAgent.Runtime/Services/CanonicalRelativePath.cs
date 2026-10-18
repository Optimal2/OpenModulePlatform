namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// The one canonical identity for a deployment-relative path. Reserved-path checks, payload
/// collision checks, inventory matching, configuration grouping and actual writer targets all
/// compare this form, so dot segments, duplicate separators and slash variants can never make
/// the same file look like a different path to a check than to the writer (which resolves
/// through <see cref="DeploymentPath.CombineUnderRoot"/> and <c>Path.GetFullPath</c>).
/// The canonical form uses '/' separators; comparisons against it are OrdinalIgnoreCase
/// because the deployment file system is case-insensitive.
/// </summary>
internal static class CanonicalRelativePath
{
    /// <summary>
    /// Canonicalizes a relative path, or returns null when the path is empty, rooted,
    /// drive-qualified, contains a parent-directory segment, a colon, or a segment Windows
    /// would silently rewrite (trailing dot or space). Such paths are unsafe or ambiguous
    /// and must be rejected before any write, never guessed at.
    /// </summary>
    internal static string? TryCanonicalize(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var unified = relativePath.Trim().Replace('\\', '/');
        // Rooted ('/x', '//server/share') and drive-qualified ('C:/x', 'C:x') paths are not
        // relative identities at all.
        if (unified.StartsWith('/') || (unified.Length >= 2 && unified[1] == ':') || unified.Contains(':'))
        {
            return null;
        }

        var segments = new List<string>();
        foreach (var segment in unified.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == ".." || segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return null;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    internal static bool AreEqual(string? first, string? second)
    {
        var canonicalFirst = TryCanonicalize(first);
        var canonicalSecond = TryCanonicalize(second);
        return canonicalFirst is not null
            && canonicalSecond is not null
            && string.Equals(canonicalFirst, canonicalSecond, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Canonical form rendered with OS separators, for storing or joining on disk.</summary>
    internal static string ToOsPath(string canonicalPath)
        => canonicalPath.Replace('/', Path.DirectorySeparatorChar);
}
