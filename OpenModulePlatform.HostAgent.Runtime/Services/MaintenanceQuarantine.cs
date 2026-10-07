using Microsoft.Extensions.Logging;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// The maintenance quarantine: where a maintenance cleanup moves a directory instead of
/// deleting it, and the sweep that removes quarantined directories once they have aged
/// out or the quarantine has grown past its cap.
/// </summary>
/// <remarks>
/// A cleanup that deletes is a cleanup that cannot be undone. The scan that proposes the
/// deletions judges ownership from a narrow model (service-app instances that are enabled,
/// allowed, desired and provisioned), so a directory it calls an orphan may still be
/// wanted: an instance that is disabled for the moment, a manually placed rig, a worker
/// deployed outside the model. Moving to a quarantine turns the worst case from "the
/// service is gone" into "the folder is under the quarantine root for 30 days".
///
/// The quarantine root lives on the same volume as the services root (a move across
/// volumes is a copy plus a delete, and a copy that fails halfway leaves two half
/// directories), so the retention is not optional: it is the same age-plus-size rule the
/// import archives use (<see cref="RetentionSweepPlanner"/>), and the newest quarantined
/// directory is never swept.
///
/// The root must be excluded from the orphan scan and refused by the directory cleanup in
/// the same HostAgent version that introduces it: a scanner that did not know the root
/// would flag the quarantine as an orphan directory, and the next cleanup would quarantine
/// the quarantine (the 2026-08-19 artifact-cache incident, in a new coat).
/// </remarks>
internal static class MaintenanceQuarantine
{
    /// <summary>The folder the quarantine defaults to, directly below the services root.</summary>
    internal const string DefaultFolderName = ".maintenance-quarantine";

    /// <summary>The sidecar written beside a quarantined directory with where it came from and why.</summary>
    internal const string ReasonSidecarSuffix = ".quarantine.txt";

    private const int MaxMoveAttempts = 20;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Every quarantine root this host may use, normalized. With a configured
    /// <see cref="HostAgentMaintenanceQuarantineSettings.Path"/> there is exactly one;
    /// otherwise one default folder below the services root and one below the HostAgent
    /// install root, de-duplicated (in the default layout they are the same folder). Two
    /// defaults because the two roots the cleanup removes below may sit on different
    /// volumes, and a directory can only be moved to a quarantine on its own volume.
    /// Resolved whether or not the quarantine is enabled: the scanner's and the cleanup's
    /// guards must keep protecting a quarantine that is still on disk after an operator
    /// switched it off, or the next scan would flag it as an orphan and the next cleanup
    /// would delete every retained copy in it.
    /// <see cref="HostAgentMaintenanceQuarantineSettings.IsEnabled"/> only decides whether a
    /// cleanup moves or deletes.
    /// </summary>
    internal static IReadOnlyList<string> ResolveRoots(HostAgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var quarantine = settings.MaintenanceQuarantine;
        var roots = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(quarantine.Path))
        {
            AddRoot(roots, quarantine.Path.Trim());
            return roots;
        }

        foreach (var parent in new[] { settings.ServicesRoot, settings.SelfUpgrade.InstallRoot })
        {
            if (!string.IsNullOrWhiteSpace(parent))
            {
                AddRoot(roots, System.IO.Path.Join(parent.Trim(), DefaultFolderName));
            }
        }

