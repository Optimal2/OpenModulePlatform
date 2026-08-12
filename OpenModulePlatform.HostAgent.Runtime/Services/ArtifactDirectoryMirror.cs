namespace OpenModulePlatform.HostAgent.Runtime.Services;

internal static class ArtifactDirectoryMirror
{
    // app_offline.htm asks ASP.NET Core to shut down, but loaded assemblies can
    // remain locked briefly while the worker process exits.
    private const int FileOperationMaxAttempts = 60;
    private static readonly TimeSpan FileOperationRetryDelay = TimeSpan.FromMilliseconds(500);

    public static void MirrorDirectory(
        string sourceDirectory,
        string targetDirectory,
        IReadOnlyCollection<string> excludedEntries,
        CancellationToken cancellationToken,
        bool deleteStaleTargetEntries = true)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"Provisioned app artifact path was not found: '{sourceDirectory}'.");
        }

        Directory.CreateDirectory(targetDirectory);
        CopySourceFiles(sourceDirectory, targetDirectory, excludedEntries, cancellationToken);
        if (deleteStaleTargetEntries)
        {
            DeleteStaleTargetEntries(sourceDirectory, targetDirectory, excludedEntries, cancellationToken);
        }
    }

    public static void DeleteFileIfExistsWithRetry(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        DeleteFileWithRetry(path, cancellationToken);
    }

    private static void CopySourceFiles(
        string sourceDirectory,
        string targetDirectory,
        IReadOnlyCollection<string> excludedEntries,
        CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            if (IsExcluded(relative, excludedEntries))
            {
                continue;
            }

            var targetSubdirectory = DeploymentPath.CombineUnderRoot(
                targetDirectory,
                relative,
                "Artifact target directory");
            // CombineUnderRoot is purely lexical: a directory junction planted at
            // a mirrored path (e.g. by a compromised app-pool identity that has
            // Modify on the deploy dir) is lexically "under root" but physically
            // points elsewhere, so File.Copy would overwrite files through it as
            // the LocalSystem host agent (R5S-D1). Refuse to mirror through a
            // reparse point.
            ThrowIfReparsePoint(targetSubdirectory);
            Directory.CreateDirectory(targetSubdirectory);
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDirectory, file);
            if (IsExcluded(relative, excludedEntries))
            {
                continue;
            }

            var target = DeploymentPath.CombineUnderRoot(
                targetDirectory,
                relative,
                "Artifact target file path");
            var targetParent = Path.GetDirectoryName(target)!;
            ThrowIfReparsePoint(targetParent);
            ThrowIfReparsePoint(target);
            Directory.CreateDirectory(targetParent);
            CopyFileWithRetry(file, target, cancellationToken);
        }
    }

    private static void DeleteStaleTargetEntries(
        string sourceDirectory,
        string targetDirectory,
        IReadOnlyCollection<string> excludedEntries,
        CancellationToken cancellationToken)
    {
        // Never recurse through a reparse point while pruning stale entries. The
        // stale-delete walk deletes any target file with no matching source; a
        // directory junction planted in the target (lexically "under root", since
        // CombineUnderRoot does not resolve links) would otherwise be followed into
        // its real target — e.g. C:\Windows\System32 — and every file there, having
        // no source counterpart, deleted by the LocalSystem host agent (R5S-D1).
        // AttributesToSkip stops the descent; the junction link itself is left in
        // place (harmless — the mirror no longer traverses it) rather than removed.
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };

        foreach (var file in Directory.EnumerateFiles(targetDirectory, "*", enumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(targetDirectory, file);
            if (IsExcluded(relative, excludedEntries))
            {
                continue;
            }

            var source = DeploymentPath.CombineUnderRoot(
                sourceDirectory,
                relative,
                "Artifact source file path");
            if (!File.Exists(source))
            {
                DeleteFileWithRetry(file, cancellationToken);
            }
        }

        var directories = Directory.EnumerateDirectories(targetDirectory, "*", enumerationOptions)
            .OrderByDescending(path => path.Length)
            .ToList();

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(targetDirectory, directory);
            if (IsExcluded(relative, excludedEntries))
            {
                continue;
            }

            var source = DeploymentPath.CombineUnderRoot(
                sourceDirectory,
                relative,
                "Artifact source directory");
            if (!Directory.Exists(source) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                DeleteDirectoryWithRetry(directory, cancellationToken);
            }
        }
    }

    private static void CopyFileWithRetry(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        // Skip the copy when the target already matches on length and last-write
        // time. The mirror ran every convergence cycle and re-copied every file
        // unconditionally, so a large mirror (e.g. a 2 GB tools folder) re-read and
        // re-wrote its full byte volume every 30 s — hundreds of GB/hour of pure
        // idle disk churn — even when nothing changed (R5-D5). File.Copy preserves
        // LastWriteTimeUtc, so this signature is stable across cycles.
        var source = new FileInfo(sourcePath);
        var target = new FileInfo(targetPath);
        if (target.Exists
            && source.Length == target.Length
            && source.LastWriteTimeUtc == target.LastWriteTimeUtc)
        {
            return;
        }

        ExecuteFileOperationWithRetry(
            () => File.Copy(sourcePath, targetPath, overwrite: true),
            cancellationToken);
    }

    private static void DeleteFileWithRetry(
        string path,
        CancellationToken cancellationToken)
        => ExecuteFileOperationWithRetry(
            () => File.Delete(path),
            cancellationToken);

    private static void DeleteDirectoryWithRetry(
        string path,
        CancellationToken cancellationToken)
        => ExecuteFileOperationWithRetry(
            () => Directory.Delete(path),
            cancellationToken);

    private static void ExecuteFileOperationWithRetry(
        Action operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                operation();
                return;
            }
            catch (IOException) when (attempt < FileOperationMaxAttempts)
            {
                WaitBeforeRetry(cancellationToken);
            }
            catch (UnauthorizedAccessException) when (attempt < FileOperationMaxAttempts)
            {
                WaitBeforeRetry(cancellationToken);
            }
        }
    }

    private static void WaitBeforeRetry(CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(FileOperationRetryDelay))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            // File.GetAttributes covers BOTH files and directories. The previous
            // DirectoryInfo-based check required info.Exists, which is always false
            // for a file, so the file-target guard in CopySourceFiles was a silent
            // no-op and only the parent-directory check did anything (R6-D7). The
            // sibling guard in ArtifactConfigurationFileWriter already got this right.
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
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

    // Throw IOException (not a bespoke type) so the existing deployment failure
    // handlers treat a planted junction as a normal, ret/logged deployment fault
    // rather than letting it escape and crash the host cycle.
    private static void ThrowIfReparsePoint(string path)
    {
        if (IsReparsePoint(path))
        {
            throw new IOException(
                $"Refusing to mirror through a reparse point (junction/symlink) in the deployment target: '{path}'. " +
                "A link here would let the copy escape the artifact target root.");
        }
    }

    private static bool IsExcluded(string relativePath, IReadOnlyCollection<string> excludedEntries)
    {
        if (excludedEntries.Count == 0)
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var firstSegment = normalized.Split('/')[0];
        var fileName = Path.GetFileName(normalized);

        foreach (var entry in excludedEntries)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var pattern = entry.Replace('\\', '/').Trim('/');
            if (pattern.EndsWith("/*", StringComparison.Ordinal))
            {
                pattern = pattern[..^2];
            }

            if (string.Equals(firstSegment, pattern, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, pattern, StringComparison.OrdinalIgnoreCase)
                || MatchesSimpleWildcard(fileName, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesSimpleWildcard(string value, string pattern)
    {
        if (!pattern.Contains('*'))
        {
            return false;
        }

        var parts = pattern.Split('*', 2);
        return value.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)
            && value.EndsWith(parts[1], StringComparison.OrdinalIgnoreCase);
    }
}
