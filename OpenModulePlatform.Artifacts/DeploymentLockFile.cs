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

        return Path.Join(
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

        // App_Data sits inside a web root that application-pool identities can write,
        // so both the lock file and the App_Data directory itself are plantable, while
        // this write runs as LocalSystem (R8-P2-8).
        OmpReparsePointGuard.PrepareOwnedFileForWrite(path, applicationRoot, "Deployment lock file");

        var tempPath = Path.Join(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
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

        // Reading through a planted link turns this into an oracle: the caller reports
        // the lock's owner and reason back to the operator, so a symlink at any file
        // LocalSystem can read leaks its first line as a parse failure message, and an
        // arbitrarily large target is read into memory before the JSON parser sees it.
        if (OmpReparsePointGuard.IsReparsePoint(path))
        {
            return DeploymentLockStatus.Locked(
                path,
                null,
                "Deployment lock file is a reparse point (junction/symlink) and was not read.");
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

    /// <summary>
    /// Creates the lock file only if no lock file exists, and reports whether this caller
    /// was the one that created it.
    /// </summary>
    /// <remarks>
    /// <see cref="WriteAsync"/> ends in <c>File.Move(overwrite: true)</c>, which is the
    /// right primitive for renewing a lock you already hold and the wrong one for taking
    /// it: two HostAgents that both read "not locked" would both write, and the second
    /// would silently become the owner of a deployment the first was already running
    /// (R7-D6). <c>FileMode.CreateNew</c> makes the claim itself the atomic step, so
    /// exactly one caller can win.
    ///
    /// A stale lock file must therefore be removed before claiming, which the caller does
    /// after establishing that the existing lock has expired. Two agents racing to clear
    /// the same stale file is harmless: both delete, one creates, the other is told it
    /// lost.
    /// </remarks>
    public static async Task<bool> TryCreateExclusiveAsync(
        string applicationRoot,
        DeploymentLockDocument document,
        CancellationToken ct)
    {
        var path = GetPath(applicationRoot);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Could not resolve deployment lock directory for '{path}'.");
        Directory.CreateDirectory(directory);

        OmpReparsePointGuard.PrepareOwnedFileForWrite(path, applicationRoot, "Deployment lock file");

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another claimant got there first. Any other IOException -- a full disk, a
            // vanished share -- is a real failure and must not be reported as a lost race.
            return false;
        }

        try
        {
            var json = JsonSerializer.Serialize(document, JsonOptions);
            await using var writer = new StreamWriter(stream, Utf8NoBom);
            await writer.WriteAsync(json.AsMemory(), ct);
            await writer.FlushAsync(ct);
        }
        catch
        {
            // The claim succeeded but its contents did not land. Leaving an empty or
            // half-written lock file would block every future deployment of this
            // application until someone deleted it by hand.
            await stream.DisposeAsync();
            TryDelete(path);
            throw;
        }

        return true;
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
        builder.Append(" deployment is skipped because a deployment lock is held. LockId=");
        builder.Append(Document?.LockId ?? "(unknown)");
        builder.Append('.');

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

            builder.Append(" Reason: ");
            builder.Append(string.IsNullOrWhiteSpace(Document.Reason) ? "(unspecified)" : Document.Reason.Trim());
            builder.Append('.');

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
        builder.Append(". The next deployment cycle will retry automatically once the lock is released or expired.");
        return builder.ToString();
    }
}
