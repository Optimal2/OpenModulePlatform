namespace OpenModulePlatform.Bootstrapper;

/// <summary>
/// Thrown when a package file would replace a file that already exists at the
/// target path. A package name carries its version, so replacing the file would
/// ship different content under the same name without a trace.
/// </summary>
internal sealed class OutputFileExistsException : IOException
{
    public OutputFileExistsException(string path, Exception? innerException = null)
        : base(
            $"{path} already exists and was not replaced. An existing package is never overwritten; "
                + "build again to get a new version, or choose another version or output file.",
            innerException)
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
/// </remarks>
internal static class NoOverwriteFile
{
    /// <summary>
    /// Writes a new file at <paramref name="path"/> through <paramref name="write"/>.
    /// Throws <see cref="OutputFileExistsException"/> when the path already
    /// exists or another writer claims it first.
    /// </summary>
    public static void Write(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);

        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"'{path}' has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        ClaimName(fullPath);
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

            // Replacing is correct here: the file being replaced is this writer's
            // own empty claim, never someone else's package.
            File.Move(tempPath, fullPath, overwrite: true);
            published = true;
        }
        finally
        {
            TryDelete(tempPath);
            if (!published)
            {
                // Release the claim so a failed build leaves no empty package behind.
                TryDelete(fullPath);
            }
        }
    }

    private static void ClaimName(string fullPath)
    {
        try
        {
            using var claim = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException ex) when (File.Exists(fullPath))
        {
            throw new OutputFileExistsException(fullPath, ex);
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

    private static void TryDelete(string path)
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
            System.Diagnostics.Debug.WriteLine(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}
