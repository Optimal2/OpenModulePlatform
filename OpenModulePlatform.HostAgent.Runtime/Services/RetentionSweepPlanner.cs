namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>One entry of a swept root (a file or a directory), as the planner sees it.</summary>
internal readonly record struct RetentionSweepEntry(string Path, long LengthBytes, DateTime LastWriteTimeUtc);

/// <summary>What a sweep of one root would remove, and the size either side of it.</summary>
internal sealed record RetentionSweepPlan(
    IReadOnlyList<RetentionSweepEntry> AgedOut,
    IReadOnlyList<RetentionSweepEntry> OverSizeCap,
    long TotalBytesBefore,
    long TotalBytesAfter);

/// <summary>
/// The retention policy shared by every root the HostAgent prunes by age and by size: the
/// processed and failed import archives, and the maintenance quarantine.
/// </summary>
/// <remarks>
/// Lifted out of <see cref="ArtifactZipImportService"/> when the maintenance quarantine
/// needed the same rule, so there is one policy to test and one to fix (metod 4.1). Pure by
/// design so the policy can be tested without a filesystem (R12-F13).
///
/// What the sweep must LET THROUGH is as much a part of the rule as what it removes
/// (metod 4.5): the newest entry in the root is never removed, by age or by size. A cap
/// smaller than one entry would otherwise empty the root on the first cycle, and the
/// newest entry is precisely the one an operator opens after a failed refresh or a
/// cleanup that went wrong. Its sidecar is kept with it, because a retained entry whose
/// reason was deleted is worse than either alone.
/// </remarks>
internal static class RetentionSweepPlanner
{
    /// <summary>
    /// Decides which entries a sweep removes: everything past the age cutoff, then
    /// oldest-first until the root fits under <paramref name="maxTotalBytes"/>.
    /// </summary>
    /// <param name="entries">The root's entries.</param>
    /// <param name="ageCutoffUtc">Entries last written before this are aged out; <see langword="null"/> keeps by age forever.</param>
    /// <param name="maxTotalBytes">Upper bound on the root's size; 0 disables the size cap.</param>
    /// <param name="isSidecar">
    /// True for an entry that only explains another (an error or reason file written after
    /// the entry it belongs to). A sidecar is never the protected newest entry, and the
    /// sidecars of the protected entry are protected with it.
    /// </param>
    internal static RetentionSweepPlan Plan(
        IReadOnlyList<RetentionSweepEntry> entries,
        DateTime? ageCutoffUtc,
        long maxTotalBytes,
        Func<string, bool> isSidecar)
    {
        ArgumentNullException.ThrowIfNull(isSidecar);

        var totalBytesBefore = entries.Sum(static entry => entry.LengthBytes);
        if (entries.Count == 0)
        {
            return new RetentionSweepPlan([], [], 0, 0);
        }

        // Oldest first, with a path tiebreak so two entries sharing a timestamp still
        // produce a deterministic plan (metod 4.11: never order on a timestamp alone).
        var ordered = entries
            .OrderBy(static entry => entry.LastWriteTimeUtc)
            .ThenBy(static entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var protectedPaths = BuildProtectedPaths(ordered, isSidecar);
        var agedOut = new List<RetentionSweepEntry>();
        var overSizeCap = new List<RetentionSweepEntry>();
        var doomedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remainingBytes = totalBytesBefore;

        if (ageCutoffUtc is { } cutoff)
        {
            foreach (var entry in ordered)
            {
                if (entry.LastWriteTimeUtc >= cutoff || protectedPaths.Contains(entry.Path))
                {
                    continue;
                }

                agedOut.Add(entry);
                doomedPaths.Add(entry.Path);
                remainingBytes -= entry.LengthBytes;
            }
        }

        if (maxTotalBytes > 0)
        {
            foreach (var entry in ordered)
            {
                if (remainingBytes <= maxTotalBytes)
                {
                    break;
                }

                if (doomedPaths.Contains(entry.Path) || protectedPaths.Contains(entry.Path))
                {
                    continue;
                }

                overSizeCap.Add(entry);
                doomedPaths.Add(entry.Path);
                remainingBytes -= entry.LengthBytes;
            }
        }

        return new RetentionSweepPlan(agedOut, overSizeCap, totalBytesBefore, remainingBytes);
    }

    private static HashSet<string> BuildProtectedPaths(
        IReadOnlyList<RetentionSweepEntry> orderedOldestFirst,
        Func<string, bool> isSidecar)
    {
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A sidecar is written AFTER the entry it explains, so it is always the newer of
        // the pair. Picking the newest entry blindly would therefore protect a 200-byte
        // reason file and remove the entry it explains.
        var newest = orderedOldestFirst
            .LastOrDefault(entry => !isSidecar(entry.Path));
        if (newest.Path is null)
        {
            newest = orderedOldestFirst[^1];
        }

        protectedPaths.Add(newest.Path);
        foreach (var entry in orderedOldestFirst.Where(entry =>
                     isSidecar(entry.Path)
                     && entry.Path.StartsWith(newest.Path, StringComparison.OrdinalIgnoreCase)))
        {
            protectedPaths.Add(entry.Path);
        }

        return protectedPaths;
    }
}
