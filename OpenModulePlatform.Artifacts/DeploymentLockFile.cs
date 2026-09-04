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

    /// <summary>
    /// How long <see cref="TryRenewExclusiveAsync"/> and <see cref="WriteAsync"/> keep
    /// retrying when the lock file is held open exclusively by the other side's renewal.
    /// A renewal holds its handle for a single read-compare-write, so ten attempts at
    /// 50 ms span it comfortably without turning a real I/O failure into a hang.
    /// </summary>
    private const int MaxSharingViolationAttempts = 10;

    private static readonly TimeSpan SharingViolationRetryDelay = TimeSpan.FromMilliseconds(50);

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

            // File.Move(overwrite: true) onto a target that is open with FileShare.None
            // -- an atomic renewal holding its read-verify-write handle, for example --
            // fails with a sharing violation (IOException) or, for the replace-existing
            // variant, an access denial (UnauthorizedAccessException). That state lasts
            // milliseconds, so retry it briefly instead of failing the write; a persistent
            // I/O problem still surfaces once the bounded run of attempts is spent.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException)
                    && attempt < MaxSharingViolationAttempts)
                {
                    await Task.Delay(SharingViolationRetryDelay, ct);
                }
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Atomically reads the lock file, verifies it belongs to <paramref name="expectedLockId"/>
    /// and writes the renewed document, all inside one exclusive file handle.
    /// </summary>
    /// <remarks>
    /// The read-then-write pair this replaces had the ownership check and the overwrite as
    /// two separate operations: a foreign claim that landed between them was silently
    /// overwritten, and the renewal that did it went on believing it still held the lock.
    /// Opening the file with <see cref="FileShare.None"/> and doing the read, the LockId
    /// comparison and the write without letting the handle go closes that window -- a
    /// competing claimant either arrives before the open (and is seen, so the result is
    /// <see cref="DeploymentLockRenewalResult.Lost"/>) or is blocked until the renewed
    /// document is on disk.
    ///
    /// This method never throws for the lock file's own I/O problems; like
    /// <see cref="ReadStatus"/> it fails closed, reporting them as
    /// <see cref="DeploymentLockRenewalResult.Indeterminate"/> so the caller can apply its
    /// bounded tolerance instead of ending a lease on one transient fault.
    /// </remarks>
    public static async Task<DeploymentLockRenewalOutcome> TryRenewExclusiveAsync(
        string applicationRoot,
        string expectedLockId,
        Func<DeploymentLockDocument, DeploymentLockDocument> renew,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(renew);

        var path = GetPath(applicationRoot);
        if (!File.Exists(path))
        {
            return new DeploymentLockRenewalOutcome(DeploymentLockRenewalResult.NotFound, null, null);
        }

        // Same fail-closed branch as ReadStatus: never open a planted link, and never
        // delete it either -- renewal proves ownership, it does not repair the root.
        if (OmpReparsePointGuard.IsReparsePoint(path))
        {
            return new DeploymentLockRenewalOutcome(
                DeploymentLockRenewalResult.Indeterminate,
                null,
                "Deployment lock file is a reparse point (junction/symlink) and was not read.");
        }

        FileStream? exclusiveHandle;
        try
        {
            // Validates the directories above the file before writing through them, exactly
            // as WriteAsync does. The leaf itself was checked just above and is left alone.
            OmpReparsePointGuard.PrepareOwnedFileForWrite(path, applicationRoot, "Deployment lock file");

            exclusiveHandle = await OpenExclusiveWithRetryAsync(path, ct);
        }
        catch (FileNotFoundException)
        {
            // The file vanished between the existence check and the open: nobody holds
            // the lock, so the caller re-asserts it through the atomic claim.
            return new DeploymentLockRenewalOutcome(DeploymentLockRenewalResult.NotFound, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DeploymentLockRenewalOutcome(
                DeploymentLockRenewalResult.Indeterminate,
                null,
                $"Deployment lock file could not be opened exclusively: {ex.Message}");
        }

        if (exclusiveHandle is null)
        {
            return new DeploymentLockRenewalOutcome(
                DeploymentLockRenewalResult.Indeterminate,
                null,
                "Deployment lock file stayed exclusively locked by another process.");
        }

        await using (var stream = exclusiveHandle)
        {
            // Same zero-byte rule as ReadStatus: an empty file is the residue of an
            // interrupted claim or renewal and can never be a valid claim, so report it
            // as absent and let the caller re-assert through the atomic claim path.
            if (stream.Length == 0)
            {
                return new DeploymentLockRenewalOutcome(DeploymentLockRenewalResult.NotFound, null, null);
            }

            DeploymentLockDocument? document;
            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                var json = await reader.ReadToEndAsync(ct);
                document = JsonSerializer.Deserialize<DeploymentLockDocument>(json, JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                return new DeploymentLockRenewalOutcome(
                    DeploymentLockRenewalResult.Indeterminate,
                    null,
                    $"Deployment lock file could not be read: {ex.Message}");
            }

            if (document is null)
            {
                return new DeploymentLockRenewalOutcome(
                    DeploymentLockRenewalResult.Indeterminate,
                    null,
                    "Deployment lock file exists but did not contain a valid document.");
            }

            // The comparison happens while this handle still holds the file exclusively,
            // so the document just read is provably still the document on disk.
            if (!string.Equals(document.LockId, expectedLockId, StringComparison.Ordinal))
            {
                return new DeploymentLockRenewalOutcome(DeploymentLockRenewalResult.Lost, document, null);
            }

            var renewed = renew(document);
            var renewedJson = JsonSerializer.Serialize(renewed, JsonOptions);

            // Cancellation is honoured only up to here. Once SetLength(0) has run, the
            // write MUST complete: aborting between the truncation and the flush leaves
            // an empty or half-written lock file, which every reader then fails closed
            // on -- a lock held by nobody that blocks every deployment until someone
            // deletes the file by hand. This is a ~500-byte write through an already-open
            // handle and cannot realistically hang, so finishing it without the token is
            // by far the smaller risk (regression fix for the atomic in-place renewal).
            ct.ThrowIfCancellationRequested();
            stream.SetLength(0);
            stream.Position = 0;
            await stream.WriteAsync(Utf8NoBom.GetBytes(renewedJson), CancellationToken.None);
            await stream.FlushAsync(CancellationToken.None);

            return new DeploymentLockRenewalOutcome(DeploymentLockRenewalResult.Renewed, renewed, null);
        }
    }

    /// <summary>
    /// Opens the lock file exclusively, retrying a sharing violation for a bounded moment:
    /// the other side's atomic renewal holds the same kind of handle for milliseconds.
    /// </summary>
    private static async Task<FileStream?> OpenExclusiveWithRetryAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 4096, useAsync: true);
            }
            catch (FileNotFoundException)
            {
                throw;
            }
            catch (DirectoryNotFoundException)
            {
                throw;
            }
            catch (IOException) when (attempt < MaxSharingViolationAttempts)
            {
                await Task.Delay(SharingViolationRetryDelay, ct);
            }
            catch (IOException)
            {
                return null;
            }
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

        // A zero-byte lock file is the residue of an interrupted claim or renewal; it can
        // never be a valid claim, so it reads as "no lock" instead of failing closed
        // forever. A NON-EMPTY but unparseable file still fails closed below -- that
        // distinction is R12-A4's safety net and is deliberately kept.
        if (new FileInfo(path).Length == 0)
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
            // Another claimant got there first -- unless what got there is a zero-byte
            // residue of an interrupted claim or renewal, which can never be a valid
            // claim (ReadStatus treats it as "no lock" for the same reason) and which
            // would otherwise block every deployment until deleted by hand. The takeover
            // opens the residue exclusively, so a claim still being written -- which
            // holds its own FileShare.None handle -- makes the open fail and the race is
            // lost exactly as if the file were a real claim. An IOException raised while
            // the file does NOT exist fails the filter above and propagates as before:
            // a full disk or a vanished share is a real failure, not a lost race.
            var takeover = TryOpenZeroByteResidueForTakeover(path);
            if (takeover is null)
            {
                return false;
            }

            stream = takeover;
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

    /// <summary>
    /// Opens an existing lock file for takeover only while it is provably a zero-byte
    /// residue: the open is exclusive, so a live claim still being written defeats it,
    /// and anything that is not empty -- a complete claim, or a non-empty unparseable
    /// file under R12-A4's fail-closed rule -- is left alone.
    /// </summary>
    private static FileStream? TryOpenZeroByteResidueForTakeover(string path)
    {
        // Never write through a planted link; losing the race is the fail-closed answer.
        if (OmpReparsePointGuard.IsReparsePoint(path))
        {
            return null;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        }
        catch (IOException)
        {
            // Vanished between the existence check and the open, or held exclusively by
            // the claimant whose CreateNew beat ours: either way the race is lost.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (stream.Length != 0)
        {
            // A complete claim landed between the failed CreateNew and this open.
            stream.Dispose();
            return null;
        }

        return stream;
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

/// <summary>
/// What one atomic renewal attempt of the deployment lock established.
/// </summary>
public enum DeploymentLockRenewalResult
{
    /// <summary>The lock file named this lease and the renewed document was written.</summary>
    Renewed,

    /// <summary>The lock file names a different lease. This is the only real loss.</summary>
    Lost,

    /// <summary>The lock file could not be read, so nothing about ownership is known.</summary>
    Indeterminate,

    /// <summary>There is no lock file at all; the caller may re-assert its claim.</summary>
    NotFound
}

/// <summary>
/// The outcome of <see cref="DeploymentLockFile.TryRenewExclusiveAsync"/>: the verdict,
/// the document that was read (or written), and a diagnostic when nothing could be proven.
/// </summary>
public sealed record DeploymentLockRenewalOutcome(
    DeploymentLockRenewalResult Result,
    DeploymentLockDocument? Document,
    string? Diagnostic);
