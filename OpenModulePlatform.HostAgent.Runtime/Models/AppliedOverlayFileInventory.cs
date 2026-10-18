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
        // be followed (R8-P2-8); report it instead of treating it as absent.
        if (OmpReparsePointGuard.IsReparsePoint(path))
        {
            diagnostic = "Applied overlay file inventory is a reparse point and was not read.";
            return null;
        }

        AppliedOverlayFileInventory? inventory;
        try
        {
            inventory = JsonSerializer.Deserialize<AppliedOverlayFileInventory>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            diagnostic = "Applied overlay file inventory could not be read; overlay-owned file removal was skipped for this cycle.";
            return null;
        }

        if (inventory is null)
        {
            diagnostic = "Applied overlay file inventory is empty; overlay-owned file removal was skipped for this cycle.";
            return null;
        }

        if (!string.Equals(inventory.SchemaName, Schema, StringComparison.Ordinal))
        {
            diagnostic = $"Applied overlay file inventory has an unsupported schema '{inventory.SchemaName}'; overlay-owned file removal was skipped for this cycle.";
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
        File.WriteAllText(path, JsonSerializer.Serialize(inventory, JsonOptions));
    }
}

public sealed class AppliedOverlayFileEntry
{
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>Byte length of the rendered content, used to reject a mismatched file before hashing it.</summary>
    public long ContentLength { get; init; }

    public string ContentSha256 { get; init; } = string.Empty;
}
