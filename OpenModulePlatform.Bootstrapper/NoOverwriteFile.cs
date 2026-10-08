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
/// volume) and renames it over its own claim, so a reader never observes a
/// partial package under the final name -- only, briefly, the empty claim.
///
/// The rename replaces whatever holds the name, so it runs only after the
/// target is verified to still be this writer's claim: empty and the same file
/// (volume serial number and file index) that was created. A claim deleted and
/// recreated by someone else while the content was written is refused instead
/// of replaced. A process killed between claim and rename leaves the empty
/// claim behind; the next build recognises an empty file and explains it
/// instead of reporting a package that does not exist.
/// </remarks>
internal static class NoOverwriteFile
{
    /// <summary>
    /// Writes a new file at <paramref name="path"/> through <paramref name="write"/>.
    /// Throws <see cref="OutputFileExistsException"/> when the path already
    /// exists or another writer claims it first. <paramref name="reportWarning"/>
    /// receives cleanup failures that leave a file behind; without it they go
    /// to <see cref="System.Diagnostics.Trace"/>.
    /// </summary>
    public static void Write(
        string path, Action<Stream> write, Action<string>? reportWarning = null,
        Func<string, FileStream>? createClaim = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);

        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"'{path}' has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var claim = ClaimName(fullPath, createClaim);
        var tempPath = System.IO.Path.Join(
            directory,
            $".{System.IO.Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var published = false;
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            // Replacing is correct only while the target is this writer's own
            // empty claim, never someone else's file.
            if (!IsOwnClaim(fullPath, claim))
            {
                throw new OutputFileExistsException(
                    fullPath,
                    $"{fullPath} was replaced by another file while this package was written, and was not overwritten. "
                        + "An existing package is never overwritten; build again to get a new version.");
            }

            File.Move(tempPath, fullPath, overwrite: true);
            published = true;
        }
        finally
        {
            TryDelete(tempPath, reportWarning, "temporary package file");
            if (!published && IsOwnClaim(fullPath, claim))
            {
                // Release the claim so a failed build leaves no empty package behind.
                TryDelete(fullPath, reportWarning, "empty name reservation");
            }
        }
    }

    private static FileIdentity ClaimName(string fullPath, Func<string, FileStream>? createClaim)
    {
        FileStream claim;
        for (var attempt = 0; ; attempt++)
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
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(fullPath))
            {
                throw ExistingFile(fullPath, ex);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                // CREATE_NEW can report access denied while another writer's
                // rename/delete keeps the name in a delete-pending state.
                // It may be invisible to File.Exists in that window. Retry
                // only the atomic claim; persistent permission errors still
                // propagate and the content callback has not run.
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
            // Without an identity the final rename could not prove it replaces
            // its own claim, so give the name back and fail.
            TryDelete(fullPath, reportWarning: null, "empty name reservation");
            throw new IOException($"The identity of the name reservation {fullPath} could not be read.");
        }

        return identity.Value;
    }

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
    /// True when <paramref name="fullPath"/> is still the empty file this writer
    /// created. Any doubt (missing file, unreadable identity) answers false.
    /// </summary>
    private static bool IsOwnClaim(string fullPath, FileIdentity claim)
    {
        try
        {
            using var target = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return target.Length == 0 && FileIdentity.Of(target.SafeFileHandle) == claim;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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
            + "It holds no package; delete it before building this version again.";
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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            SafeFileHandle hFile,
            out ByHandleFileInformation lpFileInformation);
    }
}
