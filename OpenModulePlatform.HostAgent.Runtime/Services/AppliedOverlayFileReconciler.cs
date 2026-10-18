using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// HostAgent runtime control files that a configuration overlay must never supply, overwrite
/// or be classified as owning. Generic user <c>App_Data</c> files remain allowed; only these
/// exact control paths are reserved (F6).
/// </summary>
internal static class OverlayReservedPaths
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "App_Data/" + AppliedOverlayFileInventory.FileName,
        DeploymentLockFile.RelativePath.Replace('\\', '/'),
        "App_Data/omp-runtime-stopped-for-deployment.json"
    };

    internal static bool IsReserved(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var normalized = relativePath.Trim().Replace('\\', '/').Trim('/');
        return Reserved.Contains(normalized);
    }
}

/// <summary>
/// Reconciles whole-file config overlays on the web app already-applied fast path. An overlay
/// file whose path the artifact does not own stands alone on disk, so when the overlay stops
/// supplying it, <see cref="ArtifactConfigurationFileWriter.AreApplied"/> (which only examines
/// desired files) keeps reporting the deployment applied and the file persists forever.
///
/// This type closes that gap without guessing: it only removes a file recorded in the trusted
/// applied-overlay-file inventory (HostAgent-owned state outside the web root, bound to the
/// app instance and target path), only when the path is provably not owned by the artifact
/// payload (a complete payload inventory) and is not a reserved runtime control path, and
/// only when the on-disk file is still byte-identical to what HostAgent wrote. A modified,
/// unreadable, reparse-point, payload-owned, foreign-inventory or unknown-payload path fails
/// safe and is reported, never deleted.
/// </summary>
internal static class AppliedOverlayFileReconciler
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The overlay-owned whole files in the resolved set, rendered to their effective content so
    /// the recorded hash matches what <see cref="ArtifactConfigurationFileWriter.ApplyAsync"/>
    /// writes. The built-in appsettings.json and any artifact-owned or merged file are already
    /// excluded by their descriptor flag. Reserved runtime control paths and paths the artifact
    /// payload itself ships are never overlay-owned (F2, F6).
    /// </summary>
    internal static IReadOnlyList<AppliedOverlayFileEntry> ResolveDesiredOverlayFiles(
        IReadOnlyList<ArtifactConfigurationFileDescriptor> files,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlySet<string>? payloadPaths = null)
    {
        var entries = new List<AppliedOverlayFileEntry>();
        foreach (var file in files)
        {
            if (!file.IsOverlayOwnedWholeFile || string.IsNullOrWhiteSpace(file.RelativePath))
            {
                continue;
            }

            if (OverlayReservedPaths.IsReserved(file.RelativePath))
            {
                continue;
            }

            var relativePath = file.RelativePath.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (payloadPaths is not null && payloadPaths.Contains(NormalizeRelative(file.RelativePath)))
            {
                continue;
            }

            var content = ArtifactConfigurationFileWriter.Render(file.FileContent, variables);
            entries.Add(new AppliedOverlayFileEntry
            {
                RelativePath = relativePath,
                ContentLength = Utf8NoBom.GetByteCount(content),
                ContentSha256 = ComputeContentSha256(content)
            });
        }

        return entries;
    }

    /// <summary>
    /// Artifact payload inventory: the relative paths of every regular file the artifact
    /// payload ships, plus whether the enumeration completed. An incomplete inventory
    /// (missing source, unreadable entry) proves nothing about the paths it missed, so
    /// callers must not infer "no payload collision" from it: deletion stays disabled
    /// until a complete inventory is available. Ordinary deployment (mirror plus file
    /// apply) proceeds regardless.
    /// </summary>
    internal sealed record PayloadPathInventory(IReadOnlySet<string> Paths, bool IsComplete);

    /// <summary>
    /// Relative paths of every regular file the artifact payload ships, with an explicit
    /// completeness flag. A missing source or a mid-enumeration I/O failure yields an
    /// incomplete inventory, never an implicit "no payload" claim. The optional enumerator
    /// is a deterministic test seam; production always enumerates the file system.
    /// </summary>
    internal static PayloadPathInventory EnumeratePayloadPaths(
        string? sourceLocalPath,
        Func<string, IEnumerable<string>>? enumerateFiles = null)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(sourceLocalPath) || !Directory.Exists(sourceLocalPath))
        {
            return new PayloadPathInventory(paths, IsComplete: false);
        }

        try
        {
            var files = enumerateFiles is not null
                ? enumerateFiles(sourceLocalPath)
                : Directory.EnumerateFiles(sourceLocalPath, "*", OmpReparsePointGuard.RecursiveNoFollow);
            foreach (var file in files)
            {
                paths.Add(NormalizeRelative(Path.GetRelativePath(sourceLocalPath, file)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable payload cannot prove ownership of the paths it missed, and a
            // partial set must never authorize a deletion the missing entries would have
            // vetoed. Mark the inventory incomplete so planning stays fail-closed.
            return new PayloadPathInventory(paths, IsComplete: false);
        }

        return new PayloadPathInventory(paths, IsComplete: true);
    }

    internal static string NormalizeRelative(string relativePath)
        => relativePath.Trim().Replace('\\', '/').Trim('/');

    /// <summary>
    /// Plans removal against the persisted trusted inventory. A missing inventory has nothing
    /// to remove (and no warning unless a legacy untrusted file is present); an unreadable,
    /// foreign-schema or foreign-instance inventory warns and plans nothing. A recorded file
    /// that is no longer desired is removable only when it still exists as a regular file, is
    /// not a reparse point, is provably not payload-owned or reserved, and hashes to the
    /// recorded content. A null or incomplete payload inventory cannot prove the absence of
    /// a collision, so it plans no removal: the entries are retained (the inventory is
    /// preserved, the warning repeats) and the deployment itself proceeds.
    /// </summary>
    internal static OverlayRemovalPlan Plan(
        string stateRoot,
        string targetPath,
        WebAppDeploymentDescriptor deployment,
        IReadOnlyList<AppliedOverlayFileEntry> desiredFiles,
        PayloadPathInventory? payload = null)
    {
        var inventory = AppliedOverlayFileInventory.TryRead(stateRoot, targetPath, deployment.AppInstanceId, out var readDiagnostic);
        if (inventory is null)
        {
            if (readDiagnostic is not null)
            {
                return new OverlayRemovalPlan(HasRemovals: false, RemoveEntries: [], Warning: readDiagnostic, ShouldRefreshInventory: false, RetainedEntries: []);
            }

            // No history yet: nothing to remove, but adopt the currently desired set so a later
            // overlay removal becomes reconcilable. An empty desired set has nothing to adopt.
            return new OverlayRemovalPlan(
                HasRemovals: false,
                RemoveEntries: [],
                Warning: null,
                ShouldRefreshInventory: desiredFiles.Count > 0,
                RetainedEntries: []);
        }

        // Name the binding that actually mismatched: the app-instance id is already
        // enforced by TryRead, so the reachable mismatch here is the host key, and
        // blaming the app instance for it would misdirect the operator.
        if (inventory.AppInstanceId != deployment.AppInstanceId)
        {
            return new OverlayRemovalPlan(
                HasRemovals: false,
                RemoveEntries: [],
                Warning: $"Applied overlay file inventory belongs to app instance '{inventory.AppInstanceId}' and was not used for removal.",
                ShouldRefreshInventory: false,
                RetainedEntries: []);
        }

        if (!string.Equals(inventory.HostKey ?? string.Empty, deployment.HostKey ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            return new OverlayRemovalPlan(
                HasRemovals: false,
                RemoveEntries: [],
                Warning: $"Applied overlay file inventory belongs to host '{inventory.HostKey}' and was not used for removal.",
                ShouldRefreshInventory: false,
                RetainedEntries: []);
        }

        var desiredPaths = desiredFiles
            .Select(static file => file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removals = new List<AppliedOverlayFileEntry>();
        var retained = new List<AppliedOverlayFileEntry>();
        var warnings = new List<string>();
        var payloadUnknownWarned = false;

        // Defensive: TryRead validates, but never let a null list/member become a
        // NullReferenceException (not a recordable fault) that aborts the sweep.
        foreach (var entry in inventory.Files ?? [])
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.RelativePath))
            {
                warnings.Add("Applied overlay file inventory contains an invalid entry and it was not reconciled; delete the inventory file, then let a full deployment recreate it. No file was removed for that entry.");
                continue;
            }

            if (desiredPaths.Contains(entry.RelativePath))
            {
                continue;
            }

            // Reserved paths are never overlay-owned: drop the record without
            // deleting. The artifact mirror owns the reserved file's content.
            if (OverlayReservedPaths.IsReserved(entry.RelativePath))
            {
                continue;
            }

            // Without a complete payload inventory a collision cannot be ruled out,
            // so no removal is authorized for this entry: retain it (the inventory
            // is preserved, the warning repeats) and let a later cycle retry. This
            // says the inventory is unknown -- never that the file is protected.
            if (payload is null || !payload.IsComplete)
            {
                if (!payloadUnknownWarned)
                {
                    warnings.Add("Artifact payload inventory is unavailable or incomplete, so an artifact payload collision cannot be ruled out; overlay-owned file removal was skipped for this cycle. Check the HostAgent artifact cache permissions and let a later deployment retry. No file was removed.");
                    payloadUnknownWarned = true;
                }

                retained.Add(entry);
                continue;
            }

            // Payload-owned paths are never overlay-owned: drop the record without
            // deleting. The artifact mirror owns the payload file's content.
            if (payload.Paths.Contains(NormalizeRelative(entry.RelativePath)))
            {
                continue;
            }

            if (!TryResolvePath(targetPath, entry.RelativePath, out var path, out var pathWarning))
            {
                warnings.Add(pathWarning);
                retained.Add(entry);
                continue;
            }

            // A file that no longer exists was already removed; it is dropped from the
            // rewritten inventory without a warning.
            if (!File.Exists(path))
            {
                continue;
            }

            if (HasReparsePointInPath(path, targetPath))
            {
                warnings.Add($"Applied overlay file '{entry.RelativePath}' is a reparse point and was left in place.");
                retained.Add(entry);
                continue;
            }

            if (!FileMatchesEntry(path, entry))
            {
                warnings.Add($"Applied overlay file '{entry.RelativePath}' was modified after HostAgent wrote it and was left in place; remove it manually if it is no longer needed.");
                retained.Add(entry);
                continue;
            }

            removals.Add(entry);
        }

        var warning = warnings.Count > 0 ? string.Join(" ", warnings) : null;

        // Refresh only a clean inventory: when anything is left in place the inventory is
        // preserved so the warning repeats next cycle instead of being silently forgotten.
        var shouldRefresh = warning is null
            && !InventoryMatchesDesired(inventory, desiredFiles);

        return new OverlayRemovalPlan(
            HasRemovals: removals.Count > 0,
            RemoveEntries: removals,
            Warning: warning,
            ShouldRefreshInventory: shouldRefresh,
            RetainedEntries: retained);
    }

    /// <summary>
    /// Inventory to persist after a full apply: the desired set plus every recorded entry
    /// that is still on disk because it was retained (modified, unsafe, reparse point) or
    /// whose removal did not complete. Ownership is preserved until cleanup succeeds, so a
    /// file the generic mirror excludes (appsettings.*.json, App_Data, logs) stays
    /// reconcilable instead of being silently dropped with the old inventory.
    /// </summary>
    internal static IReadOnlyList<AppliedOverlayFileEntry> ResolveInventoryForWrite(
        string targetPath,
        IReadOnlyList<AppliedOverlayFileEntry> desiredFiles,
        OverlayRemovalPlan plan)
    {
        var result = new List<AppliedOverlayFileEntry>(desiredFiles);
        var seen = desiredFiles
            .Select(static file => file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in plan.RetainedEntries.Concat(plan.RemoveEntries))
        {
            if (!seen.Add(entry.RelativePath))
            {
                continue;
            }

            if (TryResolvePath(targetPath, entry.RelativePath, out var path, out _)
                && File.Exists(path))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>
    /// Relative paths (deployment-root relative, OS separators) that a full-apply mirror must
    /// not delete as stale: every retained entry plus every planned removal that is still on
    /// disk. Reuses the mirror's existing exclusion mechanism instead of a delete bypass (F5).
    /// </summary>
    internal static IReadOnlyList<string> ResolveRetainedRelativePaths(string targetPath, OverlayRemovalPlan plan)
    {
        var paths = new List<string>();
        foreach (var entry in plan.RetainedEntries.Concat(plan.RemoveEntries))
        {
            if (TryResolvePath(targetPath, entry.RelativePath, out var path, out _) && File.Exists(path))
            {
                paths.Add(entry.RelativePath);
            }
        }

        return paths;
    }

    /// <summary>
    /// Deletes the planned files, re-verifying ownership immediately before each delete so a file
    /// that changed since planning is left in place rather than removed. Reports the number
    /// actually deleted (F7), never the planned count, plus a combined warning naming the files
    /// left in place so the caller never reports a clean removal that did not happen.
    /// </summary>
    internal static OverlayRemovalResult RemoveOwnedFiles(
        string targetPath,
        OverlayRemovalPlan plan,
        ILogger logger)
    {
        var warnings = new List<string>();
        var removed = 0;
        foreach (var entry in plan.RemoveEntries)
        {
            if (OverlayReservedPaths.IsReserved(entry.RelativePath))
            {
                continue;
            }

            if (!TryResolvePath(targetPath, entry.RelativePath, out var path, out var pathWarning))
            {
                logger.LogWarning("Applied overlay file '{RelativePath}' could not be resolved for removal and was left in place.", entry.RelativePath);
                warnings.Add(pathWarning);
                continue;
            }

            if (!File.Exists(path))
            {
                // Already gone (for example the generic mirror removed it first): the goal
                // is achieved, but it was not removed here, so it is not counted (F7).
                continue;
            }

            if (HasReparsePointInPath(path, targetPath) || !FileMatchesEntry(path, entry))
            {
                logger.LogWarning("Applied overlay file '{RelativePath}' changed before removal and was left in place.", entry.RelativePath);
                warnings.Add($"Applied overlay file '{entry.RelativePath}' changed before removal and was left in place.");
                continue;
            }

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Applied overlay file '{RelativePath}' could not be removed.", entry.RelativePath);
                warnings.Add($"Applied overlay file '{entry.RelativePath}' could not be removed and was left in place.");
            }
        }

        return new OverlayRemovalResult(removed, warnings.Count > 0 ? string.Join(" ", warnings) : null);
    }

    /// <summary>Persists trusted inventory; returns an actionable diagnostic or null. Never throws for I/O.</summary>
    internal static string? TryWriteInventory(
        string stateRoot,
        string targetPath,
        WebAppDeploymentDescriptor deployment,
        IReadOnlyList<AppliedOverlayFileEntry> desiredFiles)
        => AppliedOverlayFileInventory.TryWrite(
            stateRoot,
            targetPath,
            deployment.AppInstanceId,
            deployment.AppInstanceKey,
            deployment.HostKey,
            desiredFiles);

    private static bool TryResolvePath(string targetPath, string relativePath, out string path, out string warning)
    {
        try
        {
            path = DeploymentPath.CombineUnderRoot(targetPath, relativePath, "Applied overlay file");
            warning = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or PathTooLongException)
        {
            path = string.Empty;
            warning = $"Applied overlay file inventory entry '{relativePath}' is unsafe and was not reconciled.";
            return false;
        }
    }

    private static bool HasReparsePointInPath(string path, string root)
    {
        try
        {
            OmpReparsePointGuard.EnsureNoReparsePointInPath(path, root, "Applied overlay file");
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static bool FileMatchesEntry(string path, AppliedOverlayFileEntry entry)
    {
        // R9-B1: check length before reading, so a web-root file swapped for an arbitrarily
        // large one is rejected without pulling it into memory as LocalSystem.
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists || info.Length != entry.ContentLength)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        byte[] actualBytes;
        try
        {
            actualBytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return string.Equals(
            Convert.ToHexString(SHA256.HashData(actualBytes)).ToLowerInvariant(),
            entry.ContentSha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool InventoryMatchesDesired(
        AppliedOverlayFileInventory inventory,
        IReadOnlyList<AppliedOverlayFileEntry> desiredFiles)
    {
        var inventoryPaths = (inventory.Files ?? [])
            .Where(static file => file is not null && !string.IsNullOrWhiteSpace(file.RelativePath))
            .Select(static file => file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (inventoryPaths.Count != desiredFiles.Count)
        {
            return false;
        }

        return desiredFiles.All(file => inventoryPaths.Contains(file.RelativePath));
    }

    private static string ComputeContentSha256(string content)
        => Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(content))).ToLowerInvariant();
}

internal sealed record OverlayRemovalPlan(
    bool HasRemovals,
    IReadOnlyList<AppliedOverlayFileEntry> RemoveEntries,
    string? Warning,
    bool ShouldRefreshInventory,
    IReadOnlyList<AppliedOverlayFileEntry> RetainedEntries);

internal sealed record OverlayRemovalResult(int RemovedCount, string? Warning);
