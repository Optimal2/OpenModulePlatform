using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenModulePlatform.Artifacts;
using OpenModulePlatform.HostAgent.Runtime.Models;

namespace OpenModulePlatform.HostAgent.Runtime.Services;

/// <summary>
/// Reconciles whole-file config overlays on the web app already-applied fast path. An overlay
/// file whose path the artifact does not own stands alone on disk, so when the overlay stops
/// supplying it, <see cref="ArtifactConfigurationFileWriter.AreApplied"/> (which only examines
/// desired files) keeps reporting the deployment applied and the file persists forever.
///
/// This type closes that gap without guessing: it only removes a file recorded in the
/// applied-overlay-file inventory for the same app instance, and only when the on-disk file is
/// still byte-identical to what HostAgent wrote. A modified, unreadable, reparse-point or
/// foreign-inventory path fails safe and is reported, never deleted.
/// </summary>
internal static class AppliedOverlayFileReconciler
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The overlay-owned whole files in the resolved set, rendered to their effective content so
    /// the recorded hash matches what <see cref="ArtifactConfigurationFileWriter.ApplyAsync"/>
    /// writes. The built-in appsettings.json and any artifact-owned or merged file are already
    /// excluded by their descriptor flag.
    /// </summary>
    internal static IReadOnlyList<AppliedOverlayFileEntry> ResolveDesiredOverlayFiles(
        IReadOnlyList<ArtifactConfigurationFileDescriptor> files,
        IReadOnlyDictionary<string, string> variables)
    {
        var entries = new List<AppliedOverlayFileEntry>();
        foreach (var file in files)
        {
            if (!file.IsOverlayOwnedWholeFile || string.IsNullOrWhiteSpace(file.RelativePath))
            {
                continue;
            }

            var content = ArtifactConfigurationFileWriter.Render(file.FileContent, variables);
            entries.Add(new AppliedOverlayFileEntry
            {
                RelativePath = file.RelativePath.Trim().Replace('/', Path.DirectorySeparatorChar),
                ContentLength = Utf8NoBom.GetByteCount(content),
                ContentSha256 = ComputeContentSha256(content)
            });
        }

        return entries;
    }

    /// <summary>
    /// Plans removal against the persisted inventory. A missing inventory has nothing to remove
    /// (and no warning); an unreadable, foreign-schema or foreign-instance inventory warns and
    /// plans nothing. A recorded file that is no longer desired is removable only when it still
    /// exists as a regular file, is not a reparse point, and hashes to the recorded content.
    /// </summary>
    internal static OverlayRemovalPlan Plan(
        string targetPath,
        WebAppDeploymentDescriptor deployment,
        IReadOnlyList<AppliedOverlayFileEntry> desiredFiles)
    {
        var inventory = AppliedOverlayFileInventory.TryRead(targetPath, out var readDiagnostic);
        if (inventory is null)
        {
            if (readDiagnostic is not null)
            {
                return new OverlayRemovalPlan(HasRemovals: false, RemoveEntries: [], Warning: readDiagnostic, ShouldRefreshInventory: false);
            }

            // No history yet: nothing to remove, but adopt the currently desired set so a later
            // overlay removal becomes reconcilable. An empty desired set has nothing to adopt.
            return new OverlayRemovalPlan(
                HasRemovals: false,
                RemoveEntries: [],
                Warning: null,
                ShouldRefreshInventory: desiredFiles.Count > 0);
        }

        if (inventory.AppInstanceId != deployment.AppInstanceId)
        {
            return new OverlayRemovalPlan(
                HasRemovals: false,
                RemoveEntries: [],
                Warning: $"Applied overlay file inventory belongs to app instance '{inventory.AppInstanceId}' and was not used for removal.",
                ShouldRefreshInventory: false);
        }

        var desiredPaths = desiredFiles
            .Select(static file => file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removals = new List<AppliedOverlayFileEntry>();
        var warnings = new List<string>();

        foreach (var entry in inventory.Files)
        {
            if (desiredPaths.Contains(entry.RelativePath))
            {
                continue;
            }

            if (!TryResolvePath(targetPath, entry.RelativePath, out var path, out var pathWarning))
            {
                warnings.Add(pathWarning);
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
                continue;
            }

            if (!FileMatchesEntry(path, entry))
            {
                warnings.Add($"Applied overlay file '{entry.RelativePath}' was modified after HostAgent wrote it and was left in place; remove it manually if it is no longer needed.");
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
            ShouldRefreshInventory: shouldRefresh);
    }

    /// <summary>
    /// Deletes the planned files, re-verifying ownership immediately before each delete so a file
    /// that changed since planning is left in place rather than removed. Returns null when every
    /// planned file was removed, otherwise a combined warning naming the files left in place so
    /// the caller never reports a clean removal that did not happen.
    /// </summary>
    internal static string? RemoveOwnedFiles(
        string targetPath,
        OverlayRemovalPlan plan,
        ILogger logger)
    {
        var warnings = new List<string>();
        foreach (var entry in plan.RemoveEntries)
        {
            if (!TryResolvePath(targetPath, entry.RelativePath, out var path, out var pathWarning))
            {
                logger.LogWarning("Applied overlay file '{RelativePath}' could not be resolved for removal and was left in place.", entry.RelativePath);
                warnings.Add(pathWarning);
                continue;
            }

            if (!File.Exists(path) || HasReparsePointInPath(path, targetPath) || !FileMatchesEntry(path, entry))
            {
                logger.LogWarning("Applied overlay file '{RelativePath}' changed before removal and was left in place.", entry.RelativePath);
                warnings.Add($"Applied overlay file '{entry.RelativePath}' changed before removal and was left in place.");
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Applied overlay file '{RelativePath}' could not be removed.", entry.RelativePath);
                warnings.Add($"Applied overlay file '{entry.RelativePath}' could not be removed and was left in place.");
            }
        }

        return warnings.Count > 0 ? string.Join(" ", warnings) : null;
    }

    internal static void WriteInventory(
        string targetPath,
        WebAppDeploymentDescriptor deployment,
        IReadOnlyList<AppliedOverlayFileEntry> desiredFiles)
        => AppliedOverlayFileInventory.Write(
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
        var inventoryPaths = inventory.Files
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
    bool ShouldRefreshInventory);
