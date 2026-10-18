using System.Text.Json;
using OpenModulePlatform.Artifacts;

namespace OpenModulePlatform.HostAgent.Runtime.Models;

/// <summary>
/// Persisted inventory of the whole configuration files a config overlay owns for one web app
/// instance. Written after a successful deployment and read on the already-applied fast path so
/// HostAgent can reconcile a file the overlay no longer supplies. Only a relative path recorded
/// here -- and still byte-identical on disk -- is ever removed, so an operator's files, the
/// artifact's files and the built-in appsettings.json are never deleted by this mechanism.
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

    public string SchemaName { get; init; } = Schema;

    public Guid AppInstanceId { get; init; }

    public string AppInstanceKey { get; init; } = string.Empty;

    public string HostKey { get; init; } = string.Empty;

    public DateTimeOffset RecordedUtc { get; init; }

    public List<AppliedOverlayFileEntry> Files { get; init; } = [];

    public static string GetPath(string targetPath)
        => Path.Join(Path.GetFullPath(targetPath), "App_Data", FileName);

    public static bool Exists(string targetPath)
        => File.Exists(GetPath(targetPath));

    /// <summary>
    /// Reads and validates the inventory. A missing file returns null with no diagnostic (there is
    /// simply no history to reconcile); an unreadable, malformed, foreign-schema or reparse-point
    /// file returns null with an actionable diagnostic so the caller can warn instead of guessing.
    /// </summary>
    public static AppliedOverlayFileInventory? TryRead(string targetPath, out string? diagnostic)
    {
        diagnostic = null;
        var path = GetPath(targetPath);
        if (!File.Exists(path))
        {
            return null;
        }

        // App_Data is inside a web root that application-pool identities can write, and this
        // inventory is read and written by HostAgent as LocalSystem. A planted link must never
        // be followed (R8-P2-8); report it instead of treating it as absent. The whole path is
        // checked, not only the leaf: a junction on App_Data itself redirects everything
        // beneath it while the leaf looks ordinary.
        try
        {
            OmpReparsePointGuard.EnsureNoReparsePointInPath(
                path, Path.GetFullPath(targetPath), "Applied overlay file inventory");
        }
        catch (IOException)
        {
            diagnostic = $"Applied overlay file inventory at '{path}' is a reparse point and was not read; overlay-owned file removal was skipped for this cycle. Remove the reparse point or delete the inventory file, then let a full deployment recreate it. No file was removed.";
            return null;
        }

        // Bound the read before allocating it as LocalSystem: the inventory is a small control
        // file, never a payload (same rule as the overlay-file length check, R9-B1).
        try
        {
            if (new FileInfo(path).Length > MaxInventoryBytes)
            {
                diagnostic = $"Applied overlay file inventory at '{path}' exceeds the {MaxInventoryBytes}-byte limit and was not read; overlay-owned file removal was skipped for this cycle. Delete the inventory file, then let a full deployment recreate it. No file was removed.";
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostic = UnreadableDiagnostic(path);
            return null;
        }

        AppliedOverlayFileInventory? inventory;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaxInventoryBytes)
            {
                diagnostic = $"Applied overlay file inventory at '{path}' exceeds the {MaxInventoryBytes}-byte limit and was not read; overlay-owned file removal was skipped for this cycle. Delete the inventory file, then let a full deployment recreate it. No file was removed.";
                return null;
            }

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

        // The schema value comes from an app-pool-writable file; never echo it into a
        // diagnostic that is persisted and surfaced in Portal.
        if (!string.Equals(inventory.SchemaName, Schema, StringComparison.Ordinal))
        {
            diagnostic = $"Applied overlay file inventory at '{path}' has an unsupported schema and was not used; overlay-owned file removal was skipped for this cycle. Delete the inventory file, then let a full deployment recreate it. No file was removed.";
            return null;
        }

        if (!IsValid(inventory))
        {
            diagnostic = $"Applied overlay file inventory at '{path}' is structurally invalid and was not used; overlay-owned file removal was skipped for this cycle. Delete the inventory file, then let a full deployment recreate it. No file was removed.";
            return null;
        }

        return inventory;
    }

    public static void Write(
        string targetPath,
        Guid appInstanceId,
        string appInstanceKey,
        string hostKey,
        IReadOnlyList<AppliedOverlayFileEntry> files)
    {
        var path = GetPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        OmpReparsePointGuard.PrepareOwnedFileForWrite(path, targetPath, "Applied overlay file inventory");
        var inventory = new AppliedOverlayFileInventory
        {
            SchemaName = Schema,
            AppInstanceId = appInstanceId,
            AppInstanceKey = appInstanceKey,
            HostKey = hostKey,
            RecordedUtc = DateTimeOffset.UtcNow,
            Files = files
                .OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
        // Atomic replace: a crash mid-write must not leave torn JSON that disables
        // reconciliation indefinitely. Write a same-directory temp file, then move it over.
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(inventory, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
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
                // Best-effort temp cleanup; the move result stands.
            }
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