        return roots;
    }

    /// <summary>
    /// The quarantine root for <paramref name="directory"/>: the configured or default root
    /// on the directory's own volume, or <see langword="null"/> when none of the roots is.
    /// </summary>
    internal static string? ResolveRootFor(HostAgentSettings settings, string directory)
    {
        var volume = System.IO.Path.GetPathRoot(directory);
        if (string.IsNullOrEmpty(volume))
        {
            return null;
        }

        return ResolveRoots(settings)
            .FirstOrDefault(root => string.Equals(System.IO.Path.GetPathRoot(root), volume, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The first (or only) root, for the places that need one path to name.</summary>
    internal static string? ResolveRoot(HostAgentSettings settings)
        => ResolveRoots(settings).FirstOrDefault();

    private static void AddRoot(List<string> roots, string path)
    {
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(path)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return;
        }

        if (!roots.Any(existing => string.Equals(existing, full, StringComparison.OrdinalIgnoreCase)))
        {
            roots.Add(full);
        }
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is the quarantine root, sits under it, or
    /// contains it. The parent direction matters as much as for the artifact cache:
    /// removing a directory that contains the quarantine removes the quarantine.
    /// </summary>
    internal static bool IsQuarantinePath(string? quarantineRoot, string candidate)
        => !string.IsNullOrWhiteSpace(quarantineRoot)
            && (OmpPathContainment.IsSameOrChildPath(quarantineRoot, candidate)
                || OmpPathContainment.IsSameOrChildPath(candidate, quarantineRoot));

    /// <summary>True when <paramref name="candidate"/> is, is inside, or contains any of the roots.</summary>
    internal static bool IsQuarantinePath(IReadOnlyList<string> quarantineRoots, string candidate)
        => quarantineRoots.Any(root => IsQuarantinePath(root, candidate));

    /// <summary>
    /// Moves <paramref name="directory"/> into the quarantine root and writes a reason
    /// sidecar beside it. Refuses, with the reason in <paramref name="refusal"/>, when the
    /// two are on different volumes: <see cref="Directory.Move(string, string)"/> throws
    /// across volumes, and a copy-then-delete that fails halfway is worse than a refusal.
    /// </summary>
    internal static bool TryMoveToQuarantine(
        string quarantineRoot,
        string directory,
        long maintenanceFindingId,
        string reason,
        DateTime nowUtc,
        CancellationToken cancellationToken,
        out string destination,
        out string refusal,
        out string? sidecarWarning)
    {
        destination = string.Empty;
        refusal = string.Empty;
        sidecarWarning = null;

        var sourceVolume = System.IO.Path.GetPathRoot(directory);
        var quarantineVolume = System.IO.Path.GetPathRoot(quarantineRoot);
        if (string.IsNullOrEmpty(sourceVolume)
            || string.IsNullOrEmpty(quarantineVolume)
            || !string.Equals(sourceVolume, quarantineVolume, StringComparison.OrdinalIgnoreCase))
        {
            refusal = $"Refusing to quarantine '{directory}': the quarantine root '{quarantineRoot}' is on another volume, and a move across volumes cannot be made safely. Configure HostAgent:MaintenanceQuarantine:Path on the directory's volume (or leave it empty so a default folder is used below the services root and the install root), or set IsEnabled to false to delete directly.";
            return false;
        }

        Directory.CreateDirectory(quarantineRoot);
        OmpReparsePointGuard.EnsureNotReparsePoint(quarantineRoot, "HostAgent maintenance quarantine root");

        destination = BuildDestinationPath(quarantineRoot, directory, maintenanceFindingId, nowUtc);
        MoveWithRetry(directory, destination, cancellationToken);

        // The sweep orders by this stamp. Directory.Move keeps the source's own last-write
        // time, which may be years old and would age the directory out on the next sweep.
        try
        {
            Directory.SetLastWriteTimeUtc(destination, nowUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The sidecar written below carries the same stamp and is enough to order on.
        }

        // The move is done; the sidecar only explains it. A sidecar that cannot be
        // written must not report the entry as failed when its directory is already
        // in the quarantine (a retry would only find the source missing).
        var sidecar = destination + ReasonSidecarSuffix;
        try
        {
            File.WriteAllText(
                sidecar,
                $"Quarantined {nowUtc:O} by HostAgent maintenance cleanup.{Environment.NewLine}" +
                $"Original path: {directory}{Environment.NewLine}" +
                $"Maintenance finding: {maintenanceFindingId}{Environment.NewLine}" +
                $"Reason: {reason}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            sidecarWarning = $"The reason file '{sidecar}' could not be written ({ex.Message}).";
        }

        return true;
    }

    /// <summary>
    /// Removes quarantined directories older than the configured retention, then the
    /// oldest until the quarantine fits under its size cap. Says what it removed at
    /// Information so a pruned quarantine and an empty one cannot be confused.
    /// </summary>
    internal static void Sweep(HostAgentSettings settings, ILogger logger, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(logger);
        foreach (var root in ResolveRoots(settings))
        {
            SweepRoot(root, settings, logger, nowUtc);
        }
    }

    private static void SweepRoot(string root, HostAgentSettings settings, ILogger logger, DateTime nowUtc)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var quarantine = settings.MaintenanceQuarantine;
        DateTime? cutoff = quarantine.RetentionDays > 0 ? nowUtc.AddDays(-quarantine.RetentionDays) : null;
        if (cutoff is null && quarantine.RetentionMaxBytes <= 0)
        {
            return;
        }

        try
        {
            OmpReparsePointGuard.EnsureNotReparsePoint(root, "HostAgent maintenance quarantine root");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(ex, "HostAgent maintenance quarantine root '{Root}' is a link or could not be checked; it is not swept.", root);
            return;
        }

        List<RetentionSweepEntry> entries;
        try
        {
            entries = EnumerateEntries(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "HostAgent maintenance quarantine could not be enumerated at '{Root}'.", root);
            return;
        }

        var plan = RetentionSweepPlanner.Plan(entries, cutoff, quarantine.RetentionMaxBytes, IsReasonSidecar);
        if (plan.AgedOut.Count == 0 && plan.OverSizeCap.Count == 0)
        {
            return;
        }

        var removed = new List<string>();
        long freedBytes = 0;
        foreach (var entry in plan.AgedOut.Concat(plan.OverSizeCap))
        {
            if (!TryRemoveEntry(entry.Path, logger))
            {
                continue;
            }

            removed.Add(System.IO.Path.GetFileName(entry.Path));
            freedBytes += entry.LengthBytes;
        }

        if (removed.Count == 0)
        {
            return;
        }

        const int NamedLimit = 10;
        var named = string.Join(", ", removed.Take(NamedLimit));
        if (removed.Count > NamedLimit)
        {
            named = $"{named} (and {removed.Count - NamedLimit} more)";
        }

        logger.LogInformation(
            "HostAgent pruned the maintenance quarantine. Root={Root}, Removed={RemovedCount} entr(y/ies) freeing {FreedMegabytes} MB (AgedOut={AgedOutCount}, OverSizeCap={OverSizeCapCount}), Remaining={RemainingCount} using {RemainingMegabytes} MB. RetentionDays={RetentionDays}, MaxMegabytes={MaxMegabytes}. Removed={Removed}",
            root,
            removed.Count,
            freedBytes / (1024 * 1024),
            plan.AgedOut.Count,
            plan.OverSizeCap.Count,
            entries.Count - removed.Count,
            (plan.TotalBytesBefore - freedBytes) / (1024 * 1024),
            quarantine.RetentionDays,
            quarantine.RetentionMaxBytes / (1024 * 1024),
            named);
    }

    /// <summary>
    /// The quarantine's entries: each quarantined directory with its total size, and each
    /// reason sidecar.
    /// </summary>
    internal static List<RetentionSweepEntry> EnumerateEntries(string root)
    {
        var entries = new List<RetentionSweepEntry>();
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new DirectoryInfo(directory);
            long size = 0;
            // A junction planted inside a quarantined tree must not be walked: sizing
            // through it would count (and a future change could touch) whatever is on
            // the other side (R8-P2). Directory.Delete does not follow it either.
            foreach (var file in info.EnumerateFiles("*", OmpReparsePointGuard.RecursiveNoFollow))
            {
                size += file.Length;
            }

            entries.Add(new RetentionSweepEntry(info.FullName, size, info.LastWriteTimeUtc));
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(file);
            entries.Add(new RetentionSweepEntry(info.FullName, info.Length, info.LastWriteTimeUtc));
        }

        return entries;
    }

    internal static bool IsReasonSidecar(string path)
        => path.EndsWith(ReasonSidecarSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>&lt;folder&gt;__&lt;stamp&gt;__finding-&lt;id&gt;</c>, with a counter when two
    /// quarantines of the same folder land in the same second.
    /// </summary>
    internal static string BuildDestinationPath(string quarantineRoot, string directory, long maintenanceFindingId, DateTime nowUtc)
    {
        var folderName = System.IO.Path.GetFileName(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName))
        {
            folderName = "directory";
        }

        var baseName = $"{folderName}__{nowUtc:yyyyMMdd-HHmmss}__finding-{maintenanceFindingId}";
        var candidate = System.IO.Path.Join(quarantineRoot, baseName);
        for (var counter = 2; Directory.Exists(candidate) || File.Exists(candidate) || File.Exists(candidate + ReasonSidecarSuffix); counter++)
        {
            candidate = System.IO.Path.Join(quarantineRoot, $"{baseName}-{counter}");
        }

        return candidate;
    }

    private static void MoveWithRetry(string source, string destination, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException) when (attempt < MaxMoveAttempts && Directory.Exists(source))
            {
                WaitBeforeRetry(cancellationToken);
            }
            catch (UnauthorizedAccessException) when (attempt < MaxMoveAttempts && Directory.Exists(source))
            {
                WaitBeforeRetry(cancellationToken);
            }
        }
    }

    private static void WaitBeforeRetry(CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(MoveRetryDelay))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static bool TryRemoveEntry(string path, ILogger logger)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "HostAgent maintenance quarantine could not remove '{Path}'; it will be retried on the next sweep.", path);
            return false;
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
}
