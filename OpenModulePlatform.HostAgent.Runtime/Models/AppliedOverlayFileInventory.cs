using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenModulePlatform.Artifacts;

namespace OpenModulePlatform.HostAgent.Runtime.Models;

/// <summary>
/// Trusted inventory of the whole configuration files a config overlay owns for one web app
/// instance. The inventory is HostAgent-owned state: it lives under
/// <c>HostAgent:LocalArtifactCacheRoot</c> (outside every web root, writable only by the
/// HostAgent identity), in a file whose name binds it to one deployment target path and app
/// instance. Only a relative path recorded here -- and still byte-identical on disk -- is ever
/// removed, so an operator's files, the artifact's payload files and the built-in
/// appsettings.json are never deleted by this mechanism.
///
/// The legacy location (<c>App_Data/omp-applied-overlay-files.json</c> inside the web root) is
/// app-pool writable, so a well-formed file there proves nothing: it is treated as UNKNOWN,
/// reported with an actionable warning, and never used to authorize a deletion and never
/// migrated into trusted state.
/// </summary>
public sealed class AppliedOverlayFileInventory
{
    public const string Schema = "OpenModulePlatform.AppliedOverlayFiles.v1";
    public const string FileName = "omp-applied-overlay-files.json";

    internal const int MaxInventoryBytes = 1_048_576;
    internal const int MaxEntries = 512;
    internal const int MaxRelativePathLength = 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    // No default initializer: a missing or case-variant "SchemaName" property must fail the
    // schema check instead of silently falling back to the expected value (F8).
    public string SchemaName { get; init; } = string.Empty;

    public Guid AppInstanceId { get; init; }

    public string AppInstanceKey { get; init; } = string.Empty;

    public string HostKey { get; init; } = string.Empty;

    /// <summary>Normalized full deployment target path this inventory is bound to.</summary>
    public string TargetPath { get; init; } = string.Empty;

    public DateTimeOffset RecordedUtc { get; init; }

    public List<AppliedOverlayFileEntry> Files { get; init; } = [];

    /// <summary>Legacy app-pool-writable location. Detection only; never trusted, never migrated.</summary>
    public static string GetPath(string targetPath)
        => Path.Join(Path.GetFullPath(targetPath), "App_Data", FileName);

    public static bool Exists(string targetPath)
        => File.Exists(GetPath(targetPath));

