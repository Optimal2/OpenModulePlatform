using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenModulePlatform.Artifacts;

/// <summary>
/// Defines the standard application-local deployment lock file that HostAgent
/// checks before replacing application files or restarting application runtimes.
/// </summary>
public static class DeploymentLockFile
{
    public const string Schema = "OpenModulePlatform.DeploymentLock.v1";
    public const string RelativePath = "App_Data/omp-deployment.lock.json";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string GetPath(string applicationRoot)
    {
        if (string.IsNullOrWhiteSpace(applicationRoot))
        {
            throw new ArgumentException("Application root is required.", nameof(applicationRoot));
        }

        return Path.Combine(
            Path.GetFullPath(applicationRoot.Trim()),
            "App_Data",
            "omp-deployment.lock.json");
    }

    public static DeploymentLockDocument Create(
        string lockId,
        string applicationKey,
        string owner,
        string reason,
        DateTimeOffset nowUtc,
        DateTimeOffset expiresUtc)
        => new()
        {
            Schema = Schema,
            LockId = lockId,
            ApplicationKey = applicationKey,
            Owner = owner,
            Reason = reason,
            MachineName = Environment.MachineName,
            ProcessId = Environment.ProcessId,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
            ExpiresUtc = expiresUtc
        };

    public static async Task WriteAsync(
        string applicationRoot,
        DeploymentLockDocument document,
        CancellationToken ct)
    {
        var path = GetPath(applicationRoot);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Could not resolve deployment lock directory for '{path}'.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(document, JsonOptions);
            await File.WriteAllTextAsync(tempPath, json, Utf8NoBom, ct);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public static DeploymentLockStatus ReadStatus(string applicationRoot, DateTimeOffset nowUtc)
    {
        var path = GetPath(applicationRoot);
        if (!File.Exists(path))
        {
            return DeploymentLockStatus.NotLocked(path);
        }

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var document = JsonSerializer.Deserialize<DeploymentLockDocument>(json, JsonOptions);
            if (document is null)
            {
                return DeploymentLockStatus.Locked(
                    path,
                    null,
                    "Deployment lock file exists but did not contain a valid document.");
            }

            if (!string.Equals(document.Schema, Schema, StringComparison.Ordinal))
            {
                return DeploymentLockStatus.Locked(
                    path,
                    document,
                    $"Deployment lock file uses unsupported schema '{document.Schema}'.");
            }

            if (document.ExpiresUtc <= nowUtc)
            {
                return DeploymentLockStatus.Expired(path, document);
            }

            return DeploymentLockStatus.Locked(path, document, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return DeploymentLockStatus.Locked(
                path,
                null,
                $"Deployment lock file could not be read: {ex.Message}");
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temporary lock file.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temporary lock file.
        }
    }
}

public sealed record DeploymentLockDocument
{
    public string Schema { get; init; } = DeploymentLockFile.Schema;

    public string LockId { get; init; } = string.Empty;

    public string ApplicationKey { get; init; } = string.Empty;

    public string Owner { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string MachineName { get; init; } = string.Empty;

    public int ProcessId { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }

    public DateTimeOffset ExpiresUtc { get; init; }
}

public sealed record DeploymentLockStatus(
    bool IsLocked,
    bool IsExpired,
    string Path,
    DeploymentLockDocument? Document,
    string? Diagnostic)
{
    public static DeploymentLockStatus NotLocked(string path)
        => new(false, false, path, null, null);

    public static DeploymentLockStatus Locked(
        string path,
        DeploymentLockDocument? document,
        string? diagnostic)
        => new(true, false, path, document, diagnostic);

    public static DeploymentLockStatus Expired(string path, DeploymentLockDocument document)
        => new(false, true, path, document, null);

    public string ToDeploymentSkippedMessage(string deploymentKind)
    {
        var builder = new StringBuilder();
        builder.Append(deploymentKind);
        builder.Append(" deployment is temporarily locked.");

        if (Document is not null)
        {
            if (!string.IsNullOrWhiteSpace(Document.ApplicationKey))
            {
                builder.Append(" Application: ");
                builder.Append(Document.ApplicationKey.Trim());
                builder.Append('.');
            }

            if (!string.IsNullOrWhiteSpace(Document.Owner))
            {
                builder.Append(" Owner: ");
                builder.Append(Document.Owner.Trim());
                builder.Append('.');
            }

            if (!string.IsNullOrWhiteSpace(Document.Reason))
            {
                builder.Append(" Reason: ");
                builder.Append(Document.Reason.Trim());
                builder.Append('.');
            }

            builder.Append(" Expires UTC: ");
            builder.Append(Document.ExpiresUtc.UtcDateTime.ToString("u", CultureInfo.InvariantCulture));
            builder.Append('.');
        }

        if (!string.IsNullOrWhiteSpace(Diagnostic))
        {
            builder.Append(' ');
            builder.Append(Diagnostic.Trim());
        }

        builder.Append(" Lock file: ");
        builder.Append(Path);
        return builder.ToString();
    }
}
