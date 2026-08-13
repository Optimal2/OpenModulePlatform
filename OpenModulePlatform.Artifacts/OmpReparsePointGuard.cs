// File: OpenModulePlatform.Artifacts/OmpReparsePointGuard.cs
namespace OpenModulePlatform.Artifacts;

/// <summary>
/// Refuses to read, write or delete through a junction or symlink an unprivileged account could
/// have planted.
/// </summary>
/// <remarks>
/// R8-P2. Seven rounds hardened this one call site at a time -- R2-S8, R3-A1, R5S-A2, R5S-D1,
/// R7-S2 through R7-S5, R7-S11, R7-A5 -- and R8's sweep still found a dozen paths without it. The
/// reason is structural rather than an oversight each time: the guard was written as a private
/// method inside ArtifactZipImportService, so no other file could call it. Every later author who
/// needed the same protection had to notice it existed, find it, and copy it. Most did not.
///
/// It lives here because OpenModulePlatform.Artifacts is the one project HostAgent.Runtime, the
/// Portal and the Bootstrapper all reference, so a fix applied here reaches every writer instead
/// of one of them.
///
/// The checks deliberately swallow a missing path: callers guard a path they are about to create
/// as often as one they are about to open, and "not there yet" is not a violation. An access
/// denial is swallowed for the same reason it always was -- the guard must not turn a permission
/// problem into a different, more confusing failure than the operation would have produced on its
/// own.
/// </remarks>
public static class OmpReparsePointGuard
{
    /// <summary>
    /// Throws when <paramref name="path"/> exists and is a reparse point.
    /// </summary>
    /// <exception cref="IOException">The path is a junction or symlink.</exception>
    public static void EnsureNotReparsePoint(string path, string description)
    {
        if (IsReparsePoint(path))
        {
            throw new IOException(
                $"{description} is a reparse point (junction/symlink): '{path}'. Refusing to use it.");
        }
    }

    /// <summary>
    /// Throws when <paramref name="path"/> or any directory above it up to
    /// <paramref name="stopAtRoot"/> is a reparse point.
    /// </summary>
    /// <remarks>
    /// Checking only the leaf is what made several of the R8-P2 findings exploitable anyway: a
    /// junction planted on an intermediate directory redirects everything beneath it while the
    /// leaf itself looks ordinary. Callers that write into a tree they do not fully own should
    /// use this rather than the single-path check.
    /// </remarks>
    public static void EnsureNoReparsePointInPath(string path, string stopAtRoot, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var root = string.IsNullOrWhiteSpace(stopAtRoot)
            ? null
            : Path.GetFullPath(stopAtRoot).TrimEnd(Path.DirectorySeparatorChar);

        var current = Path.GetFullPath(path);
        for (var depth = 0; depth < 64 && !string.IsNullOrEmpty(current); depth++)
        {
            EnsureNotReparsePoint(current, description);

            if (root is not null
                && string.Equals(
                    current.TrimEnd(Path.DirectorySeparatorChar),
                    root,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            current = parent;
        }
    }

    /// <summary>
    /// Prepares a file that HostAgent owns outright for an overwrite: refuses every directory
    /// above it up to <paramref name="stopAtRoot"/>, and removes the file itself when it turns
    /// out to be a reparse point.
    /// </summary>
    /// <remarks>
    /// R8-P2-8. The deployment control files -- app_offline.htm, the deployment lock and the
    /// runtime stop marker -- all live inside a web root that IIS application-pool identities can
    /// write, and all three are written by HostAgent as LocalSystem. Throwing on a planted link
    /// would be the wrong answer: it hands an unprivileged account a way to block every future
    /// deployment by creating one symlink. These files carry no state worth preserving and no
    /// legitimate reason to be links, so the link is deleted and the real file written in its
    /// place. Deleting a symlink removes the link, never its target.
    ///
    /// The directories above are a different matter and still throw. A junction there means the
    /// deployment target itself is not what the caller believes, which is not something to repair
    /// silently underneath an operator.
    /// </remarks>
    public static void PrepareOwnedFileForWrite(string path, string stopAtRoot, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            EnsureNoReparsePointInPath(parent, stopAtRoot, description);
        }

        if (!IsReparsePoint(fullPath))
        {
            return;
        }

        try
        {
            File.Delete(fullPath);
        }
        catch (UnauthorizedAccessException)
        {
            // A directory junction cannot be removed with File.Delete.
            Directory.Delete(fullPath);
        }
    }

    /// <summary>
    /// True when the path exists and is a junction or symlink. A missing or unreadable path is
    /// not a reparse point as far as callers are concerned.
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException
            or DirectoryNotFoundException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Enumeration options that never descend into a reparse point and never fail on one
    /// unreadable entry.
    /// </summary>
    /// <remarks>
    /// R8-P2-7 and R8-P2-10: enumerating a tree without AttributesToSkip walks straight through a
    /// planted junction, and the caller then copies, hashes or deletes whatever is on the other
    /// side. Handing out one options object stops each caller from assembling its own and
    /// forgetting a flag.
    /// </remarks>
    public static EnumerationOptions RecursiveNoFollow => new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true
    };
}
