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
/// Here the existence decision belongs to the file system itself: the content
/// is written to a temporary file beside the target (opened with
/// <see cref="FileMode.CreateNew"/>, same directory and therefore same volume)
/// and then renamed with <c>File.Move(overwrite: false)</c>, which fails when
/// the target exists at the moment of the rename. A reader never observes a
/// partial package under the final name, and a losing race ends in
/// <see cref="OutputFileExistsException"/> with the target untouched.
/// </remarks>
internal static class NoOverwriteFile
{
    /// <summary>
    /// Writes a new file at <paramref name="path"/> through <paramref name="write"/>.
    /// Throws <see cref="OutputFileExistsException"/> when the path already
    /// exists, including when another writer creates it while this one runs.
    /// </summary>
    public static void Write(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);

        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"'{path}' has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        // Fail before doing the work when the answer is already known; the
        // rename below is what decides a race.
        if (File.Exists(fullPath))
        {
            throw new OutputFileExistsException(fullPath);
        }

        var tempPath = System.IO.Path.Join(
            directory,
            $".{System.IO.Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(tempPath, fullPath, overwrite: false);
            }
            catch (IOException ex) when (File.Exists(fullPath))
            {
                throw new OutputFileExistsException(fullPath, ex);
            }
        }
        finally
        {
            TryDelete(tempPath);
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
