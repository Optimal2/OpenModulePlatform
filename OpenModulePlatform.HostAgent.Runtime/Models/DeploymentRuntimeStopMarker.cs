using System.Text.Json;
using OpenModulePlatform.Artifacts;

namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class DeploymentRuntimeStopMarker
{
    private static readonly TimeSpan DefaultExpiry = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string RuntimeKind { get; init; } = string.Empty;

    public string RuntimeName { get; init; } = string.Empty;

    public Guid AppInstanceId { get; init; }

    public string AppInstanceKey { get; init; } = string.Empty;

    public string HostKey { get; init; } = string.Empty;

    public DateTimeOffset RecordedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ExpiresUtc { get; init; } = DateTimeOffset.UtcNow.Add(DefaultExpiry);

    public bool IsExpired(DateTimeOffset now)
        => ExpiresUtc <= now;

    public static string GetPath(string targetPath)
        => Path.Join(targetPath, "App_Data", "omp-runtime-stopped-for-deployment.json");

    public static bool Exists(string targetPath)
        => File.Exists(GetPath(targetPath));

    public static DeploymentRuntimeStopMarker? TryRead(string targetPath)
    {
        var path = GetPath(targetPath);
        if (!File.Exists(path))
        {
            return null;
        }

        // App_Data is inside a web root that application-pool identities can write, and
        // this marker is read and written by HostAgent as LocalSystem (R8-P2-8). An
        // unreadable marker is already treated as absent below, so refusing a link here
        // costs the caller nothing it does not already handle.
        if (OmpReparsePointGuard.IsReparsePoint(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<DeploymentRuntimeStopMarker>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Recovery treats unreadable or malformed markers as absent/stale;
            // the caller logs and repairs the deployment state from database truth.
            return null;
        }
    }

    public static void Write(
        string targetPath,
        string runtimeKind,
        string runtimeName,
        Guid appInstanceId,
        string appInstanceKey,
        string hostKey,
        TimeSpan? expiry = null)
    {
        var path = GetPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        OmpReparsePointGuard.PrepareOwnedFileForWrite(path, targetPath, "Deployment runtime stop marker");
        var now = DateTimeOffset.UtcNow;
        var marker = new DeploymentRuntimeStopMarker
        {
            RuntimeKind = runtimeKind,
            RuntimeName = runtimeName,
            AppInstanceId = appInstanceId,
            AppInstanceKey = appInstanceKey,
            HostKey = hostKey,
            RecordedUtc = now,
            ExpiresUtc = now.Add(expiry ?? DefaultExpiry)
        };
        File.WriteAllText(path, JsonSerializer.Serialize(marker, JsonOptions));
    }

    public static void Delete(string targetPath)
    {
        var path = GetPath(targetPath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
