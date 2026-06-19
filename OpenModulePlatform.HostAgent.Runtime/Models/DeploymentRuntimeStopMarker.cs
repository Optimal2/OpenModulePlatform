using System.Text.Json;

namespace OpenModulePlatform.HostAgent.Runtime.Models;

public sealed class DeploymentRuntimeStopMarker
{
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

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<DeploymentRuntimeStopMarker>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void Write(
        string targetPath,
        string runtimeKind,
        string runtimeName,
        Guid appInstanceId,
        string appInstanceKey,
        string hostKey)
    {
        var path = GetPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var marker = new DeploymentRuntimeStopMarker
        {
            RuntimeKind = runtimeKind,
            RuntimeName = runtimeName,
            AppInstanceId = appInstanceId,
            AppInstanceKey = appInstanceKey,
            HostKey = hostKey,
            RecordedUtc = DateTimeOffset.UtcNow
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
