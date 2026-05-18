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

            Directory.CreateDirectory(DeploymentPath.CombineUnderRoot(
                targetDirectory,
                relative,
                "Artifact target directory"));
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
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyFileWithRetry(file, target, cancellationToken);
        }
    }

    private static void DeleteStaleTargetEntries(
        string sourceDirectory,
        string targetDirectory,
        IReadOnlyCollection<string> excludedEntries,
        CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(targetDirectory, "*", SearchOption.AllDirectories))
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

        var directories = Directory.EnumerateDirectories(targetDirectory, "*", SearchOption.AllDirectories)
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
        => ExecuteFileOperationWithRetry(
            () => File.Copy(sourcePath, targetPath, overwrite: true),
            cancellationToken);

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
