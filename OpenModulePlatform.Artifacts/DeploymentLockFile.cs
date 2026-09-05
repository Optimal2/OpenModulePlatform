using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

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
    /// bounded tolerance instead of ending a lease on one transient fault. That covers the
    /// write phase too: an I/O failure there is reported, not raised.
    /// </remarks>
    public static Task<DeploymentLockRenewalOutcome> TryRenewExclusiveAsync(
        string applicationRoot,
        string expectedLockId,
        Func<DeploymentLockDocument, DeploymentLockDocument> renew,
        CancellationToken ct)
        => TryRenewExclusiveAsync(applicationRoot, expectedLockId, renew, OpenExclusiveWithRetryAsync, ct);

    /// <summary>
    /// Test seam: same atomic renewal, but the exclusive open is supplied by the caller
    /// so a test can hand in a handle whose write phase fails at an exact point --
    /// before or after the truncation -- which is what decides whether a truncated
    /// residue must be deleted. Production always uses the public overload.
    /// </summary>
    internal static async Task<DeploymentLockRenewalOutcome> TryRenewExclusiveAsync(
        string applicationRoot,
        string expectedLockId,
        Func<DeploymentLockDocument, DeploymentLockDocument> renew,
        Func<string, CancellationToken, Task<FileStream?>> openExclusive,
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

            exclusiveHandle = await openExclusive(path, ct);
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

        var stream = exclusiveHandle;
        try
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
            // write must either complete or be reported: aborting between the truncation
            // and the flush leaves an empty or half-written lock file, which every reader
            // then fails closed on. This is a ~500-byte write through an already-open
            // handle and cannot realistically hang, so finishing it without the token is
            // by far the smaller risk (regression fix for the atomic in-place renewal).
            //
            // An I/O failure in the write phase is not raised either: it is reported as
            // Indeterminate, exactly like a failed open or read, so the lease loop's
            // bounded tolerance decides whether the lease survives the tick. What such a
            // failure leaves behind is handled by TryRewriteHeldContentAsync itself: a
            // failed truncation keeps the intact document (nothing is deleted), and a
            // post-truncation residue is marked for deletion on the close of THIS handle
            // -- the one that just proved ownership -- never by a path-based delete after
            // the handle is gone.
            ct.ThrowIfCancellationRequested();
            var writeFailure = await TryRewriteHeldContentAsync(stream, renewedJson);
            if (writeFailure is not null)
            {
                return new DeploymentLockRenewalOutcome(
                    DeploymentLockRenewalResult.Indeterminate,
                    null,
                    writeFailure);
            }

            return new DeploymentLockRenewalOutcome(DeploymentLockRenewalResult.Renewed, renewed, null);
        }
        finally
        {
            // The ~500-byte write sits in the FileStream buffer until the flush, so a
            // failed flush leaves the buffer dirty and DisposeAsync retries the write --
            // throwing the same I/O error again, out of a method that just reported the
            // failure as Indeterminate. The write phase already spoke for itself; the
            // disposal must not turn that report into an exception.
            try
            {
                await stream.DisposeAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The failed write was already reported, and a truncated residue rides
                // the deletion TryRewriteHeldContentAsync armed on this handle.
            }
        }
    }

    /// <summary>
    /// Overwrites the content of the exclusively held lock file handle with the renewed
    /// document. Returns null on success, or a diagnostic when the write itself failed --
    /// never throws for an I/O error, so the caller can report the attempt as
    /// <see cref="DeploymentLockRenewalResult.Indeterminate"/> instead of dying mid-lease.
    /// </summary>
    /// <remarks>
    /// The two failure points are deliberately separated:
    ///
    /// A failure of the TRUNCATION itself leaves the intact, still-valid document on disk.
    /// Nothing is deleted: the lease's next tick simply retries against it. Deleting there
    /// turned one transient write fault into a lock-less gap of up to a renewal interval,
    /// in which a competitor could claim while this lease still believed it held the lock.
    ///
    /// A failure AFTER the truncation leaves empty or half-written residue, which is
    /// deleted -- but the deletion rides this handle: it is armed with
    /// SetFileInformationByHandle while the handle that proved ownership is still held,
    /// and completed by its close. A path-based delete after the release could land on a
    /// claim that arrived in the microseconds in between, which is exactly the window the
    /// delete-on-close form does not have.
    /// </remarks>
    internal static async Task<string?> TryRewriteHeldContentAsync(Stream stream, string renewedJson)
    {
        try
        {
            stream.SetLength(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Deployment lock file could not be written: {ex.Message}";
        }

        try
        {
            stream.Position = 0;
            await stream.WriteAsync(Utf8NoBom.GetBytes(renewedJson), CancellationToken.None);
            await stream.FlushAsync(CancellationToken.None);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (stream is FileStream fileStream)
            {
                // Best effort: if the arming itself fails, the residue stays and is treated
                // as absent by every reader (zero-byte) or fails closed (R12-A4), and the
                // lease loop's bounded tolerance stops the renewal.
                TryArmDeleteOnClose(fileStream, out _);
            }

            return $"Deployment lock file could not be written: {ex.Message}";
        }
    }

    /// <summary>
    /// Opens the lock file exclusively, retrying a sharing violation for a bounded moment:
    /// the other side's atomic renewal holds the same kind of handle for milliseconds.
    /// A delete-pending file (marked for deletion on close, not yet unlinked) answers
    /// CreateFile with ERROR_ACCESS_DENIED rather than a sharing violation, so a denial
    /// that probes as delete-pending is retried on the same bounded budget. A denial
    /// that does NOT probe as delete-pending is a real permission failure -- wrong
    /// service account, a read-only file, a missing DELETE right -- which does not
    /// clear inside the budget; it is rethrown so the caller reports it as what it is
    /// instead of "locked by another process".
    /// </summary>
    private static async Task<FileStream?> OpenExclusiveWithRetryAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return OpenExclusiveWithDeleteAccess(path);
            }
            catch (FileNotFoundException)
            {
                throw;
            }
            catch (DirectoryNotFoundException)
            {
                throw;
            }
            catch (UnauthorizedAccessException) when (!FileIsDeletePending(path))
            {
                // A real ACL denial, retried and then misreported as a lost race, sent
                // the operator hunting for a competing deployment that does not exist.
                throw;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException)
                && attempt < MaxSharingViolationAttempts)
            {
                await Task.Delay(SharingViolationRetryDelay, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Opens the lock file with full access and no sharing, including DELETE access so the
    /// holder can mark the file for deletion on close
    /// (<c>SetFileInformationByHandle(FileDispositionInfo)</c>) -- the only deletion form
    /// whose effect cannot outlive the ownership the handle proved, because the file is
    /// unlinked by the close of that very handle. While the handle is held no other handle
    /// to the file can be opened at all, so whatever the holder verifies about the content
    /// is still true when it acts on it.
    /// </summary>
    /// <remarks>
    /// This is the one place the platform drops to CreateFile: FileStream cannot express
    /// "DELETE in the desired access". The mapping to the Framework exception shapes keeps
    /// every caller's existing catch filters working unchanged.
    /// </remarks>
    internal static FileStream OpenExclusiveWithDeleteAccess(string path)
    {
        var handle = OpenExclusiveHandleWithDeleteAccess(path);
        try
        {
            return new FileStream(handle, FileAccess.ReadWrite, bufferSize: 4096, isAsync: false);
        }
        catch
        {
            // Never leak the raw handle if the FileStream construction itself fails.
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The raw handle form of <see cref="OpenExclusiveWithDeleteAccess"/>, for tests that
    /// wrap the handle in a FileStream subclass with an injected failure.
    /// </summary>
    internal static SafeFileHandle OpenExclusiveHandleWithDeleteAccess(string path)
    {
        var handle = NativeMethods.CreateFile(
            @"\\?\" + path,
            NativeMethods.GenericRead | NativeMethods.GenericWrite | NativeMethods.Delete,
            dwShareMode: 0,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileAttributeNormal,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw CreateOpenException(path, Marshal.GetLastWin32Error());
        }

        return handle;
    }

    /// <summary>
    /// Distinguishes the two causes of ERROR_ACCESS_DENIED on the lock file, which have
    /// opposite meanings here. A file marked for deletion on close (delete-pending --
    /// a lost race whose unlink lands when the last handle closes) refuses EVERY new
    /// open, including this metadata-only probe with zero desired access. A real
    /// permission failure -- wrong service account, a read-only file, a missing DELETE
    /// right -- still answers it, because the probe requests no data access at all.
    /// The probe is the only distinction available: a delete-pending file cannot be
    /// opened for a query either, so no handle-based query can settle it directly.
    /// A denial even of the probe is treated as delete-pending: an ACL exotic enough
    /// to refuse attribute reads is indistinguishable from it, and the fail-closed
    /// answer is the same.
    /// </summary>
    private static bool FileIsDeletePending(string path)
    {
        using var probe = NativeMethods.CreateFile(
            @"\\?\" + path,
            dwDesiredAccess: 0,
            NativeMethods.FileShareReadWriteDelete,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileAttributeNormal,
            IntPtr.Zero);
        return probe.IsInvalid
            && Marshal.GetLastWin32Error() == NativeMethods.ErrorAccessDenied;
    }

    /// <summary>
    /// Maps a failed CreateFile to the exception shape the managed FileStream open would
    /// have produced, so existing catch filters (FileNotFoundException, IOException for a
    /// sharing violation, UnauthorizedAccessException for a denial) behave identically.
    /// </summary>
    private static Exception CreateOpenException(string path, int error)
        => error switch
        {
            NativeMethods.ErrorFileNotFound
                => new FileNotFoundException($"Could not find deployment lock file '{path}'.", path),
            NativeMethods.ErrorPathNotFound
                => new DirectoryNotFoundException($"Could not find deployment lock directory for '{path}'."),
            NativeMethods.ErrorAccessDenied
                => new UnauthorizedAccessException($"Access to the deployment lock file '{path}' is denied."),
            _ => new IOException(
                $"Could not open deployment lock file '{path}' (Win32 error {error}).",
                unchecked((int)0x80070000 | error))
        };

    /// <summary>
    /// Marks the file an exclusive handle holds for deletion when that handle closes.
    /// Best effort: returns false with a diagnostic instead of throwing, so a failed arming
    /// is reported like any other I/O fault and the residue is left for the zero-byte rules.
    /// Internal so tests can put a file into the delete-pending state directly.
    /// </summary>
    internal static bool TryArmDeleteOnClose(FileStream stream, out string? diagnostic)
    {
        try
        {
            var info = new NativeMethods.FileDispositionInfo { DeleteFile = 1 };
            if (!NativeMethods.SetFileInformationByHandle(
                    stream.SafeFileHandle,
                    NativeMethods.FileDispositionInfoClass,
                    ref info,
                    NativeMethods.FileDispositionInfoSize))
            {
                diagnostic =
                    $"Deployment lock file could not be marked for deletion on close (Win32 error {Marshal.GetLastWin32Error()}).";
                return false;
            }

            diagnostic = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostic = $"Deployment lock file could not be marked for deletion on close: {ex.Message}";
            return false;
        }
    }

    private static class NativeMethods
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint Delete = 0x00010000;
        internal const uint FileShareReadWriteDelete = 0x00000007;
        internal const uint OpenExisting = 3;
        internal const uint CreateNew = 1;
        internal const uint FileAttributeNormal = 0x80;

        internal const int ErrorFileNotFound = 2;
        internal const int ErrorPathNotFound = 3;
        internal const int ErrorAccessDenied = 5;

        internal const int FileDispositionInfoClass = 4;
        internal const int FileDispositionInfoSize = 1;

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileDispositionInfo
        {
            internal byte DeleteFile;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(
            SafeFileHandle hFile,
            int fileInformationClass,
            ref FileDispositionInfo lpFileInformation,
            int dwBufferSize);
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
        //
        // DO NOT REORDER THIS CONDITION. `!info.Exists` must stay on the LEFT of the `||`.
        //
        // The probe sits outside the try block below, so whatever it does is what ReadStatus
        // does. A bare `new FileInfo(path).Length` throws FileNotFoundException once the file is
        // gone (measured), and ReadStatus is contracted to return a status rather than throw --
        // most of its ~15 call sites, including the HostAgent lease loop, both deployment
        // services and the health monitor, call it with no try/catch, so an exception here kills
        // a renewal loop mid-deployment. (DeleteIfOwned is the one caller that does guard it.)
        // The File.Exists check above is no protection: DeleteIfOwned or expiry cleanup on
        // another thread lands in between.
        //
        // TWO mechanisms make this safe, and the order is what binds them together:
        //   * File present at the stat: FileInfo caches it on first property access, so Length
        //     reuses that stat and cannot throw even if the file vanishes a moment later. The
        //     stale length then fails in ReadAllText below, INSIDE the try -- the fail-closed
        //     path R12-A4 intends.
        //   * File already gone at the stat: Exists returns false WITHOUT caching anything
        //     (Refresh throws internally and the cache stays uninitialised), so a later Length
        //     would issue a fresh query and throw. Nothing here saves us -- the `||`
        //     short-circuit does, by never evaluating Length at all.
        //
        // Writing this as `info.Length == 0 || !info.Exists` reads identically and reintroduces
        // the crash for exactly the case the fix exists for. Verified in .NET 10 by an
        // independent review, which is why the warning is a line of code's worth of comment.
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
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
    public static Task<bool> TryCreateExclusiveAsync(
        string applicationRoot,
        DeploymentLockDocument document,
        CancellationToken ct)
        => TryCreateExclusiveAsync(applicationRoot, document, OpenClaimWithDeleteAccess, ct);

    /// <summary>
    /// Test seam: same atomic claim, but the open is supplied by the caller so a test
    /// can hand in a handle whose write phase fails and watch the cleanup ride the
    /// creating handle. Production always uses the public overload.
    /// </summary>
    internal static async Task<bool> TryCreateExclusiveAsync(
        string applicationRoot,
        DeploymentLockDocument document,
        Func<string, uint, FileStream> openClaim,
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
            stream = openClaim(path, NativeMethods.CreateNew);
        }
        catch (Exception ex) when ((ex is IOException
            || (ex is UnauthorizedAccessException && FileIsDeletePending(path)))
            && File.Exists(path))
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
            //
            // The UnauthorizedAccessException half covers ONLY a delete-pending file:
            // marked for deletion on close but not yet unlinked, it answers CreateFile
            // with ERROR_ACCESS_DENIED, and that is a lost race too -- the takeover open
            // fails the same way and this claim returns false instead of throwing out of
            // the acquire path. A denial that does not probe as delete-pending is a real
            // permission failure (wrong service account, read-only file, missing DELETE
            // right): it fails the filter and propagates, so the operator is told
            // "access denied" rather than "another deployment claimed the lock first".
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
            // leaveOpen: the writer is disposed when this block unwinds -- including
            // before the catch below runs -- and the handle must still be open there so
            // the cleanup deletion can be armed on it.
            await using (var writer = new StreamWriter(stream, Utf8NoBom, bufferSize: 4096, leaveOpen: true))
            {
                await writer.WriteAsync(json.AsMemory(), ct);
                await writer.FlushAsync(ct);
            }

            // The writer's flush only empties the WRITER's buffer into the FileStream's;
            // a deferred write failure would otherwise surface for the first time in the
            // disposal below, outside this try, where the catch could no longer arm the
            // deletion on the still-held handle.
            await stream.FlushAsync(ct);
        }
        catch
        {
            // The claim succeeded but its contents did not land. The deletion rides the
            // handle that created the claim -- armed while it is still held, completed by
            // its close -- so it can never remove a claim that landed at the path after
            // the release. If the arming itself fails the cleanup falls back to the
            // compare-and-delete primitive with our own LockId -- still no path-based
            // delete without proven ownership. A residue the primitive cannot prove ours
            // (zero-byte, or half-written past recognition) is left behind: the zero-byte
            // rules treat it as absent and the takeover path claims it, and an
            // unparseable one fails closed -- the safe direction.
            var armed = TryArmDeleteOnClose(stream, out _);
            await stream.DisposeAsync();
            if (!armed)
            {
                await TryDeleteIfOwnedExclusiveAsync(
                    applicationRoot,
                    document.LockId,
                    deletionRequirement: null,
                    CancellationToken.None);
            }

            throw;
        }

        await stream.DisposeAsync();
        return true;
    }

    /// <summary>
    /// Opens the lock file for a claim with no sharing and DELETE access, so a failed
    /// claim write can be cleaned up by marking the file for deletion on the close of
    /// the very handle that created it. <see cref="NativeMethods.CreateNew"/> fails with
    /// an IOException when the file already exists, exactly like FileMode.CreateNew.
    /// </summary>
    internal static FileStream OpenClaimWithDeleteAccess(string path, uint creationDisposition)
    {
        var handle = OpenClaimHandleWithDeleteAccess(path, creationDisposition);
        try
        {
            return new FileStream(handle, FileAccess.ReadWrite, bufferSize: 4096, isAsync: false);
        }
        catch
        {
            // Never leak the raw handle if the FileStream construction itself fails.
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The raw handle form of <see cref="OpenClaimWithDeleteAccess"/>, for tests that
    /// wrap the handle in a FileStream subclass with an injected failure.
    /// </summary>
    internal static SafeFileHandle OpenClaimHandleWithDeleteAccess(string path, uint creationDisposition)
    {
        var handle = NativeMethods.CreateFile(
            @"\\?\" + path,
            NativeMethods.GenericRead | NativeMethods.GenericWrite | NativeMethods.Delete,
            dwShareMode: 0,
            IntPtr.Zero,
            creationDisposition,
            NativeMethods.FileAttributeNormal,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw CreateOpenException(path, Marshal.GetLastWin32Error());
        }

        return handle;
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
            stream = OpenClaimWithDeleteAccess(path, NativeMethods.OpenExisting);
        }
        catch (IOException)
        {
            // Vanished between the existence check and the open, or held exclusively by
            // the claimant whose CreateNew beat ours: either way the race is lost.
            return null;
        }
        catch (UnauthorizedAccessException) when (FileIsDeletePending(path))
        {
            // Delete-pending: the race is lost and the unlink lands with the last close.
            return null;
        }
        // A denial that does not probe as delete-pending is a real permission failure:
        // it propagates so the acquire path reports it as what it is.

        if (stream.Length != 0)
        {
            // A complete claim landed between the failed CreateNew and this open.
            stream.Dispose();
            return null;
        }

        return stream;
    }

    /// <summary>
    /// Atomically reads the lock file, verifies it still belongs to
    /// <paramref name="expectedLockId"/>, and deletes it -- all inside one exclusive file
    /// handle.
    /// </summary>
    /// <remarks>
    /// This is the compare-and-delete primitive that replaces every "read the status,
    /// compare the owner, then File.Delete by path" pair. That pattern's verification and
    /// deletion were two separate operations, and anything could happen between them: two
    /// agents that both verified the same expired lock could each delete the other's fresh
    /// claim, and both ended up believing they owned the deployment. Here the file is
    /// opened with <see cref="FileShare.None"/> (so nothing can be opened, renamed or
    /// deleted behind the handle's back), the LockId comparison runs while the handle is
    /// held, and the deletion is armed on that same handle with
    /// <c>SetFileInformationByHandle(FileDispositionInfo)</c> -- the file is unlinked by
    /// the close of the very handle that proved ownership, so the deletion can never land
    /// on a claim that arrived after the verification.
    ///
    /// <paramref name="deletionRequirement"/> runs inside the handle, after the ownership
    /// comparison: callers clearing an EXPIRED lock pass an expiry check so a lock its
    /// owner renewed in the meantime (same LockId, future expiry) is left alone.
    ///
    /// Like <see cref="TryRenewExclusiveAsync(string, string, Func{DeploymentLockDocument, DeploymentLockDocument}, CancellationToken)"/>
    /// this method never throws for the lock file's own I/O problems; it fails closed and
    /// reports them as <see cref="DeploymentLockDeleteResult.Indeterminate"/> instead.
    /// </remarks>
    public static async Task<DeploymentLockDeleteOutcome> TryDeleteIfOwnedExclusiveAsync(
        string applicationRoot,
        string expectedLockId,
        Func<DeploymentLockDocument, bool>? deletionRequirement,
        CancellationToken ct)
    {
        var path = GetPath(applicationRoot);
        if (!File.Exists(path))
        {
            return new DeploymentLockDeleteOutcome(DeploymentLockDeleteResult.NotFound, null, null);
        }

        // Same fail-closed branch as ReadStatus: never open a planted link, and never
        // delete it either -- deletion proves ownership, it does not repair the root.
        if (OmpReparsePointGuard.IsReparsePoint(path))
        {
            return new DeploymentLockDeleteOutcome(
                DeploymentLockDeleteResult.Indeterminate,
                null,
                "Deployment lock file is a reparse point (junction/symlink) and was not read.");
        }

        FileStream? exclusiveHandle;
        try
        {
            // Validates the directories above the file before acting through them,
            // exactly as the renewal does. The leaf itself was checked just above.
            OmpReparsePointGuard.PrepareOwnedFileForWrite(path, applicationRoot, "Deployment lock file");

            exclusiveHandle = await OpenExclusiveWithRetryAsync(path, ct);
        }
        catch (FileNotFoundException)
        {
            // The file vanished between the existence check and the open: nobody holds
            // the lock, so the caller proceeds through the atomic claim.
            return new DeploymentLockDeleteOutcome(DeploymentLockDeleteResult.NotFound, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DeploymentLockDeleteOutcome(
                DeploymentLockDeleteResult.Indeterminate,
                null,
                $"Deployment lock file could not be opened exclusively: {ex.Message}");
        }

        if (exclusiveHandle is null)
        {
            return new DeploymentLockDeleteOutcome(
                DeploymentLockDeleteResult.Indeterminate,
                null,
                "Deployment lock file stayed exclusively locked by another process.");
        }

        await using (var stream = exclusiveHandle)
        {
            // Same zero-byte rule as ReadStatus and the renewal: residue of an
            // interrupted claim or renewal, treated as absent and left for the takeover
            // path rather than deleted here.
            if (stream.Length == 0)
            {
                return new DeploymentLockDeleteOutcome(DeploymentLockDeleteResult.NotFound, null, null);
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
                return new DeploymentLockDeleteOutcome(
                    DeploymentLockDeleteResult.Indeterminate,
                    null,
                    $"Deployment lock file could not be read: {ex.Message}");
            }

            if (document is null)
            {
                return new DeploymentLockDeleteOutcome(
                    DeploymentLockDeleteResult.Indeterminate,
                    null,
                    "Deployment lock file exists but did not contain a valid document.");
            }

            // The comparison happens while this handle still holds the file exclusively,
            // so the document just read is provably still the document on disk -- and the
            // deletion below is armed on this same handle, so it cannot outlive the proof.
            if (!string.Equals(document.LockId, expectedLockId, StringComparison.Ordinal))
            {
                return new DeploymentLockDeleteOutcome(DeploymentLockDeleteResult.NotOwned, document, null);
            }

            if (deletionRequirement is not null && !deletionRequirement(document))
            {
                return new DeploymentLockDeleteOutcome(DeploymentLockDeleteResult.NotOwned, document, null);
            }

            // Cancellation is honoured only up to here: once the deletion is armed, the
            // close completes it, and claiming otherwise after cancelling would be a lie.
            ct.ThrowIfCancellationRequested();

            if (!TryArmDeleteOnClose(stream, out var armDiagnostic))
            {
                return new DeploymentLockDeleteOutcome(
                    DeploymentLockDeleteResult.Indeterminate,
                    null,
                    armDiagnostic);
            }

            return new DeploymentLockDeleteOutcome(DeploymentLockDeleteResult.Deleted, document, null);
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

/// <summary>
/// What one atomic compare-and-delete attempt of the deployment lock established.
/// </summary>
public enum DeploymentLockDeleteResult
{
    /// <summary>The lock file named the expected owner and was deleted.</summary>
    Deleted,

    /// <summary>
    /// The lock file names a different owner, or no longer meets the caller's deletion
    /// requirement. It was left in place.
    /// </summary>
    NotOwned,

    /// <summary>
    /// The lock file could not be opened or read, so nothing about ownership was proven
    /// and nothing was deleted.
    /// </summary>
    Indeterminate,

    /// <summary>There is no lock file at all.</summary>
    NotFound
}

/// <summary>
/// The outcome of <see cref="DeploymentLockFile.TryDeleteIfOwnedExclusiveAsync"/>: the
/// verdict, the document that was read, and a diagnostic when nothing could be proven.
/// </summary>
public sealed record DeploymentLockDeleteOutcome(
    DeploymentLockDeleteResult Result,
    DeploymentLockDocument? Document,
    string? Diagnostic);
