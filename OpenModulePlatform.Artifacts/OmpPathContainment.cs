// File: OpenModulePlatform.Artifacts/OmpPathContainment.cs
namespace OpenModulePlatform.Artifacts;

/// <summary>
/// Decides whether one path is the same as, or inside, another.
/// </summary>
/// <remarks>
/// R8-P2-16..23. This existed as three private copies -- ArtifactProvisioner,
/// HostAgentJobProcessor and the Bootstrapper -- and the sweep flagged that not all of them
/// normalized their inputs first. That matters: the comparison is a string prefix test, so
/// "C:\OMP\Artifacts" against "C:\OMP\Artifacts\..\..\Windows" returns true unless both sides go
/// through Path.GetFullPath. R3-A1 closed exactly that hole in one copy and the others kept the
/// unnormalized version.
///
/// Normalizing inside the helper rather than trusting each caller is the point. A containment
/// check that is only correct when the caller remembers to prepare its arguments is a check that
/// will eventually be called wrong -- which is the whole finding.
/// </remarks>
public static class OmpPathContainment
{
    /// <summary>
    /// True when <paramref name="candidatePath"/> is <paramref name="rootPath"/> or lies beneath
    /// it. Both sides are fully resolved first, so relative segments cannot escape the root.
    /// </summary>
    public static bool IsSameOrChildPath(string? rootPath, string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        string root;
        string candidate;
        try
        {
            root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unresolvable path is not contained by anything.
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(root, candidate, comparison))
        {
            return true;
        }

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}
