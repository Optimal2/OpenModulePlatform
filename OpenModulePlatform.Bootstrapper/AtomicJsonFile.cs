using System.Text;
using System.Text.Json;

namespace OpenModulePlatform.Bootstrapper;

/// <summary>
/// Writes a JSON configuration file so that a reader never observes a partial
/// document.
/// </summary>
/// <remarks>
/// Every configuration write in the deploy chain used
/// <c>File.WriteAllTextAsync</c> straight onto the live path. That truncates the
/// existing file first and then streams the new content, so an interruption --
/// a crash, a full disk, a process kill during a refresh -- leaves a truncated
/// or empty configuration file where a valid one used to be. The installer then
/// reads it back and fails in a way that points at configuration rather than at
/// the interrupted write.
///
/// The pattern here is the one already used by
/// <c>OpenModulePlatform.Artifacts/DeploymentLockFile.cs</c> and
/// <c>HostAgentCredentialStoreService.cs</c>: write a temporary file beside the
/// target (same directory, therefore same volume, so the replace is a rename
/// rather than a copy), flush it to disk, read it back to prove it parses, and
/// only then replace the target. A failure at any step leaves the previous file
/// untouched.
/// </remarks>
internal static class AtomicJsonFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Atomically writes <paramref name="content"/> to <paramref name="path"/>.
    /// </summary>
    /// <param name="path">Target file. Its directory is created if missing.</param>
    /// <param name="content">
    /// The JSON document. A trailing newline is written as given by the caller;
    /// this method does not add one, so callers keep their existing formatting.
    /// </param>
    /// <param name="encoding">
    /// Encoding to use. Defaults to UTF-8 without BOM, which is how these files
    /// are authored; System.Text.Json reads both forms.
    /// </param>
    /// <param name="validateJson">
    /// When true (the default) the temporary file is read back and parsed before
    /// it replaces the target. A write that produced something unparseable must
    /// not be allowed to overwrite a working configuration.
    /// </param>
    public static async Task WriteAsync(
        string path,
        string content,
        Encoding? encoding = null,
        bool validateJson = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // The temporary file lives beside the target so the replace is a rename
        // on the same volume. A temp file in %TEMP% could land on another volume,
        // where File.Move degrades to copy-then-delete and stops being atomic.
        var tempPath = Path.Join(
            directory ?? ".",
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream, encoding ?? Utf8NoBom))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                // Push the bytes past the OS cache: without this the rename can
                // land while the content is still only in memory, which is the
                // failure this whole method exists to prevent.
                stream.Flush(flushToDisk: true);
            }

            if (validateJson)
            {
                var written = await File.ReadAllTextAsync(tempPath, cancellationToken);
                using var _ = JsonDocument.Parse(written);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Synchronous form, for call sites that are not async.</summary>
    public static void Write(
        string path,
        string content,
        Encoding? encoding = null,
        bool validateJson = true)
        => WriteAsync(path, content, encoding, validateJson).GetAwaiter().GetResult();

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