    /// <summary>
    /// Trusted location: <c>&lt;stateRoot&gt;/.overlay-state/&lt;binding hash&gt;.json</c>, where the
    /// binding hash covers the normalized target path and the app instance id, so state written
    /// for one app/root can never be replayed for another.
    /// </summary>
    public static string GetTrustedPath(string stateRoot, string targetPath, Guid appInstanceId)
    {
        var binding = NormalizeTargetPath(targetPath) + "\n" + appInstanceId.ToString("D");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding))).ToLowerInvariant();
        return Path.Join(Path.GetFullPath(stateRoot.Trim()), ".overlay-state", hash + ".json");
    }

    internal static string NormalizeTargetPath(string targetPath)
        => Path.GetFullPath(targetPath.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Reads and validates the trusted inventory. A missing trusted file returns null with no
    /// diagnostic when no legacy file exists either (there is simply no history to reconcile);
    /// a legacy App_Data file, an unreadable, malformed, foreign-schema, mis-bound or
    /// reparse-point trusted file returns null with an actionable diagnostic so the caller can
    /// warn instead of guessing. No file is ever removed on the basis of untrusted state.
    /// </summary>
    public static AppliedOverlayFileInventory? TryRead(
        string stateRoot,
        string targetPath,
        Guid expectedAppInstanceId,
        out string? diagnostic)
    {
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(stateRoot))
        {
            diagnostic = "Applied overlay file inventory is unavailable because HostAgent:LocalArtifactCacheRoot is not configured; overlay-owned file removal was skipped for this cycle. No file was removed.";
            return null;
        }

        var path = GetTrustedPath(stateRoot, targetPath, expectedAppInstanceId);
        if (!File.Exists(path))
        {
            // Legacy untrusted inventory: unknown ownership, actionable warning, no deletion.
            var legacyPath = GetPath(targetPath);
            if (File.Exists(legacyPath) || Directory.Exists(legacyPath))
            {
                diagnostic = $"Applied overlay file inventory at '{legacyPath}' is in an app-pool-writable location and is not trusted as proof of overlay ownership; overlay-owned file removal was skipped for this cycle. Delete that file, then let a full deployment recreate trusted state. No file was removed.";
            }

            return null;
        }

        // The trusted root is HostAgent-owned, but still refuse to follow a planted link:
        // the whole path up to the state root is checked, not only the leaf.
        try
        {
            OmpReparsePointGuard.EnsureNoReparsePointInPath(
                path, Path.GetFullPath(stateRoot.Trim()), "Applied overlay file inventory");
        }
        catch (IOException)
        {
            diagnostic = $"Applied overlay file inventory at '{path}' is a reparse point and was not read; overlay-owned file removal was skipped for this cycle. Remove the reparse point or delete the inventory file, then let a full deployment recreate it. No file was removed.";
            return null;
        }

        // Bound the read before allocating it as LocalSystem: the inventory is a small control
        // file, never a payload (same rule as the overlay-file length check, R9-B1).
        byte[] bytes;
        try
        {
            if (new FileInfo(path).Length > MaxInventoryBytes)
            {
                diagnostic = $"Applied overlay file inventory at '{path}' exceeds the {MaxInventoryBytes}-byte limit and was not read; overlay-owned file removal was skipped for this cycle. Delete the inventory file, then let a full deployment recreate it. No file was removed.";
                return null;
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostic = UnreadableDiagnostic(path);
            return null;
        }

        if (bytes.Length > MaxInventoryBytes)
        {
            diagnostic = $"Applied overlay file inventory at '{path}' exceeds the {MaxInventoryBytes}-byte limit and was not read; overlay-owned file removal was skipped for this cycle. Delete the inventory file, then let a full deployment recreate it. No file was removed.";
            return null;
        }

        // F8: require the exact (case-sensitive) serialized property identity. A case-variant
        // or absent binding must be rejected, not silently defaulted into acceptance.
        if (!HasExactPropertyIdentity(bytes))
        {
            diagnostic = $"Applied overlay file inventory at '{path}' is structurally invalid and was not used; overlay-owned file removal was skipped for this cycle. Delete the inventory file, then let a full deployment recreate it. No file was removed.";
            return null;
        }

        AppliedOverlayFileInventory? inventory;
        try
        {
            inventory = JsonSerializer.Deserialize<AppliedOverlayFileInventory>(bytes, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            diagnostic = UnreadableDiagnostic(path);
            return null;
        }

        if (inventory is null)
        {
            diagnostic = UnreadableDiagnostic(path);
            return null;
        }

        // The schema value comes from a file; never echo it into a diagnostic that is
        // persisted and surfaced in Portal.
        if (!string.Equals(inventory.SchemaName, Schema, StringComparison.Ordinal))
        {
            diagnostic = $"Applied overlay file inventory at '{path}' has an unsupported schema and was not used; overlay-owned file removal was skipped for this cycle. Delete the inventory file, then let a full deployment recreate it. No file was removed.";
            return null;
        }

        if (!IsValid(inventory)
            || inventory.AppInstanceId != expectedAppInstanceId
            || !string.Equals(inventory.TargetPath, NormalizeTargetPath(targetPath), StringComparison.OrdinalIgnoreCase))
        {
            diagnostic = $"Applied overlay file inventory at '{path}' is structurally invalid and was not used; overlay-owned file removal was skipped for this cycle. Delete the inventory file, then let a full deployment recreate it. No file was removed.";
            return null;
        }

        return inventory;
    }

    /// <summary>
    /// Persists the trusted inventory. Enforces exactly the limits <see cref="TryRead"/>
    /// enforces (entry count, serialized size, per-path length) before writing, so HostAgent
    /// never writes an inventory it would later reject. Inventory inability, tamper or I/O
    /// failure degrades to an actionable diagnostic (returned, never thrown for I/O): removal
    /// stays disabled for the cycle, but the deployment itself proceeds. No partial state is
    /// left: the temp file is only moved over the target after every check passes.
    /// </summary>
    /// <returns>Null on success; otherwise an actionable diagnostic. No file was removed either way.</returns>
    public static string? TryWrite(
        string stateRoot,
        string targetPath,
        Guid appInstanceId,
        string appInstanceKey,
        string hostKey,
        IReadOnlyList<AppliedOverlayFileEntry> files)
    {
        if (string.IsNullOrWhiteSpace(stateRoot))
        {
            return "Applied overlay file inventory could not be written because HostAgent:LocalArtifactCacheRoot is not configured; overlay-owned file removal will stay disabled until it is configured. No file was removed.";
        }

        if (files.Count > MaxEntries)
        {
            return $"Applied overlay file inventory could not be written because the desired overlay set has {files.Count} files, above the {MaxEntries}-file limit; overlay-owned file removal is disabled for this cycle. Reduce the number of overlay-owned whole files. No file was removed.";
        }

        foreach (var file in files)
        {
            if (file is null
                || string.IsNullOrWhiteSpace(file.RelativePath)
                || file.RelativePath.Length > MaxRelativePathLength
                || file.ContentLength < 0
                || !IsSha256Hex(file.ContentSha256))
            {
                return "Applied overlay file inventory could not be written because a desired overlay file entry is invalid or its relative path exceeds the "
                    + MaxRelativePathLength + "-character limit; overlay-owned file removal is disabled for this cycle. No file was removed.";
            }
        }

        var path = GetTrustedPath(stateRoot, targetPath, appInstanceId);
        var inventory = new AppliedOverlayFileInventory
        {
            SchemaName = Schema,
            AppInstanceId = appInstanceId,
            AppInstanceKey = appInstanceKey,
            HostKey = hostKey,
            TargetPath = NormalizeTargetPath(targetPath),
            RecordedUtc = DateTimeOffset.UtcNow,
            Files = files
                .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        string json;
        try
        {
            json = JsonSerializer.Serialize(inventory, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return $"Applied overlay file inventory at '{path}' could not be serialized and was not written; overlay-owned file removal is disabled for this cycle. No file was removed.";
        }

        if (Encoding.UTF8.GetByteCount(json) > MaxInventoryBytes)
        {
            return $"Applied overlay file inventory could not be written because it exceeds the {MaxInventoryBytes}-byte limit; overlay-owned file removal is disabled for this cycle. Reduce the number of overlay-owned whole files. No file was removed.";
        }

        // Atomic replace: a crash mid-write must not leave torn JSON that disables
        // reconciliation indefinitely. Write a same-directory temp file, then move it over.
        // Every I/O failure here (planted directory at the leaf, reparse-point state root,
        // ACL denial) degrades to a warning: an app-pool identity must not be able to block
        // every future deployment by planting one directory (F3).
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            OmpReparsePointGuard.PrepareOwnedFileForWrite(path, Path.GetFullPath(stateRoot.Trim()), "Applied overlay file inventory");
            if (Directory.Exists(path))
            {
                return $"Applied overlay file inventory at '{path}' could not be written because a directory occupies the inventory path; overlay-owned file removal is disabled for this cycle. Remove that directory, then let a full deployment recreate the inventory. No file was removed.";
            }

            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Applied overlay file inventory at '{path}' could not be written ({ex.GetType().Name}); overlay-owned file removal is disabled for this cycle and the deployment continues without it. Check the HostAgent state directory permissions. No file was removed.";
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
                // Best-effort temp cleanup; the write result stands.
            }
        }
    }

    private static bool HasExactPropertyIdentity(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                names.Add(property.Name);
            }

            if (!names.Contains("SchemaName") || !names.Contains("AppInstanceId") || !names.Contains("Files"))
            {
                return false;
            }

            if (root.GetProperty("Files").ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in root.GetProperty("Files").EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue; // IsValid rejects null/non-object-shaped entries below.
                    }

                    var entryNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in entry.EnumerateObject())
                    {
                        entryNames.Add(property.Name);
                    }

                    if (!entryNames.Contains("RelativePath") || !entryNames.Contains("ContentSha256"))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string UnreadableDiagnostic(string path)
        => $"Applied overlay file inventory at '{path}' could not be read and was not used; overlay-owned file removal was skipped for this cycle. Delete the inventory file if it is corrupt, then let a full deployment recreate it. No file was removed.";

    private static bool IsValid(AppliedOverlayFileInventory inventory)
    {
        // System.Text.Json assigns an explicit JSON null over the [] initializer, and a null
        // list element is equally possible; both must be rejected here, before any caller
        // enumerates Files (a NullReferenceException is not a recordable deployment fault).
        if (inventory.Files is null || inventory.Files.Count > MaxEntries)
        {
            return false;
        }

        foreach (var entry in inventory.Files)
        {
            if (entry is null
                || string.IsNullOrWhiteSpace(entry.RelativePath)
                || entry.RelativePath.Length > MaxRelativePathLength
                || entry.ContentLength < 0
                || !IsSha256Hex(entry.ContentSha256))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSha256Hex(string? value)
    {
        if (value is not { Length: 64 })
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class AppliedOverlayFileEntry
{
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>Byte length of the rendered content, used to reject a mismatched file before hashing it.</summary>
    public long ContentLength { get; init; }

    public string ContentSha256 { get; init; } = string.Empty;
}
