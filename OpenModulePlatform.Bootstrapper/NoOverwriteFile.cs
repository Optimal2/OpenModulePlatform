using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenModulePlatform.Bootstrapper;

/// <summary>
/// Thrown when a package file would replace a file that already exists at the
/// target path. A package name carries its version, so replacing the file would
/// ship different content under the same name without a trace.
/// </summary>
internal sealed class OutputFileExistsException : IOException
{
    public OutputFileExistsException(string path, Exception? innerException = null)
        : this(
            path,
            $"{path} already exists and was not replaced. An existing package is never overwritten; "
                + "build again to get a new version, or choose another version or output file.",
            innerException)
    {
    }

    public OutputFileExistsException(string path, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>
/// Creates package files that never replace an existing file.
/// </summary>
/// <remarks>
/// The universal package builders used to check <c>File.Exists</c> and then
/// write the target with <c>File.Delete</c> plus <c>ZipFile.Open(Create)</c>.
/// Between the check and the write another build could create the same name
/// (two builds within the same minute get the same automatic version), and the
/// delete then replaced that package silently. Staging copied with
/// <c>File.Copy(overwrite: false)</c>, which refuses correctly, but the refusal
/// surfaced as an uncaught <see cref="IOException"/> instead of a message.
///
/// The name is claimed first: the target is created empty with
/// <see cref="FileMode.CreateNew"/>, which the operating system decides
/// atomically (CREATE_NEW on Windows, O_CREAT|O_EXCL elsewhere), so exactly one
/// of several concurrent writers gets it and the others fail before doing any
/// work. <c>File.Move(overwrite: false)</c> alone is not enough for that: on
/// Unix .NET may implement it as an existence check followed by a rename, and
/// two concurrent writers were measured both succeeding. The owner then writes
/// the content to a temporary file beside the target (same directory, same
/// volume). On Windows the empty claim is removed by a verified, delete-capable
/// handle that denies writes and renames while held. Publication then uses a
/// non-replacing rename. There is briefly no file at the final name; a competing
/// file created in that gap wins and is never overwritten. The gap is kept
/// deliberately: publishing over the still-held claim was measured to be
/// unachievable on Windows. A handle that denies swaps also blocks the atomic
/// replace itself (<c>File.Move(overwrite: true)</c> fails with access denied,
/// and a handle-based <c>FileRenameInfoEx</c> replace fails with access denied,
/// or sharing violation with POSIX semantics), while a hold that permits the
/// replace also permits a foreign delete, so verifying-then-replacing would race
/// again. At most one writer publishes: under contention the claim owner can
/// still fail (for example when another handle on its claim keeps the final
/// rename denied), and then nobody wins. What never depends on scheduling is
/// that no two files are produced and no writer's file is replaced. A refusal
/// is typed <see cref="OutputFileExistsException"/> on both the claim path
/// (the name is taken) and the publish path (the owner's rename found the name
/// taken by a file that won the gap); contention that outlives the bounded
/// <c>ClaimRetryWindow</c>/<c>PublishRetryWindow</c> can still surface as a raw
/// access or IO error. Readers see no partial package. A killed process can
/// leave an empty claim, which the next build explains.
/// This implementation belongs to the Windows bootstrapper and rejects other
/// operating systems before creating even the output directory.
/// </remarks>
internal static class NoOverwriteFile
{
    // Bounded contention windows. A delete-pending name becomes visible or
    // claimable once the competing rename/delete completes, and a
    // scanner-like handle on the claim is released when the scan moves on,
    // both on a millisecond scale. A denial that persists across the whole
    // window without ever resolving is treated as permanent.
    private static readonly TimeSpan ClaimRetryWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PublishRetryWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Writes a new file at <paramref name="path"/> through <paramref name="write"/>.
    /// Throws <see cref="OutputFileExistsException"/> when the path already
    /// exists or another writer claims it first. <paramref name="reportWarning"/>
    /// receives cleanup failures that leave a file behind; without it they go
    /// to <see cref="System.Diagnostics.Trace"/>.
    /// </summary>
    public static void Write(
        string path, Action<Stream> write, Action<string>? reportWarning = null,
        Func<string, FileStream>? createClaim = null, Action? beforePublish = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);
        EnsureSupportedPlatform(OperatingSystem.IsWindows());

        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"'{path}' has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var claim = ClaimName(fullPath, createClaim, reportWarning);
        var tempPath = System.IO.Path.Join(
            directory,
            $".{System.IO.Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var published = false;
        var claimReleased = false;
        var claimNeedsCleanup = true;
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            // A scanner-like handle can deny removal or publication. Retry for
            // a bounded window, but never delete by a previously checked pathname
            // and never use an overwrite rename, even on a retry.
            var publishUntil = DateTime.UtcNow.Add(PublishRetryWindow);
            var ownClaimPending = false;
            for (;;)
            {
                try
                {
                    if (!claimReleased)
                    {
                        if (!ReleaseOwnClaim(fullPath, claim))
                        {
                            claimNeedsCleanup = false;
                            throw ReplacedClaim(fullPath);
                        }
                        claimReleased = true;
                        claimNeedsCleanup = false;
                        // The released claim can be delete-pending: the name stays
                        // visible until the last reader lets go of the file. This
                        // sample is only an optimization; every failed publish
                        // rechecks the current file identity before waiting, because
                        // the own claim can disappear and a foreign file can take
                        // the name after this point.
                        ownClaimPending = File.Exists(fullPath);
                        // Optional scheduling seam, like createClaim: production
                        // callers supply neither. It selects the final race window.
                        beforePublish?.Invoke();
                    }
                    File.Move(tempPath, fullPath, overwrite: false);
                    break;
                }
                catch (OutputFileExistsException) { throw; }
                catch (Exception ex) when (claimReleased
                    && (ex is IOException or UnauthorizedAccessException) && File.Exists(fullPath))
                {
                    // A different identity read through a supported handle proves
                    // foreign replacement. An inaccessible delete-pending name is
                    // ambiguous, never proof of own ownership: it may be retried
                    // inside the bounded window, but a persistent failure gets the
                    // ordinary replacement refusal rather than an own-claim claim.
                    if (ownClaimPending
                        && !IsForeignFileAtReleasedName(fullPath, claim)
                        && DateTime.UtcNow < publishUntil)
                    {
                        Thread.Sleep(20);
                        continue;
                    }

                    throw ReplacedClaim(fullPath, ex);
                }
                catch (Exception ex)
                    when ((ex is IOException or UnauthorizedAccessException) && DateTime.UtcNow < publishUntil)
                {
                    Thread.Sleep(20);
                }
            }

            published = true;
        }
        finally
        {
            TryDelete(tempPath, reportWarning, "temporary package file");
            if (!published && claimNeedsCleanup)
            {
                // Cleanup uses the same locked handle proof as publication, so
                // it cannot delete a foreign file swapped in after a name check.
                try { ReleaseOwnClaim(fullPath, claim); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ReportLeftover(fullPath, "empty name reservation", ex, reportWarning);
                }
            }
        }
    }

    internal static void EnsureSupportedPlatform(bool isWindows)
    {
        if (!isWindows)
        {
            throw new PlatformNotSupportedException("Package file publication requires Windows.");
        }
    }

    private static OutputFileExistsException ReplacedClaim(string path, Exception? innerException = null)
        => new(path, $"{path} was replaced by another file while this package was written, and was not overwritten. "
            + "An existing package is never overwritten; build again to get a new version.", innerException);

    private static FileIdentity ClaimName(
        string fullPath, Func<string, FileStream>? createClaim, Action<string>? reportWarning)
    {
        // A delete-pending name becomes visible or claimable again once the
        // competing rename/delete completes, while a permanent denial never
        // does. Retry the atomic claim for a bounded window: an access denial
        // that resolves, or that ever lets the name become visible, is
        // contention and ends in success or OutputFileExistsException. An
        // access denial that persists across the whole window without the name
        // ever becoming visible or claimable is a permanent permission error
        // and still propagates raw; the content callback has not run. Only
        // UnauthorizedAccessException is retried: delete-pending contention
        // reports access denied, so an IOException on an invisible name is a
        // different, non-transient problem and propagates at once, unless the
        // operating system itself reported the name as taken (see below).
        var retryUntil = DateTime.UtcNow.Add(ClaimRetryWindow);
        FileStream claim;
        for (;;)
        {
            try
            {
                // The optional factory lets tests reproduce Windows rename
                // contention without depending on thread scheduling.
                claim = createClaim is null
                    ? new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                    : createClaim(fullPath);
                break;
            }
            catch (IOException ex) when (IsNameInUseError(ex))
            {
                // The operating system refused the atomic claim because the
                // name is taken. Classify from the error the claim itself
                // returned, not from a later File.Exists re-check: by then
                // the winning writer may have released its empty claim and
                // not yet renamed its package into place, so the name can be
                // momentarily absent and the refusal would otherwise escape
                // as a raw IOException on an invisible name.
                throw ExistingFile(fullPath, ex);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(fullPath))
            {
                throw ExistingFile(fullPath, ex);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < retryUntil)
            {
                // CREATE_NEW can report access denied while another writer's
                // rename/delete keeps the name in a delete-pending state.
                // It may be invisible to File.Exists in that window. Retry
                // only the atomic claim.
                Thread.Sleep(10);
            }
        }

        FileIdentity? identity;
        using (claim)
        {
            identity = FileIdentity.Of(claim.SafeFileHandle);
        }

        if (identity is null)
        {
            // Without identity it is unsafe to delete by name, even on failure.
            var error = new IOException($"The identity of the name reservation {fullPath} could not be read.");
            ReportLeftover(fullPath, "empty name reservation", error, reportWarning);
            throw error;
        }

        return identity.Value;
    }

    /// <summary>
    /// Reports whether the operating system refused a file creation because
    /// the name is taken: <c>ERROR_FILE_EXISTS</c> (80) or
    /// <c>ERROR_ALREADY_EXISTS</c> (183) carried in the low word of an
    /// <see cref="Exception.HResult"/> with the Win32 facility
    /// (<c>0x8007</c> in the high word). Only the error the failed claim
    /// itself returned proves contention; a later <c>File.Exists</c> re-check
    /// can observe a name that is momentarily absent between the winner's
    /// claim release and its final rename.
    /// </summary>
    private static bool IsNameInUseError(IOException ex)
        => (ex.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)
            && (ex.HResult & 0xFFFF) is 80 or 183;

    private static OutputFileExistsException ExistingFile(string fullPath, Exception innerException)
    {
        long length;
        try
        {
            length = new FileInfo(fullPath).Length;
        }
        catch (IOException)
        {
            return new OutputFileExistsException(fullPath, innerException);
        }
        catch (UnauthorizedAccessException)
        {
            return new OutputFileExistsException(fullPath, innerException);
        }

        if (length != 0)
        {
            return new OutputFileExistsException(fullPath, innerException);
        }

        return new OutputFileExistsException(
            fullPath,
            $"{fullPath} already exists and is empty, so it holds no package. Either another build is writing "
                + "this package right now, or an interrupted build left its name reservation behind. If no other "
                + "build is running, delete the empty file and build again; an existing file is never overwritten.",
            innerException);
    }

    /// <summary>
    /// Proves foreign replacement only when a supported handle identifies a
    /// different file. An open or identity read failure remains ambiguous.
    /// </summary>
    private static bool IsForeignFileAtReleasedName(string fullPath, FileIdentity claim)
    {
        const uint readAttributes = 0x00000080;
        const uint openReparsePoint = 0x00200000;
        using var handle = NativeMethods.CreateFile(
            fullPath, readAttributes, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            FileMode.Open, openReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid
            || !NativeMethods.GetFileInformationByHandle(handle, out var info))
        {
            return false;
        }

        var identity = new FileIdentity(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
        return identity != claim;
    }

    /// <summary>
    /// Removes only this writer's empty claim. The handle denies writes/deletes
    /// between the identity/length proof and disposition, closing the cleanup
    /// race too. Missing or foreign files are untouched and return false.
    /// </summary>
    private static bool ReleaseOwnClaim(string fullPath, FileIdentity claim)
    {
        const uint deleteAndReadAttributes = 0x00010000 | 0x00000080;
        const uint openReparsePoint = 0x00200000;
        using var handle = NativeMethods.CreateFile(
            fullPath, deleteAndReadAttributes, FileShare.Read, IntPtr.Zero,
            FileMode.Open, openReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 2 or 3) { return false; }
            throw FileOperationError(fullPath, error);
        }

        if (!NativeMethods.GetFileInformationByHandle(handle, out var info))
        {
            throw FileOperationError(fullPath, Marshal.GetLastPInvokeError());
        }
        var identity = new FileIdentity(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
        if (identity != claim || info.FileSizeHigh != 0 || info.FileSizeLow != 0)
        {
            return false;
        }

        byte deleteFile = 1; // FILE_DISPOSITION_INFO contains one BOOLEAN.
        if (!NativeMethods.SetFileInformationByHandle(handle, 4, ref deleteFile, 1))
        {
            throw FileOperationError(fullPath, Marshal.GetLastPInvokeError());
        }
        return true; // Closing the verified handle releases this file, not a new pathname occupant.
    }

    private static Exception FileOperationError(string path, int error)
    {
        var cause = new Win32Exception(error);
        return error == 5
            ? new UnauthorizedAccessException($"Could not release name reservation {path}: {cause.Message}", cause)
            : new IOException($"Could not release name reservation {path}: {cause.Message}", cause);
    }

    /// <summary>
    /// Copies <paramref name="sourcePath"/> to a new file at
    /// <paramref name="destinationPath"/>. Throws
    /// <see cref="OutputFileExistsException"/> when the destination exists.
    /// </summary>
    /// <remarks>
    /// The copy goes straight to the destination name: the HostAgent import
    /// folder imports every file it finds, so a temporary file there would be
    /// picked up as a package of its own. <c>File.Copy(overwrite: false)</c>
    /// already refuses an existing destination atomically; this only turns
    /// that refusal into the typed, explained exception.
    /// </remarks>
    public static void Copy(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var fullDestination = System.IO.Path.GetFullPath(destinationPath);
        if (File.Exists(fullDestination))
        {
            throw new OutputFileExistsException(fullDestination);
        }

        try
        {
            File.Copy(sourcePath, fullDestination, overwrite: false);
        }
        catch (IOException ex) when (File.Exists(fullDestination))
        {
            throw new OutputFileExistsException(fullDestination, ex);
        }
    }

    private static void TryDelete(string path, Action<string>? reportWarning, string description)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            ReportLeftover(path, description, ex, reportWarning);
        }
        catch (UnauthorizedAccessException ex)
        {
            ReportLeftover(path, description, ex, reportWarning);
        }
    }

