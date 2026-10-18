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
        // Compared in canonical form: 'App_Data/./omp-deployment.lock.json' and
        // 'App_Data//omp-deployment.lock.json' are the reserved control file, and a
        // non-canonicalizable path is never a safe non-reserved identity either.
        var canonical = CanonicalRelativePath.TryCanonicalize(relativePath);
        return canonical is not null && Reserved.Contains(canonical);
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

            var canonical = CanonicalRelativePath.TryCanonicalize(file.RelativePath);
            if (canonical is null)
            {
                continue;
            }

            if (payloadPaths is not null && payloadPaths.Contains(canonical))
            {
                continue;
            }

            var content = ArtifactConfigurationFileWriter.Render(file.FileContent, variables);
            entries.Add(new AppliedOverlayFileEntry
            {
                RelativePath = CanonicalRelativePath.ToOsPath(canonical),
                ContentLength = Utf8NoBom.GetByteCount(content),
                ContentSha256 = ComputeContentSha256(content)
            });
        }

        return entries;
    }

    /// <summary>
    /// Overlay whole files whose canonical path the artifact payload itself ships. These are
    /// not standalone overlay-owned files (the payload file must never be deleted), but the
    /// overlay content <see cref="ArtifactConfigurationFileWriter.ApplyAsync"/> writes over
    /// the mirrored payload copy must still be recorded: without the recorded overlay hash a
    /// later overlay removal has no proof that the on-disk bytes are HostAgent's overlay
    /// rather than an independent modification, so the payload bytes could never be safely
    /// restored (F1). Recorded here, planned as restores -- never removals -- by
    /// <see cref="Plan"/>.
    /// </summary>
    internal static IReadOnlyList<AppliedOverlayFileEntry> ResolveDesiredPayloadOverlaidFiles(
        IReadOnlyList<ArtifactConfigurationFileDescriptor> files,
        IReadOnlyDictionary<string, string> variables,
        IReadOnlySet<string>? payloadPaths)
    {
        var entries = new List<AppliedOverlayFileEntry>();
        if (payloadPaths is null)
        {
            return entries;
        }

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

            var canonical = CanonicalRelativePath.TryCanonicalize(file.RelativePath);
            if (canonical is null || !payloadPaths.Contains(canonical))
            {
                continue;
            }

            var content = ArtifactConfigurationFileWriter.Render(file.FileContent, variables);
            entries.Add(new AppliedOverlayFileEntry
            {
                RelativePath = CanonicalRelativePath.ToOsPath(canonical),
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
            // Deletion/restoration decisions need a completeness proof, so this
            // enumeration is strict: it never follows a reparse point and never silently
            // skips an inaccessible directory (IgnoreInaccessible=false). An unreadable
            // entry throws and the catch below marks the inventory incomplete (F4). The
            // shared RecursiveNoFollow options (IgnoreInaccessible=true) stay unchanged
            // for the mirror and other best-effort enumerations.
            var strictNoFollow = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            };
            var files = enumerateFiles is not null
                ? enumerateFiles(sourceLocalPath)
                : Directory.EnumerateFiles(sourceLocalPath, "*", strictNoFollow);
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
        => CanonicalRelativePath.TryCanonicalize(relativePath)
            ?? relativePath.Trim().Replace('\\', '/').Trim('/');

    /// <summary>
    /// Plans removal/restoration against the persisted trusted inventory. A missing inventory
    /// has nothing to reconcile (and no warning unless a legacy untrusted file is present);
    /// an unreadable, foreign-schema or foreign-instance inventory warns and plans nothing.
    /// A recorded file that is no longer desired is removable only when it still exists as a
    /// regular file, is not a reparse point, is provably not payload-owned or reserved, and
    /// hashes to the recorded content. A recorded file whose path the payload owns is never
    /// removed: it is planned as a payload restoration (the recorded overlay bytes are
    /// replaced by the current artifact payload bytes) under the same byte-identical proof.
    /// A null or incomplete payload inventory cannot prove the absence of a collision, so it
    /// plans neither removal nor restoration: the entries are retained (the inventory is
    /// preserved, the warning repeats) and the deployment itself proceeds.
    /// </summary>
    internal static OverlayRemovalPlan Plan(
        string stateRoot,
        string targetPath,
        WebAppDeploymentDescriptor deployment,
        IReadOnlyList<AppliedOverlayFileEntry> desiredFiles,
        PayloadPathInventory? payload = null)
        => Plan(stateRoot, targetPath, deployment.AppInstanceId, deployment.HostKey, desiredFiles, payload);

    /// <summary>
    /// Service-app overload of <see cref="Plan(string, string, WebAppDeploymentDescriptor, IReadOnlyList{AppliedOverlayFileEntry}, PayloadPathInventory?)"/>:
    /// the same trusted-inventory reconciliation, bound to the app instance and host the
    /// service deployment carries.
    /// </summary>
    internal static OverlayRemovalPlan Plan(
        string stateRoot,
        string targetPath,
        ServiceAppDeploymentDescriptor deployment,
        IReadOnlyList<AppliedOverlayFileEntry> desiredFiles,
        PayloadPathInventory? payload = null)
        => Plan(stateRoot, targetPath, deployment.AppInstanceId, deployment.HostKey, desiredFiles, payload);

    private static OverlayRemovalPlan Plan(
        string stateRoot,
        string targetPath,
        Guid appInstanceId,
        string? hostKey,
        IReadOnlyList<AppliedOverlayFileEntry> desiredFiles,
        PayloadPathInventory? payload)
    {
        var inventory = AppliedOverlayFileInventory.TryRead(stateRoot, targetPath, appInstanceId, out var readDiagnostic);
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
        if (inventory.AppInstanceId != appInstanceId)
        {
            return new OverlayRemovalPlan(
                HasRemovals: false,
                RemoveEntries: [],
                Warning: $"Applied overlay file inventory belongs to app instance '{inventory.AppInstanceId}' and was not used for removal.",
                ShouldRefreshInventory: false,
                RetainedEntries: []);
        }

        if (!string.Equals(inventory.HostKey ?? string.Empty, hostKey ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            return new OverlayRemovalPlan(
                HasRemovals: false,
                RemoveEntries: [],
                Warning: $"Applied overlay file inventory belongs to host '{inventory.HostKey}' and was not used for removal.",
                ShouldRefreshInventory: false,
                RetainedEntries: []);
        }

        var desiredPaths = desiredFiles
            .Select(static file => CanonicalRelativePath.TryCanonicalize(file.RelativePath))
            .Where(static path => path is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removals = new List<AppliedOverlayFileEntry>();
        var restores = new List<AppliedOverlayFileEntry>();
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

            var canonicalEntry = CanonicalRelativePath.TryCanonicalize(entry.RelativePath);
            if (canonicalEntry is null)
            {
                warnings.Add($"Applied overlay file '{entry.RelativePath}' has an unsafe or ambiguous relative path and was not reconciled; it was left in place. Remove the stale inventory entry by deleting the inventory file, then let a full deployment recreate it.");
                retained.Add(entry);
                continue;
            }

            if (desiredPaths.Contains(canonicalEntry))
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
            // so neither removal nor payload restoration is authorized for this entry:
            // retain it (the inventory is preserved, the warning repeats) and let a
            // later cycle retry. This says the inventory is unknown -- never that the
            // file is protected.
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

            var isPayloadOwned = payload.Paths.Contains(canonicalEntry);

            if (!TryResolvePath(targetPath, entry.RelativePath, out var path, out var pathWarning))
            {
                warnings.Add(pathWarning);
                retained.Add(entry);
                continue;
            }

            // A file that no longer exists was already removed; it is dropped from the
            // rewritten inventory without a warning. That is intentional and idempotent:
            // the reconciliation goal (the overlay bytes are gone) is already achieved.
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
                warnings.Add(isPayloadOwned
                    ? $"Applied overlay file '{entry.RelativePath}' was modified after HostAgent wrote it and was left in place; the artifact payload content was not restored over the modified file."
                    : $"Applied overlay file '{entry.RelativePath}' was modified after HostAgent wrote it and was left in place; remove it manually if it is no longer needed.");
                retained.Add(entry);
                continue;
            }

            // A payload-owned path is never deleted. The on-disk bytes are still exactly
            // the recorded overlay bytes, so the only safe reconciliation is to restore
            // the current artifact payload bytes over them (F1).
            if (isPayloadOwned)
            {
                restores.Add(entry);
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
            RetainedEntries: retained)
        {
            RestoreEntries = restores
        };
    }

    /// <summary>
    /// Restores planned payload files to the current artifact payload bytes. A payload file
    /// is never deleted and never overwritten on suspicion: the target must still hash to
    /// the recorded overlay content (re-verified immediately before the write), neither
    /// side may be a reparse point, and the payload source must be readable under the
    /// deployment source root. Any failure leaves the file in place with a warning and the
    /// inventory record survives because the caller only refreshes it on a clean result.
    /// The write is atomic (same-directory temp file plus move).
    /// </summary>
    internal static OverlayRestoreResult RestorePayloadFiles(
        string targetPath,
        string? sourceLocalPath,
        OverlayRemovalPlan plan,
        ILogger logger)
    {
        var warnings = new List<string>();
        var restored = 0;
        if (plan.RestoreEntries.Count == 0)
        {
            return new OverlayRestoreResult(0, null);
        }

        if (string.IsNullOrWhiteSpace(sourceLocalPath) || !Directory.Exists(sourceLocalPath))
        {
            var unavailable = "Artifact payload source is unavailable, so a previously overlaid payload file could not be restored to the artifact content and was left in place. Let a later deployment retry. No payload file was deleted or overwritten.";
            logger.LogWarning("{Warning}", unavailable);
            return new OverlayRestoreResult(0, unavailable);
        }

        foreach (var entry in plan.RestoreEntries)
        {
            if (OverlayReservedPaths.IsReserved(entry.RelativePath))
            {
                continue;
            }

            var canonical = CanonicalRelativePath.TryCanonicalize(entry.RelativePath);
            if (canonical is null)
            {
                logger.LogWarning("Applied overlay file '{RelativePath}' could not be resolved for payload restoration and was left in place.", entry.RelativePath);
                warnings.Add($"Applied overlay file '{entry.RelativePath}' has an unsafe or ambiguous relative path; the artifact payload content was not restored and the file was left in place.");
                continue;
            }

            if (!TryResolvePath(targetPath, entry.RelativePath, out var targetFile, out var targetWarning))
            {
                logger.LogWarning("Applied overlay file '{RelativePath}' could not be resolved for payload restoration and was left in place.", entry.RelativePath);
                warnings.Add(targetWarning);
                continue;
            }

            string sourceFile;
            try
            {
                sourceFile = DeploymentPath.CombineUnderRoot(
                    sourceLocalPath, CanonicalRelativePath.ToOsPath(canonical), "Artifact payload file");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or PathTooLongException)
            {
                logger.LogWarning("Applied overlay file '{RelativePath}' payload source could not be resolved and was left in place.", entry.RelativePath);
                warnings.Add($"Artifact payload file '{entry.RelativePath}' could not be resolved; the previously overlaid file was left in place and no content was overwritten.");
                continue;
            }

            if (!File.Exists(targetFile))
            {
                // Already gone: nothing to restore over. A full apply recreates payload files.
                continue;
            }

            if (!File.Exists(sourceFile)
                || HasReparsePointInPath(sourceFile, sourceLocalPath)
                || HasReparsePointInPath(targetFile, targetPath)
                || !FileMatchesEntry(targetFile, entry))
            {
                logger.LogWarning("Applied overlay file '{RelativePath}' changed or could not be verified before payload restoration and was left in place.", entry.RelativePath);
                warnings.Add($"Applied overlay file '{entry.RelativePath}' changed or could not be verified before payload restoration and was left in place; no content was overwritten.");
                continue;
            }

            byte[] payloadBytes;
            try
            {
                payloadBytes = File.ReadAllBytes(sourceFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Artifact payload file '{RelativePath}' could not be read for restoration.", entry.RelativePath);
                warnings.Add($"Artifact payload file '{entry.RelativePath}' could not be read, so the previously overlaid file was left in place; no content was overwritten.");
                continue;
            }

            var directory = Path.GetDirectoryName(targetFile)!;
            var tempPath = Path.Join(directory, "." + Path.GetFileName(targetFile) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(tempPath, payloadBytes);
                // Re-verify immediately before the atomic replace: a file modified since
                // planning is independent content and must never be overwritten.
                if (!FileMatchesEntry(targetFile, entry))
                {
                    warnings.Add($"Applied overlay file '{entry.RelativePath}' changed before payload restoration and was left in place; no content was overwritten.");
                    continue;
                }

                File.Move(tempPath, targetFile, overwrite: true);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Applied overlay file '{RelativePath}' could not be restored to the artifact payload content.", entry.RelativePath);
                warnings.Add($"Applied overlay file '{entry.RelativePath}' could not be restored to the artifact payload content and was left in place.");
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort temp cleanup; the restore result stands.
                }
            }
        }

        return new OverlayRestoreResult(restored, warnings.Count > 0 ? string.Join(" ", warnings) : null);
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
            .Select(static file => CanonicalRelativePath.TryCanonicalize(file.RelativePath) ?? file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in plan.RetainedEntries.Concat(plan.RemoveEntries))
        {
            if (!seen.Add(CanonicalRelativePath.TryCanonicalize(entry.RelativePath) ?? entry.RelativePath))
            {
                continue;
            }

            if (TryResolvePath(targetPath, entry.RelativePath, out var path, out _)
                && File.Exists(path))
            {
                result.Add(entry);
            }
        }

        // A restore entry is kept only when the on-disk bytes are still the recorded
        // overlay bytes (restoration did not happen); once the payload bytes are in
        // place the record is complete and must not be carried over.
        foreach (var entry in plan.RestoreEntries)
        {
            if (!seen.Add(CanonicalRelativePath.TryCanonicalize(entry.RelativePath) ?? entry.RelativePath))
            {
                continue;
            }

            if (TryResolvePath(targetPath, entry.RelativePath, out var path, out _)
                && File.Exists(path)
                && FileMatchesEntry(path, entry))
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
        => TryWriteInventory(
            stateRoot,
            targetPath,
            deployment.AppInstanceId,
            deployment.AppInstanceKey,
            deployment.HostKey,
            desiredFiles);

    /// <summary>Service-app overload of the trusted-inventory write; same binding, same limits.</summary>
    internal static string? TryWriteInventory(
        string stateRoot,
        string targetPath,
        ServiceAppDeploymentDescriptor deployment,
        IReadOnlyList<AppliedOverlayFileEntry> desiredFiles)
        => TryWriteInventory(
            stateRoot,
            targetPath,
            deployment.AppInstanceId,
            deployment.AppInstanceKey,
            deployment.HostKey,
            desiredFiles);

    private static string? TryWriteInventory(
        string stateRoot,
        string targetPath,
        Guid appInstanceId,
        string appInstanceKey,
        string hostKey,
        IReadOnlyList<AppliedOverlayFileEntry> desiredFiles)
        => AppliedOverlayFileInventory.TryWrite(
            stateRoot,
            targetPath,
            appInstanceId,
            appInstanceKey,
            hostKey,
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
            .Select(static file => CanonicalRelativePath.TryCanonicalize(file.RelativePath) ?? file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (inventoryPaths.Count != desiredFiles.Count)
        {
            return false;
        }

        return desiredFiles.All(file => inventoryPaths.Contains(
            CanonicalRelativePath.TryCanonicalize(file.RelativePath) ?? file.RelativePath));
    }

    private static string ComputeContentSha256(string content)
        => Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(content))).ToLowerInvariant();
}

internal sealed record OverlayRemovalPlan(
    bool HasRemovals,
    IReadOnlyList<AppliedOverlayFileEntry> RemoveEntries,
    string? Warning,
    bool ShouldRefreshInventory,
    IReadOnlyList<AppliedOverlayFileEntry> RetainedEntries)
{
    /// <summary>Recorded payload-owned files to restore to artifact payload bytes, never delete.</summary>
    internal IReadOnlyList<AppliedOverlayFileEntry> RestoreEntries { get; init; } = [];

    internal bool HasRestores => RestoreEntries.Count > 0;
}

internal sealed record OverlayRemovalResult(int RemovedCount, string? Warning);

internal sealed record OverlayRestoreResult(int RestoredCount, string? Warning);