    private static void ReportLeftover(string path, string description, Exception ex, Action<string>? reportWarning)
    {
        var message = $"The {description} {path} of a failed package write could not be removed ({ex.Message}). "
            + "Inspect this path before building this version again; never remove another writer's package.";
        if (reportWarning is not null)
        {
            reportWarning(message);
        }
        else
        {
            System.Diagnostics.Trace.TraceWarning(message);
        }
    }

    /// <summary>The NTFS identity of an open file: volume serial number plus file index.</summary>
    private readonly record struct FileIdentity(uint VolumeSerialNumber, uint FileIndexHigh, uint FileIndexLow)
    {
        public static FileIdentity? Of(SafeFileHandle handle)
            => NativeMethods.GetFileInformationByHandle(handle, out var info)
                ? new FileIdentity(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow)
                : null;
    }

    private static class NativeMethods
    {
        // The claim is released through a raw handle instead of File.Delete,
        // because the release has to be proved against the file this writer
        // created, not against whatever holds the name by then. CreateFileW
        // asks for DELETE plus FILE_READ_ATTRIBUTES so the handle can carry
        // the delete disposition and report the identity of the file it
        // actually opened. The share mode allows reading only, so while the
        // handle is held no other writer can write, replace or rename the
        // file, and the identity proof and the delete cannot be separated.
        // FILE_FLAG_OPEN_REPARSE_POINT keeps a link at the path from being
        // followed to some other file.
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
            FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        // The release marks the verified file delete-pending through the file
        // disposition information class while the handle is still open, so the
        // name cannot be taken by another writer between the identity proof
        // and the delete. The file itself disappears when the last handle to
        // it is closed, which is why a reader that still holds the claim keeps
        // the name visible for a while after the release.
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(
            SafeFileHandle file, int informationClass, ref byte information, uint bufferSize);

        [StructLayout(LayoutKind.Sequential)]
        internal struct ByHandleFileInformation
        {
            // FILETIME is two DWORDs with 4-byte alignment; a ulong would add padding.
            internal uint FileAttributes;
            internal uint CreationTimeLow;
            internal uint CreationTimeHigh;
            internal uint LastAccessTimeLow;
            internal uint LastAccessTimeHigh;
            internal uint LastWriteTimeLow;
            internal uint LastWriteTimeHigh;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }

        // CodeQL cs/unmanaged-code: kept - volume and file identity detect replacement of the reserved file.
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", ExactSpelling = true, CharSet = CharSet.None, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            SafeFileHandle hFile,
            out ByHandleFileInformation lpFileInformation);
    }
}
